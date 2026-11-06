using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Shnapp.Core;
using Microsoft.UI;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Text;
using Windows.Foundation.Collections;
using Shnapp.VideoEffects;

namespace Shnapp.App.Video;

/// <summary>Turns the project's non-destructive timeline into a Windows media composition.</summary>
internal static class VideoCompositionService
{
    private const int MaximumTextRasterDimension = 4_096;
    private const double MaximumTextRasterPixels = 4_000_000;
    private const int MaximumFadeSamples = 60;
    private const int MaximumTransitionSamples = 120;
    private static readonly AsyncKeyedGate<(Guid Project, Guid Overlay)> TextRasterGates = new();
    private static readonly AsyncKeyedGate<string> CacheFileGates = new();

    internal static async Task<MediaComposition> BuildAsync(
        VideoProject project, VideoProjectStore store, CancellationToken cancellationToken = default,
        (int Width, int Height)? previewSize = null, bool includeImageOverlays = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(store);

        var composition = new MediaComposition();
        var files = new Dictionary<Guid, StorageFile>();
        Dictionary<Guid, VideoAsset> assets = project.Assets.ToDictionary(item => item.Id);

        async Task<StorageFile> FileForAsync(Guid assetId)
        {
            if (files.TryGetValue(assetId, out StorageFile? cached))
            {
                return cached;
            }

            VideoAsset asset = assets[assetId];
            string path = store.ResolveAssetPath(project, asset);
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            files.Add(assetId, file);
            return file;
        }

        TimeSpan cursor = TimeSpan.Zero;
        bool primaryVisible = !project.HiddenLayerIds.Contains(Guid.Empty);
        foreach (VideoSegment segment in project.VideoSegments.OrderBy(item => item.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment.Start > cursor)
            {
                composition.Clips.Add(MediaClip.CreateFromColor(Colors.Black, segment.Start - cursor));
                cursor = segment.Start;
            }

            MediaClip clip;
            if (primaryVisible)
            {
                clip = await MediaClip.CreateFromFileAsync(await FileForAsync(segment.AssetId));
                Trim(clip, segment.SourceIn, segment.SourceOut);
                // Recording audio is stored in independent stems; never double-play embedded sound.
                clip.Volume = 0;
            }
            else clip = MediaClip.CreateFromColor(Colors.Black, segment.Duration);
            composition.Clips.Add(clip);
            cursor += clip.TrimmedDuration;
        }

        if (cursor < project.Duration)
        {
            composition.Clips.Add(MediaClip.CreateFromColor(Colors.Black, project.Duration - cursor));
        }

        foreach (AudioSegment segment in project.AudioSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BackgroundAudioTrack track = await BackgroundAudioTrack.CreateFromFileAsync(
                await FileForAsync(segment.AssetId));
            Trim(track, segment.SourceIn, segment.SourceOut);
            track.Delay = segment.Start;
            track.Volume = segment.Muted ? 0 : segment.Gain;
            composition.BackgroundAudioTracks.Add(track);
        }

        // Clip fades affect the primary video only; image and text layers stay visible.
        // Small black slices keep the native compositor and MP4 renderer in agreement.
        if (primaryVisible && includeImageOverlays && project.VideoSegments.Any(item =>
                item.FadeIn > TimeSpan.Zero || item.FadeOut > TimeSpan.Zero))
        {
            var layer = new MediaOverlayLayer();
            double width = previewSize?.Width ?? project.Width;
            double height = previewSize?.Height ?? project.Height;
            foreach (VideoSegment segment in project.VideoSegments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (segment.FadeIn > TimeSpan.Zero)
                    AddFadeSlices(layer, segment.Start, segment.FadeIn,
                        fadeIn: true, project.FrameRate, width, height);
                if (segment.FadeOut > TimeSpan.Zero)
                    AddFadeSlices(layer, segment.End - segment.FadeOut, segment.FadeOut,
                        fadeIn: false, project.FrameRate, width, height);
            }
            composition.OverlayLayers.Add(layer);
        }

        if (primaryVisible && project.Transitions.Any(item =>
            item.Lane == VideoTransitionLane.Video))
        {
            var layer = new MediaOverlayLayer();
            double width = previewSize?.Width ?? project.Width;
            double height = previewSize?.Height ?? project.Height;
            foreach (VideoTransition transition in project.Transitions.Where(item =>
                item.Lane == VideoTransitionLane.Video))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!VideoTransitionGeometry.TryGetSpan(project, transition,
                    out VideoTransitionSpan span)) continue;
                if (transition.Effect is VideoTransitionEffect.DipToBlack or
                    VideoTransitionEffect.DipToWhite)
                {
                    AddColorTransitionSlices(layer, transition, span,
                        project.FrameRate, width, height);
                }
                else
                {
                    VideoSegment outgoing = project.VideoSegments.First(item =>
                        item.Id == transition.OutgoingId);
                    VideoSegment incoming = project.VideoSegments.First(item =>
                        item.Id == transition.IncomingId);
                    await AddVideoTransitionSlicesAsync(layer, transition, span,
                        outgoing, incoming, project, store, assets, FileForAsync,
                        project.FrameRate,
                        width, height, cancellationToken);
                }
            }
            composition.OverlayLayers.Add(layer);
        }

        int baseLayerCount = composition.OverlayLayers.Count;
        var visualLayers = new List<(Guid Id, MediaOverlayLayer Layer)>();
        if (!project.VideoOverlays.IsDefaultOrEmpty)
            await AddVideoOverlayLayersAsync(composition, project, store, assets,
                FileForAsync, previewSize, visualLayers, cancellationToken);

        if (includeImageOverlays && !project.ImageOverlays.IsDefaultOrEmpty)
        {
            double scaleX = previewSize?.Width / (double)project.Width ?? 1;
            double scaleY = previewSize?.Height / (double)project.Height ?? 1;
            var items = new List<StaticOverlayItem>(project.ImageOverlays.Length);
            foreach (ImageOverlay image in project.ImageOverlays)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Guid layerId = image.LayerId == Guid.Empty ? image.Id : image.LayerId;
                if (project.HiddenLayerIds.Contains(layerId)) continue;
                StorageFile file = await FileForAsync(image.AssetId);
                if (image.SourceCrop is { } crop)
                    file = await EnsureCroppedImageAsync(project, store,
                        image.Id, image.AssetId,
                        file, crop, cancellationToken);
                items.Add(new StaticOverlayItem(image.Id,
                    layerId,
                    image.Start, image.End, image.ZIndex,
                    ScaleBounds(image.Bounds, scaleX, scaleY),
                    file));
            }
            await AddStaticOverlayLayersAsync(composition, project,
                VideoTransitionLane.Image, items, visualLayers, cancellationToken);
        }

        if (includeImageOverlays && !project.TextOverlays.IsDefaultOrEmpty)
        {
            double scaleX = previewSize?.Width / (double)project.Width ?? 1;
            double scaleY = previewSize?.Height / (double)project.Height ?? 1;
            var items = new List<StaticOverlayItem>(project.TextOverlays.Length);
            foreach (TextOverlay text in project.TextOverlays)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Guid layerId = text.LayerId == Guid.Empty ? text.Id : text.LayerId;
                if (project.HiddenLayerIds.Contains(layerId)) continue;
                string path = await EnsureTextOverlayPngAsync(project, text, store, cancellationToken);
                items.Add(new StaticOverlayItem(text.Id,
                    layerId,
                    text.Start, text.End, text.ZIndex,
                    ScaleBounds(text.Bounds, scaleX, scaleY),
                    await StorageFile.GetFileFromPathAsync(path)));
            }
            await AddStaticOverlayLayersAsync(composition, project,
                VideoTransitionLane.Text, items, visualLayers, cancellationToken);
        }

        // Native composition layers are bottom-to-top. Respect the same order
        // the user sees and drags in the timeline, across media types.
        if (visualLayers.Count != 0)
        {
            while (composition.OverlayLayers.Count > baseLayerCount)
                composition.OverlayLayers.RemoveAt(baseLayerCount);
            var byId = visualLayers.ToDictionary(item => item.Id, item => item.Layer);
            foreach (Guid id in VideoProjectTracks.OrderedIds(project).Reverse())
                if (byId.TryGetValue(id, out MediaOverlayLayer? layer))
                    composition.OverlayLayers.Add(layer);
        }

        return composition;
    }

    /// <summary>Returns the black overlay opacity used at a preview playhead position.</summary>
    internal static double GetFadeOpacity(VideoProject project, TimeSpan playhead)
    {
        if (project.HiddenLayerIds.Contains(Guid.Empty)) return 0;
        foreach (VideoSegment segment in project.VideoSegments)
        {
            if (playhead < segment.Start || playhead >= segment.End) continue;
            TimeSpan intoClip = playhead - segment.Start;
            if (segment.FadeIn > TimeSpan.Zero && intoClip < segment.FadeIn)
                return FadeSampleOpacity(intoClip, segment.FadeIn, true, project.FrameRate);
            TimeSpan fadeOutStart = segment.Duration - segment.FadeOut;
            if (segment.FadeOut > TimeSpan.Zero && intoClip >= fadeOutStart)
                return FadeSampleOpacity(intoClip - fadeOutStart, segment.FadeOut,
                    false, project.FrameRate);
            return 0;
        }
        return 0;
    }

    /// <summary>Renders one style/content variant once; preview and MP4 export use the same PNG.</summary>
    internal static async Task<string> EnsureTextOverlayPngAsync(VideoProject project,
        TextOverlay overlay, VideoProjectStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(store);
        cancellationToken.ThrowIfCancellationRequested();
        string projectDirectory = store.GetProjectDirectory(project.Id);
        if (!Directory.Exists(projectDirectory) || IsReparsePoint(projectDirectory))
            throw new IOException("The video project directory is unavailable.");
        string cacheDirectory = Path.Combine(projectDirectory, ".text-cache");
        Directory.CreateDirectory(cacheDirectory);
        if (IsReparsePoint(cacheDirectory))
            throw new IOException("The video text cache cannot be a linked directory.");
        string hash = TextRasterHash(overlay);
        string path = Path.Combine(cacheDirectory, $"{overlay.Id:N}-{hash}.png");
        if (HasCachedFile(path, "The video text cache cannot contain linked files."))
            return path;
        IDisposable gate = await TextRasterGates.EnterAsync(
            (project.Id, overlay.Id), cancellationToken);
        try
        {
            if (HasCachedFile(path, "The video text cache cannot contain linked files."))
                return path;

            double scale = Math.Min(1, Math.Min(
                MaximumTextRasterDimension / Math.Max(overlay.Bounds.Width, overlay.Bounds.Height),
                Math.Sqrt(MaximumTextRasterPixels /
                    (overlay.Bounds.Width * overlay.Bounds.Height))));
            int rasterWidth = Math.Max(1, (int)Math.Ceiling(overlay.Bounds.Width * scale));
            int rasterHeight = Math.Max(1, (int)Math.Ceiling(overlay.Bounds.Height * scale));
            string temporary = Path.Combine(cacheDirectory,
                $".{overlay.Id:N}.{Guid.NewGuid():N}.tmp");
            try
            {
                using var bitmap = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(),
                    rasterWidth, rasterHeight, 96);
                using (CanvasDrawingSession drawing = bitmap.CreateDrawingSession())
                using (var format = new CanvasTextFormat
                {
                    FontFamily = overlay.FontFamily,
                    FontSize = (float)(overlay.FontSize * scale),
                    FontWeight = new FontWeight { Weight = overlay.Bold ? (ushort)700 : (ushort)400 },
                    FontStyle = overlay.Italic ? FontStyle.Italic : FontStyle.Normal,
                    HorizontalAlignment = overlay.Alignment switch
                    {
                        TextOverlayAlignment.Left => CanvasHorizontalAlignment.Left,
                        TextOverlayAlignment.Right => CanvasHorizontalAlignment.Right,
                        _ => CanvasHorizontalAlignment.Center,
                    },
                    VerticalAlignment = CanvasVerticalAlignment.Center,
                    WordWrapping = CanvasWordWrapping.Wrap,
                })
                {
                    drawing.Clear(Colors.Transparent);
                    double border = overlay.BorderColor is null ? 0 : overlay.BorderThickness * scale;
                    double radius = Math.Min(overlay.CornerRadius * scale,
                        Math.Min(rasterWidth, rasterHeight) / 2d);
                    var frame = new Rect(border / 2d, border / 2d,
                        Math.Max(1, rasterWidth - border), Math.Max(1, rasterHeight - border));
                    if (overlay.BackgroundColor is { } background)
                        drawing.FillRoundedRectangle(frame, (float)radius, (float)radius,
                            ParseColor(background));
                    if (border > 0 && overlay.BorderColor is { } outline)
                        drawing.DrawRoundedRectangle(frame, (float)radius, (float)radius,
                            ParseColor(outline), (float)border);
                    double inset = Math.Min((overlay.Padding + overlay.BorderThickness) * scale,
                        Math.Max(0, Math.Min(rasterWidth, rasterHeight) / 2d - 1));
                    var bounds = new Rect(inset, inset,
                        Math.Max(1, rasterWidth - inset * 2),
                        Math.Max(1, rasterHeight - inset * 2));
                    drawing.DrawText(overlay.Text, bounds, ParseColor(overlay.Color), format);
                }
                await bitmap.SaveAsync(temporary, CanvasBitmapFileFormat.Png)
                    .AsTask(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
            }
            return path;
        }
        finally
        {
            gate.Dispose();
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0;

    private static bool HasCachedFile(string path, string linkedFileMessage)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return false;
        if ((file.Attributes & System.IO.FileAttributes.ReparsePoint) != 0)
            throw new IOException(linkedFileMessage);
        return file.Length > 0;
    }

    private static string TextRasterHash(TextOverlay overlay)
    {
        string signature = string.Join('\0', overlay.Text,
            overlay.Bounds.Width.ToString("R", CultureInfo.InvariantCulture),
            overlay.Bounds.Height.ToString("R", CultureInfo.InvariantCulture),
            overlay.FontSize.ToString("R", CultureInfo.InvariantCulture),
            overlay.Color.ToUpperInvariant(), overlay.Alignment.ToString(),
            overlay.FontFamily, overlay.Bold.ToString(), overlay.Italic.ToString(),
            overlay.BackgroundColor?.ToUpperInvariant() ?? string.Empty,
            overlay.Padding.ToString("R", CultureInfo.InvariantCulture),
            overlay.BorderColor?.ToUpperInvariant() ?? string.Empty,
            overlay.BorderThickness.ToString("R", CultureInfo.InvariantCulture),
            overlay.CornerRadius.ToString("R", CultureInfo.InvariantCulture));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(signature));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static Color ParseColor(string hex)
    {
        int offset = hex.Length == 9 ? 2 : 0;
        byte alpha = hex.Length == 9 ? Convert.ToByte(hex.Substring(1, 2), 16) : (byte)255;
        return Color.FromArgb(alpha,
            Convert.ToByte(hex.Substring(1 + offset, 2), 16),
            Convert.ToByte(hex.Substring(3 + offset, 2), 16),
            Convert.ToByte(hex.Substring(5 + offset, 2), 16));
    }

    private sealed record StaticOverlayItem(Guid Id, Guid LayerId, TimeSpan Start,
        TimeSpan End, int ZIndex, Rect Bounds, StorageFile File);

    private static async Task<StorageFile> EnsureCroppedImageAsync(
        VideoProject project, VideoProjectStore store, Guid overlayId, Guid assetId,
        StorageFile source, VideoRect crop, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string projectDirectory = store.GetProjectDirectory(project.Id);
        if (!Directory.Exists(projectDirectory) || IsReparsePoint(projectDirectory))
            throw new IOException("The video project directory is unavailable.");
        string cacheDirectory = Path.Combine(projectDirectory, ".image-crop-cache");
        Directory.CreateDirectory(cacheDirectory);
        if (IsReparsePoint(cacheDirectory))
            throw new IOException("The image crop cache cannot be a linked directory.");

        var sourceInfo = new FileInfo(source.Path);
        string key = FormattableString.Invariant(
            $"{assetId:N}|{sourceInfo.FullName}|{sourceInfo.Length}|{sourceInfo.LastWriteTimeUtc.Ticks}|{crop.X:R}|{crop.Y:R}|{crop.Width:R}|{crop.Height:R}");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        string path = Path.Combine(cacheDirectory, $"{overlayId:N}-{hash[..20]}.png");
        if (HasCachedFile(path, "The image crop cache cannot contain linked files."))
            return await StorageFile.GetFileFromPathAsync(path);
        IDisposable gate = await CacheFileGates.EnterAsync(path, token);
        try
        {
            if (HasCachedFile(path, "The image crop cache cannot contain linked files."))
                return await StorageFile.GetFileFromPathAsync(path);

            string temporary = Path.Combine(cacheDirectory,
                $".{overlayId:N}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var input = await source.OpenReadAsync().AsTask(token))
                {
                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input)
                        .AsTask(token);
                    uint width = decoder.PixelWidth;
                    uint height = decoder.PixelHeight;
                    if (width == 0 || height == 0)
                        throw new InvalidDataException("The image has no pixels to crop.");
                    uint left = (uint)Math.Clamp(Math.Floor(crop.X * width), 0, width - 1);
                    uint top = (uint)Math.Clamp(Math.Floor(crop.Y * height), 0, height - 1);
                    uint right = (uint)Math.Clamp(
                        Math.Ceiling((crop.X + crop.Width) * width), left + 1, width);
                    uint bottom = (uint)Math.Clamp(
                        Math.Ceiling((crop.Y + crop.Height) * height), top + 1, height);
                    uint cropWidth = right - left;
                    uint cropHeight = bottom - top;
                    var transform = new BitmapTransform
                    {
                        Bounds = new BitmapBounds
                        {
                            X = left, Y = top,
                            Width = cropWidth, Height = cropHeight,
                        },
                    };
                    PixelDataProvider pixels = await decoder.GetPixelDataAsync(
                        BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                        transform, ExifOrientationMode.RespectExifOrientation,
                        ColorManagementMode.ColorManageToSRgb).AsTask(token);

                    StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(
                        cacheDirectory).AsTask(token);
                    StorageFile temporaryFile = await folder.CreateFileAsync(
                        Path.GetFileName(temporary), CreationCollisionOption.FailIfExists)
                        .AsTask(token);
                    using (var output = await temporaryFile.OpenAsync(
                        FileAccessMode.ReadWrite).AsTask(token))
                    {
                        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
                            BitmapEncoder.PngEncoderId, output).AsTask(token);
                        encoder.SetPixelData(BitmapPixelFormat.Bgra8,
                            BitmapAlphaMode.Premultiplied, cropWidth, cropHeight,
                            96, 96, pixels.DetachPixelData());
                        await encoder.FlushAsync().AsTask(token);
                    }
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
            }
            return await StorageFile.GetFileFromPathAsync(path);
        }
        finally
        {
            gate.Dispose();
        }
    }

    private static async Task<StorageFile> EnsureCroppedVideoAsync(
        VideoProject project, VideoProjectStore store, VideoOverlay overlay,
        StorageFile source, VideoRect crop, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string projectDirectory = store.GetProjectDirectory(project.Id);
        if (!Directory.Exists(projectDirectory) || IsReparsePoint(projectDirectory))
            throw new IOException("The video project directory is unavailable.");
        string cacheDirectory = Path.Combine(projectDirectory, ".video-crop-cache");
        Directory.CreateDirectory(cacheDirectory);
        if (IsReparsePoint(cacheDirectory))
            throw new IOException("The video crop cache cannot be a linked directory.");

        var sourceInfo = new FileInfo(source.Path);
        string key = FormattableString.Invariant(
            $"{overlay.AssetId:N}|{sourceInfo.FullName}|{sourceInfo.Length}|{sourceInfo.LastWriteTimeUtc.Ticks}|{crop.X:R}|{crop.Y:R}|{crop.Width:R}|{crop.Height:R}");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        string path = Path.Combine(cacheDirectory, $"{hash[..24]}.mp4");
        if (HasCachedFile(path, "The video crop cache cannot contain linked files."))
            return await StorageFile.GetFileFromPathAsync(path);
        IDisposable gate = await CacheFileGates.EnterAsync(path, token);
        try
        {
            if (HasCachedFile(path, "The video crop cache cannot contain linked files."))
                return await StorageFile.GetFileFromPathAsync(path);

            string temporary = Path.Combine(cacheDirectory,
                $".{Guid.NewGuid():N}.mp4");
            try
            {
                MediaClip original = await MediaClip.CreateFromFileAsync(source);
                VideoEncodingProperties format = original.GetVideoEncodingProperties();
                Rect cropBounds = SourceCropRectangle(format, crop);
                uint outputWidth = checked((uint)Math.Max(2,
                    Math.Ceiling(cropBounds.Width / 2) * 2));
                uint outputHeight = checked((uint)Math.Max(2,
                    Math.Ceiling(cropBounds.Height / 2) * 2));
                var effect = new VideoTransformEffectDefinition
                {
                    CropRectangle = cropBounds,
                    OutputSize = new Size(outputWidth, outputHeight),
                };
                var transcoder = new MediaTranscoder
                {
                    HardwareAccelerationEnabled = true,
                    AlwaysReencode = true,
                };
                transcoder.AddVideoEffect(effect.ActivatableClassId,
                    true, effect.Properties);
                MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(
                    VideoEncodingQuality.HD1080p);
                profile.Video.Width = outputWidth;
                profile.Video.Height = outputHeight;
                profile.Video.FrameRate.Numerator = format.FrameRate.Numerator > 0
                    ? format.FrameRate.Numerator : checked((uint)project.FrameRate);
                profile.Video.FrameRate.Denominator = format.FrameRate.Denominator > 0
                    ? format.FrameRate.Denominator : 1;
                profile.Video.Bitrate = checked((uint)Math.Clamp(
                    format.Bitrate > 0 ?
                        (long)((double)format.Bitrate * outputWidth / format.Width *
                            outputHeight / format.Height) :
                        (long)outputWidth * outputHeight * project.FrameRate / 4,
                    2_000_000, 60_000_000));
                // Imported video sound lives on separate timeline tracks.
                profile.Audio = null;

                StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(
                    cacheDirectory).AsTask(token);
                StorageFile target = await folder.CreateFileAsync(
                    Path.GetFileName(temporary), CreationCollisionOption.FailIfExists)
                    .AsTask(token);
                PrepareTranscodeResult prepared = await transcoder.PrepareFileTranscodeAsync(
                    source, target, profile).AsTask(token);
                if (!prepared.CanTranscode)
                    throw new InvalidOperationException(
                        $"Windows could not crop this video ({prepared.FailureReason}).");
                await prepared.TranscodeAsync().AsTask(token);
                token.ThrowIfCancellationRequested();
                if (new FileInfo(temporary).Length == 0)
                    throw new IOException("The cropped video cache is empty.");
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
            }
            return await StorageFile.GetFileFromPathAsync(path);
        }
        finally
        {
            gate.Dispose();
        }
    }

    private static Rect ScaleBounds(VideoRect bounds, double scaleX, double scaleY) =>
        new(bounds.X * scaleX, bounds.Y * scaleY,
            bounds.Width * scaleX, bounds.Height * scaleY);

    private static Rect SourceCropRectangle(VideoEncodingProperties format,
        VideoRect crop)
    {
        if (format.Width == 0 || format.Height == 0)
            throw new InvalidDataException("The media has no video dimensions to crop.");
        double width = format.Width;
        double height = format.Height;
        double left = Math.Clamp(Math.Floor(crop.X * width), 0, width - 1);
        double top = Math.Clamp(Math.Floor(crop.Y * height), 0, height - 1);
        double right = Math.Clamp(Math.Ceiling((crop.X + crop.Width) * width),
            left + 1, width);
        double bottom = Math.Clamp(Math.Ceiling((crop.Y + crop.Height) * height),
            top + 1, height);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static async Task AddVideoOverlayLayersAsync(MediaComposition composition,
        VideoProject project, VideoProjectStore store,
        IReadOnlyDictionary<Guid, VideoAsset> assets,
        Func<Guid, Task<StorageFile>> fileForAsync, (int Width, int Height)? previewSize,
        List<(Guid Id, MediaOverlayLayer Layer)> visualLayers,
        CancellationToken token)
    {
        double scaleX = previewSize?.Width / (double)project.Width ?? 1;
        double scaleY = previewSize?.Height / (double)project.Height ?? 1;
        var croppedSources = new Dictionary<Guid, StorageFile>();
        async Task<StorageFile> OverlayFileForAsync(VideoOverlay overlay)
        {
            // Shaped video uses its GPU effect for crop and mask in one pass.
            // Avoid transcoding an entire camera source merely for a 1:1 preset.
            if (overlay.Shape != VideoOverlayShape.Rectangle)
                return await fileForAsync(overlay.AssetId);
            if (overlay.SourceCrop is not { } crop)
                return await fileForAsync(overlay.AssetId);
            if (croppedSources.TryGetValue(overlay.Id, out StorageFile? cached))
                return cached;
            StorageFile source = await EnsureCroppedVideoAsync(project, store,
                overlay, await fileForAsync(overlay.AssetId), crop, token);
            croppedSources.Add(overlay.Id, source);
            return source;
        }
        foreach (IGrouping<Guid, VideoOverlay> group in project.VideoOverlays
            .Where(item => !project.HiddenLayerIds.Contains(
                item.LayerId == Guid.Empty ? item.Id : item.LayerId))
            .GroupBy(item => item.LayerId == Guid.Empty ? item.Id : item.LayerId)
            .OrderBy(group => group.Min(item => item.ZIndex)))
        {
            token.ThrowIfCancellationRequested();
            var layer = new MediaOverlayLayer();
            Dictionary<Guid, VideoOverlay> byId = group.ToDictionary(item => item.Id);
            var transitions = new List<(VideoTransition Transition, VideoTransitionSpan Span)>();
            foreach (VideoTransition item in project.Transitions.Where(item =>
                item.Lane == VideoTransitionLane.VideoOverlay &&
                byId.ContainsKey(item.OutgoingId) &&
                byId.ContainsKey(item.IncomingId)))
            {
                if (VideoTransitionGeometry.TryGetSpan(project, item,
                    out VideoTransitionSpan span))
                    transitions.Add((item, span));
            }
            foreach (VideoOverlay overlay in group.OrderBy(item => item.Start))
            {
                TimeSpan bodyStart = overlay.Start;
                TimeSpan bodyEnd = overlay.End;
                foreach ((VideoTransition transition, VideoTransitionSpan span) in transitions)
                {
                    if (transition.IncomingId == overlay.Id && span.End > bodyStart)
                        bodyStart = span.End;
                    if (transition.OutgoingId == overlay.Id && span.Start < bodyEnd)
                        bodyEnd = span.Start;
                }
                if (bodyEnd > bodyStart)
                    await AddVideoOverlaySourceAsync(layer, overlay,
                        assets[overlay.AssetId], OverlayFileForAsync, null, bodyStart,
                        bodyEnd - bodyStart, ScaleBounds(overlay.Bounds, scaleX, scaleY),
                        1, token);
            }
            foreach ((VideoTransition transition, VideoTransitionSpan span) in transitions)
                await AddVideoOverlayTransitionSlicesAsync(layer, project, store, assets,
                    OverlayFileForAsync, byId[transition.OutgoingId],
                    byId[transition.IncomingId], transition, span, scaleX, scaleY, token);
            composition.OverlayLayers.Add(layer);
            visualLayers.Add((group.Key, layer));
        }
    }

    private static async Task AddVideoOverlayTransitionSlicesAsync(
        MediaOverlayLayer layer, VideoProject project, VideoProjectStore store,
        IReadOnlyDictionary<Guid, VideoAsset> assets,
        Func<VideoOverlay, Task<StorageFile>> overlayFileForAsync,
        VideoOverlay outgoing, VideoOverlay incoming, VideoTransition transition,
        VideoTransitionSpan span, double scaleX, double scaleY, CancellationToken token)
    {
        int count = TransitionSampleCount(transition.Duration, project.FrameRate);
        StorageFile? outgoingHold = null;
        StorageFile? incomingHold = null;
        if (outgoing.SourceOut + (span.End - span.Cut) > assets[outgoing.AssetId].Duration)
        {
            StorageFile file = await overlayFileForAsync(outgoing);
            VideoEncodingProperties format = (await MediaClip.CreateFromFileAsync(file))
                .GetVideoEncodingProperties();
            outgoingHold = await EnsureVideoEdgeFrameAsync(project, store,
                new VideoSegment(outgoing.Id, outgoing.AssetId, outgoing.Start,
                    outgoing.SourceIn, outgoing.SourceOut), file, outgoing: true,
                Math.Max(1, (double)format.Width), Math.Max(1, (double)format.Height), token);
        }
        if (incoming.SourceIn < span.Cut - span.Start)
        {
            StorageFile file = await overlayFileForAsync(incoming);
            VideoEncodingProperties format = (await MediaClip.CreateFromFileAsync(file))
                .GetVideoEncodingProperties();
            incomingHold = await EnsureVideoEdgeFrameAsync(project, store,
                new VideoSegment(incoming.Id, incoming.AssetId, incoming.Start,
                    incoming.SourceIn, incoming.SourceOut), file, outgoing: false,
                Math.Max(1, (double)format.Width), Math.Max(1, (double)format.Height), token);
        }
        Rect outgoingBounds = ScaleBounds(outgoing.Bounds, scaleX, scaleY);
        Rect incomingBounds = ScaleBounds(incoming.Bounds, scaleX, scaleY);
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            TimeSpan start = SliceTime(span.Start, transition.Duration, index, count);
            TimeSpan end = SliceTime(span.Start, transition.Duration, index + 1, count);
            if (end <= start) continue;
            double progress = (index + .5) / count;
            VideoTransitionSample sample = VideoTransitionGeometry.Sample(
                transition.Effect, progress);
            if (sample.BackdropColor is { } backdrop)
            {
                Rect area = Union(outgoingBounds, incomingBounds);
                MediaClip color = MediaClip.CreateFromColor(ParseColor(backdrop), end - start);
                layer.Overlays.Add(new MediaOverlay(color)
                {
                    Delay = start,
                    Position = area,
                    Opacity = 1 - Math.Abs(progress * 2 - 1),
                    AudioEnabled = false,
                });
            }
            await AddVideoOverlaySourceAsync(layer, outgoing, assets[outgoing.AssetId],
                overlayFileForAsync, outgoingHold, start, end - start,
                TransitionBounds(outgoingBounds, sample.OutgoingOffsetX,
                    sample.OutgoingOffsetY, sample.OutgoingScale),
                sample.OutgoingOpacity, token);
            await AddVideoOverlaySourceAsync(layer, incoming, assets[incoming.AssetId],
                overlayFileForAsync, incomingHold, start, end - start,
                TransitionBounds(incomingBounds, sample.IncomingOffsetX,
                    sample.IncomingOffsetY, sample.IncomingScale),
                sample.IncomingOpacity, token);
        }
    }

    private static async Task AddVideoOverlaySourceAsync(MediaOverlayLayer layer,
        VideoOverlay overlay, VideoAsset asset,
        Func<VideoOverlay, Task<StorageFile>> overlayFileForAsync,
        StorageFile? heldFrame, TimeSpan start, TimeSpan duration, Rect bounds,
        double opacity, CancellationToken token)
    {
        if (opacity <= 0 || duration <= TimeSpan.Zero) return;
        token.ThrowIfCancellationRequested();
        TimeSpan sourceIn = overlay.SourceIn + (start - overlay.Start);
        bool before = sourceIn < TimeSpan.Zero;
        bool after = sourceIn + duration > asset.Duration;
        MediaClip clip;
        if (before || after)
        {
            if (heldFrame is null)
                throw new InvalidDataException("A transition extends beyond its video source.");
            clip = await MediaClip.CreateFromImageFileAsync(heldFrame, duration);
        }
        else
        {
            if (sourceIn < TimeSpan.Zero || sourceIn + duration > asset.Duration)
                throw new InvalidDataException("A transition extends beyond its source file.");
            clip = await MediaClip.CreateFromFileAsync(
                await overlayFileForAsync(overlay));
            Trim(clip, sourceIn, sourceIn + duration);
            clip.Volume = 0;
        }
        if (overlay.Shape != VideoOverlayShape.Rectangle)
        {
            VideoRect crop = overlay.SourceCrop ?? new VideoRect(0, 0, 1, 1);
            var properties = new PropertySet
            {
                ["Shape"] = (int)overlay.Shape,
                ["CropX"] = crop.X,
                ["CropY"] = crop.Y,
                ["CropWidth"] = crop.Width,
                ["CropHeight"] = crop.Height,
            };
            clip.VideoEffectDefinitions.Add(new VideoEffectDefinition(
                typeof(VideoShapeMaskEffect).FullName!, properties));
        }
        layer.Overlays.Add(new MediaOverlay(clip)
        {
            Delay = start,
            Position = bounds,
            Opacity = Math.Clamp(opacity, 0, 1),
            AudioEnabled = false,
        });
    }

    private static async Task AddStaticOverlayLayersAsync(MediaComposition composition,
        VideoProject project, VideoTransitionLane lane,
        IReadOnlyList<StaticOverlayItem> items,
        List<(Guid Id, MediaOverlayLayer Layer)> visualLayers, CancellationToken token)
    {
        // Keep each logical timeline lane in one native layer so a transition
        // cannot cover unrelated overlays with a higher z-order.
        foreach (IGrouping<Guid, StaticOverlayItem> group in items
            .GroupBy(item => item.LayerId)
            .OrderBy(group => group.Min(item => item.ZIndex)))
        {
            token.ThrowIfCancellationRequested();
            var layer = new MediaOverlayLayer();
            Dictionary<Guid, StaticOverlayItem> byId = group.ToDictionary(item => item.Id);
            var transitions = new List<(VideoTransition Transition, VideoTransitionSpan Span)>();
            foreach (VideoTransition transition in project.Transitions.Where(item =>
                item.Lane == lane && byId.ContainsKey(item.OutgoingId) &&
                byId.ContainsKey(item.IncomingId)))
            {
                if (VideoTransitionGeometry.TryGetSpan(project, transition,
                    out VideoTransitionSpan span))
                    transitions.Add((transition, span));
            }

            foreach (StaticOverlayItem item in group.OrderBy(item => item.Start))
            {
                token.ThrowIfCancellationRequested();
                TimeSpan bodyStart = item.Start;
                TimeSpan bodyEnd = item.End;
                foreach ((VideoTransition transition, VideoTransitionSpan span) in transitions)
                {
                    if (transition.IncomingId == item.Id && span.End > bodyStart)
                        bodyStart = span.End;
                    if (transition.OutgoingId == item.Id && span.Start < bodyEnd)
                        bodyEnd = span.Start;
                }
                if (bodyEnd > bodyStart)
                    await AddStaticOverlayAsync(layer, item, bodyStart,
                        bodyEnd - bodyStart, item.Bounds, 1);
            }

            foreach ((VideoTransition transition, VideoTransitionSpan span) in transitions)
            {
                StaticOverlayItem outgoing = byId[transition.OutgoingId];
                StaticOverlayItem incoming = byId[transition.IncomingId];
                int count = TransitionSampleCount(transition.Duration, project.FrameRate);
                for (int index = 0; index < count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    TimeSpan start = SliceTime(span.Start, transition.Duration, index, count);
                    TimeSpan end = SliceTime(span.Start, transition.Duration, index + 1, count);
                    if (end <= start) continue;
                    double progress = (index + .5) / count;
                    VideoTransitionSample sample = VideoTransitionGeometry.Sample(
                        transition.Effect, progress);
                    if (sample.BackdropColor is { } backdrop)
                    {
                        double opacity = 1 - Math.Abs(progress * 2 - 1);
                        if (opacity > 0)
                        {
                            Rect bounds = Union(outgoing.Bounds, incoming.Bounds);
                            MediaClip color = MediaClip.CreateFromColor(
                                ParseColor(backdrop), end - start);
                            layer.Overlays.Add(new MediaOverlay(color)
                            {
                                Delay = start,
                                Position = bounds,
                                Opacity = opacity,
                                AudioEnabled = false,
                            });
                        }
                    }
                    await AddStaticOverlayAsync(layer, outgoing, start, end - start,
                        TransitionBounds(outgoing.Bounds, sample.OutgoingOffsetX,
                            sample.OutgoingOffsetY, sample.OutgoingScale),
                        sample.OutgoingOpacity);
                    await AddStaticOverlayAsync(layer, incoming, start, end - start,
                        TransitionBounds(incoming.Bounds, sample.IncomingOffsetX,
                            sample.IncomingOffsetY, sample.IncomingScale),
                        sample.IncomingOpacity);
                }
            }
            composition.OverlayLayers.Add(layer);
            visualLayers.Add((group.Key, layer));
        }
    }

    private static async Task AddStaticOverlayAsync(MediaOverlayLayer layer,
        StaticOverlayItem item, TimeSpan start, TimeSpan duration, Rect bounds,
        double opacity)
    {
        if (opacity <= 0 || duration <= TimeSpan.Zero) return;
        MediaClip clip = await MediaClip.CreateFromImageFileAsync(item.File, duration);
        layer.Overlays.Add(new MediaOverlay(clip)
        {
            Delay = start,
            Position = bounds,
            Opacity = Math.Clamp(opacity, 0, 1),
            AudioEnabled = false,
        });
    }

    private static Rect Union(Rect first, Rect second)
    {
        double left = Math.Min(first.Left, second.Left);
        double top = Math.Min(first.Top, second.Top);
        return new Rect(left, top, Math.Max(first.Right, second.Right) - left,
            Math.Max(first.Bottom, second.Bottom) - top);
    }

    private static void AddFadeSlices(MediaOverlayLayer layer, TimeSpan start,
        TimeSpan duration, bool fadeIn, int frameRate, double width, double height)
    {
        int count = FadeSampleCount(duration, frameRate);
        for (int index = 0; index < count; index++)
        {
            TimeSpan sliceStart = TimeSpan.FromTicks(duration.Ticks * index / count);
            TimeSpan sliceEnd = TimeSpan.FromTicks(duration.Ticks * (index + 1) / count);
            if (sliceEnd <= sliceStart) continue;
            MediaClip clip = MediaClip.CreateFromColor(Colors.Black, sliceEnd - sliceStart);
            layer.Overlays.Add(new MediaOverlay(clip)
            {
                Delay = start + sliceStart,
                Position = new Rect(0, 0, width, height),
                Opacity = FadeSampleValue(index, count, fadeIn),
                AudioEnabled = false,
            });
        }
    }

    private static double FadeSampleOpacity(TimeSpan elapsed, TimeSpan duration,
        bool fadeIn, int frameRate)
    {
        int count = FadeSampleCount(duration, frameRate);
        int index = (int)Math.Min(count - 1,
            elapsed.Ticks * (long)count / duration.Ticks);
        return FadeSampleValue(index, count, fadeIn);
    }

    private static int FadeSampleCount(TimeSpan duration, int frameRate) =>
        Math.Clamp((int)Math.Ceiling(duration.TotalSeconds * Math.Min(frameRate, 60)),
            1, MaximumFadeSamples);

    private static double FadeSampleValue(int index, int count, bool fadeIn) =>
        count == 1 ? 1 :
        fadeIn ? 1 - index / (double)(count - 1) : index / (double)(count - 1);

    private static void AddColorTransitionSlices(MediaOverlayLayer layer,
        VideoTransition transition, VideoTransitionSpan span, int frameRate,
        double width, double height)
    {
        int count = TransitionSampleCount(transition.Duration, frameRate);
        Color color = transition.Effect == VideoTransitionEffect.DipToWhite
            ? Colors.White : Colors.Black;
        for (int index = 0; index < count; index++)
        {
            TimeSpan start = SliceTime(span.Start, transition.Duration, index, count);
            TimeSpan end = SliceTime(span.Start, transition.Duration, index + 1, count);
            if (end <= start) continue;
            double progress = (index + .5) / count;
            double opacity = 1 - Math.Abs(progress * 2 - 1);
            var clip = MediaClip.CreateFromColor(color, end - start);
            layer.Overlays.Add(new MediaOverlay(clip)
            {
                Delay = start,
                Position = new Rect(0, 0, width, height),
                Opacity = opacity,
                AudioEnabled = false,
            });
        }
    }

    private static async Task<StorageFile> EnsureVideoEdgeFrameAsync(
        VideoProject project, VideoProjectStore store, VideoSegment segment,
        StorageFile sourceFile, bool outgoing, double width, double height,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        double rasterScale = Math.Min(1, MaximumTextRasterDimension /
            Math.Max(width, height));
        int rasterWidth = Math.Max(1, (int)Math.Ceiling(width * rasterScale));
        int rasterHeight = Math.Max(1, (int)Math.Ceiling(height * rasterScale));
        string projectDirectory = store.GetProjectDirectory(project.Id);
        if (!Directory.Exists(projectDirectory) || IsReparsePoint(projectDirectory))
            throw new IOException("The video project directory is unavailable.");
        string cacheDirectory = Path.Combine(projectDirectory, ".transition-cache");
        Directory.CreateDirectory(cacheDirectory);
        if (IsReparsePoint(cacheDirectory))
            throw new IOException("The transition frame cache cannot be a linked directory.");
        var sourceInfo = new FileInfo(sourceFile.Path);
        string sourceKey = FormattableString.Invariant(
            $"{sourceInfo.FullName}|{sourceInfo.Length}|{sourceInfo.LastWriteTimeUtc.Ticks}");
        string sourceHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(sourceKey)))[..16];
        string path = Path.Combine(cacheDirectory,
            $"{segment.AssetId:N}-{sourceHash}-{segment.SourceIn.Ticks}-{segment.SourceOut.Ticks}-" +
            $"{(outgoing ? "last" : "first")}-{rasterWidth}x{rasterHeight}.png");
        if (HasCachedFile(path, "The transition cache cannot contain linked files."))
            return await StorageFile.GetFileFromPathAsync(path);
        IDisposable gate = await CacheFileGates.EnterAsync(path, token);
        try
        {
            if (HasCachedFile(path, "The transition cache cannot contain linked files."))
                return await StorageFile.GetFileFromPathAsync(path);

            string temporary = Path.Combine(cacheDirectory,
                $".{Guid.NewGuid():N}.tmp");
            try
            {
                var source = new MediaComposition();
                MediaClip clip = await MediaClip.CreateFromFileAsync(sourceFile);
                Trim(clip, segment.SourceIn, segment.SourceOut);
                clip.Volume = 0;
                source.Clips.Add(clip);
                TimeSpan at = outgoing
                    ? TimeSpan.FromTicks(Math.Max(0, clip.TrimmedDuration.Ticks -
                        TimeSpan.TicksPerSecond / project.FrameRate))
                    : TimeSpan.Zero;
                using var thumbnail = await source.GetThumbnailAsync(at,
                    rasterWidth, rasterHeight, VideoFramePrecision.NearestFrame);
                using CanvasBitmap bitmap = await CanvasBitmap.LoadAsync(
                    CanvasDevice.GetSharedDevice(), thumbnail);
                await bitmap.SaveAsync(temporary, CanvasBitmapFileFormat.Png)
                    .AsTask(token);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
            }
            return await StorageFile.GetFileFromPathAsync(path);
        }
        finally
        {
            gate.Dispose();
        }
    }

    private static async Task AddVideoTransitionSlicesAsync(MediaOverlayLayer layer,
        VideoTransition transition, VideoTransitionSpan span,
        VideoSegment outgoing, VideoSegment incoming,
        VideoProject project, VideoProjectStore store,
        Dictionary<Guid, VideoAsset> assets,
        Func<Guid, Task<StorageFile>> fileForAsync,
        int frameRate, double width, double height, CancellationToken token)
    {
        int count = TransitionSampleCount(transition.Duration, frameRate);
        StorageFile? outgoingHold = outgoing.SourceOut + (span.End - span.Cut) <=
            assets[outgoing.AssetId].Duration ? null :
            await EnsureVideoEdgeFrameAsync(project, store, outgoing,
                await fileForAsync(outgoing.AssetId), outgoing: true,
                width, height, token);
        StorageFile? incomingHold = incoming.SourceIn >= span.Cut - span.Start ? null :
            await EnsureVideoEdgeFrameAsync(project, store, incoming,
                await fileForAsync(incoming.AssetId), outgoing: false,
                width, height, token);
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            TimeSpan start = SliceTime(span.Start, transition.Duration, index, count);
            TimeSpan end = SliceTime(span.Start, transition.Duration, index + 1, count);
            if (end <= start) continue;
            double progress = (index + .5) / count;
            VideoTransitionSample sample = VideoTransitionGeometry.Sample(
                transition.Effect, progress);
            TimeSpan duration = end - start;
            // The primary track changes at the cut. Cover it for this slice and
            // draw both sources so slides and zooms do not jump at that switch.
            layer.Overlays.Add(new MediaOverlay(MediaClip.CreateFromColor(
                Colors.Black, duration))
            {
                Delay = start,
                Position = new Rect(0, 0, width, height),
                Opacity = 1,
                AudioEnabled = false,
            });
            await AddVideoTransitionSourceAsync(layer, outgoing,
                assets[outgoing.AssetId], fileForAsync, outgoingHold,
                span, start, duration,
                new Rect(0, 0, width, height), sample.OutgoingOffsetX,
                sample.OutgoingOffsetY, sample.OutgoingScale,
                sample.OutgoingOpacity, outgoing: true);
            await AddVideoTransitionSourceAsync(layer, incoming,
                assets[incoming.AssetId], fileForAsync, incomingHold,
                span, start, duration,
                new Rect(0, 0, width, height), sample.IncomingOffsetX,
                sample.IncomingOffsetY, sample.IncomingScale,
                sample.IncomingOpacity, outgoing: false);
        }
    }

    private static async Task AddVideoTransitionSourceAsync(MediaOverlayLayer layer,
        VideoSegment segment, VideoAsset asset,
        Func<Guid, Task<StorageFile>> fileForAsync,
        StorageFile? heldFrame, VideoTransitionSpan span,
        TimeSpan start, TimeSpan duration, Rect bounds, double offsetX,
        double offsetY, double scale, double opacity, bool outgoing)
    {
        if (opacity <= 0) return;
        bool useHold = heldFrame is not null && (outgoing
            ? start >= span.Cut : start < span.Cut);
        MediaClip clip;
        if (useHold)
        {
            clip = await MediaClip.CreateFromImageFileAsync(heldFrame!, duration);
        }
        else
        {
            TimeSpan sourceIn = segment.SourceIn + (start - segment.Start);
            if (sourceIn < TimeSpan.Zero || sourceIn + duration > asset.Duration)
                throw new InvalidDataException("A transition extends beyond its source file.");
            clip = await MediaClip.CreateFromFileAsync(
                await fileForAsync(segment.AssetId));
            Trim(clip, sourceIn, sourceIn + duration);
            clip.Volume = 0;
        }
        layer.Overlays.Add(new MediaOverlay(clip)
        {
            Delay = start,
            Position = TransitionBounds(bounds, offsetX, offsetY, scale),
            Opacity = Math.Clamp(opacity, 0, 1),
            AudioEnabled = false,
        });
    }

    private static Rect TransitionBounds(Rect bounds, double offsetX, double offsetY,
        double scale) => new(
        bounds.X + bounds.Width * offsetX - bounds.Width * (scale - 1) / 2,
        bounds.Y + bounds.Height * offsetY - bounds.Height * (scale - 1) / 2,
        bounds.Width * scale, bounds.Height * scale);

    private static TimeSpan SliceTime(TimeSpan start, TimeSpan duration,
        int index, int count) =>
        start + TimeSpan.FromTicks(duration.Ticks * index / count);

    private static int TransitionSampleCount(TimeSpan duration, int frameRate) =>
        Math.Clamp(((int)Math.Ceiling(duration.TotalSeconds * Math.Min(frameRate, 60)) + 1) / 2 * 2,
            2, MaximumTransitionSamples);

    internal static async Task<TimeSpan> ProbeAudioDurationAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        BackgroundAudioTrack track = await BackgroundAudioTrack.CreateFromFileAsync(file);
        if (track.OriginalDuration <= TimeSpan.Zero)
        {
            throw new InvalidDataException("The audio file has no playable duration.");
        }

        return track.OriginalDuration;
    }

    internal static async Task ExportAsync(
        VideoProject project, VideoProjectStore store, string destinationPath,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string destination = store.GetExternalExportPath(destinationPath);
        string directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("Choose an output folder.", nameof(destinationPath));
        string staging = Path.Combine(directory, $".shnapp-{Guid.NewGuid():N}.mp4");
        MediaComposition composition = await BuildAsync(project, store, cancellationToken);

        try
        {
            await using (FileStream stream = new(staging, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, bufferSize: 4096, FileOptions.Asynchronous))
            {
                await stream.FlushAsync(cancellationToken);
            }

            StorageFile target = await StorageFile.GetFileFromPathAsync(staging);
            MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Width = checked((uint)Math.Max(2, project.Width & ~1));
            profile.Video.Height = checked((uint)Math.Max(2, project.Height & ~1));
            profile.Video.FrameRate.Numerator = checked((uint)project.FrameRate);
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.Bitrate = checked((uint)Math.Clamp(
                (long)profile.Video.Width * profile.Video.Height * 8 * project.FrameRate / 30,
                2_000_000, 60_000_000));

            var operation = composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile);
            operation.Progress = (_, value) => progress?.Report(value);
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                try { operation.Cancel(); }
                catch (System.Runtime.InteropServices.COMException) { }
            });
            TranscodeFailureReason reason = await operation;
            if (reason != TranscodeFailureReason.None)
            {
                throw new InvalidOperationException($"Windows could not export this video ({reason}).");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (new FileInfo(staging).Length == 0)
            {
                throw new IOException("The video export was empty.");
            }

            store.GetExternalExportPath(destination);
            File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }

    private static void Trim(MediaClip clip, TimeSpan sourceIn, TimeSpan sourceOut)
    {
        TimeSpan end = Min(sourceOut, clip.OriginalDuration);
        if (sourceIn >= end)
        {
            throw new InvalidDataException("A video segment is outside its source file.");
        }

        clip.TrimTimeFromStart = sourceIn;
        clip.TrimTimeFromEnd = clip.OriginalDuration - end;
    }

    private static void Trim(BackgroundAudioTrack track, TimeSpan sourceIn, TimeSpan sourceOut)
    {
        TimeSpan end = Min(sourceOut, track.OriginalDuration);
        if (sourceIn >= end)
        {
            throw new InvalidDataException("An audio segment is outside its source file.");
        }

        track.TrimTimeFromStart = sourceIn;
        track.TrimTimeFromEnd = track.OriginalDuration - end;
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
}
