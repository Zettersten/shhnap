using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Shnapp.Core;
using Microsoft.UI;
using Windows.Foundation;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.UI;

namespace Shnapp.App.Video;

/// <summary>Turns the project's non-destructive timeline into a Windows media composition.</summary>
internal static class VideoCompositionService
{
    private const int MaximumTextRasterDimension = 4_096;
    private const double MaximumTextRasterPixels = 4_000_000;
    private const int MaximumFadeSamples = 60;
    private static readonly ConcurrentDictionary<(Guid Project, Guid Overlay), SemaphoreSlim>
        TextRasterGates = new();

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
        foreach (VideoSegment segment in project.VideoSegments.OrderBy(item => item.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment.Start > cursor)
            {
                composition.Clips.Add(MediaClip.CreateFromColor(Colors.Black, segment.Start - cursor));
                cursor = segment.Start;
            }

            MediaClip clip = await MediaClip.CreateFromFileAsync(await FileForAsync(segment.AssetId));
            Trim(clip, segment.SourceIn, segment.SourceOut);
            // Recording audio is stored in independent stems; never double-play embedded sound.
            clip.Volume = 0;
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
        if (includeImageOverlays && project.VideoSegments.Any(item =>
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

        if (includeImageOverlays && !project.ImageOverlays.IsDefaultOrEmpty)
        {
            double scaleX = previewSize?.Width / (double)project.Width ?? 1;
            double scaleY = previewSize?.Height / (double)project.Height ?? 1;
            var layer = new MediaOverlayLayer();
            foreach (ImageOverlay image in project.ImageOverlays.OrderBy(item => item.ZIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                MediaClip clip = await MediaClip.CreateFromImageFileAsync(
                    await FileForAsync(image.AssetId), image.Duration);
                var overlay = new MediaOverlay(clip)
                {
                    Delay = image.Start,
                    Position = new Rect(image.Bounds.X * scaleX, image.Bounds.Y * scaleY,
                        image.Bounds.Width * scaleX, image.Bounds.Height * scaleY),
                    Opacity = 1,
                    AudioEnabled = false,
                };
                layer.Overlays.Add(overlay);
            }

            composition.OverlayLayers.Add(layer);
        }

        if (includeImageOverlays && !project.TextOverlays.IsDefaultOrEmpty)
        {
            double scaleX = previewSize?.Width / (double)project.Width ?? 1;
            double scaleY = previewSize?.Height / (double)project.Height ?? 1;
            var layer = new MediaOverlayLayer();
            foreach (TextOverlay text in project.TextOverlays.OrderBy(item => item.ZIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = await EnsureTextOverlayPngAsync(project, text, store, cancellationToken);
                StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                MediaClip clip = await MediaClip.CreateFromImageFileAsync(file, text.Duration);
                layer.Overlays.Add(new MediaOverlay(clip)
                {
                    Delay = text.Start,
                    Position = new Rect(text.Bounds.X * scaleX, text.Bounds.Y * scaleY,
                        text.Bounds.Width * scaleX, text.Bounds.Height * scaleY),
                    Opacity = 1,
                    AudioEnabled = false,
                });
            }
            composition.OverlayLayers.Add(layer);
        }

        return composition;
    }

    /// <summary>Returns the black overlay opacity used at a preview playhead position.</summary>
    internal static double GetFadeOpacity(VideoProject project, TimeSpan playhead)
    {
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
        SemaphoreSlim gate = TextRasterGates.GetOrAdd((project.Id, overlay.Id),
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path))
            {
                if (IsReparsePoint(path))
                    throw new IOException("The video text cache cannot contain linked files.");
                if (new FileInfo(path).Length > 0) return path;
            }

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
                    FontFamily = "Segoe UI Variable Display",
                    FontSize = (float)(overlay.FontSize * scale),
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
                    var bounds = new Rect(0, 0, rasterWidth, rasterHeight);
                    var shadowBounds = new Rect(2 * scale, 2 * scale,
                        rasterWidth, rasterHeight);
                    drawing.DrawText(overlay.Text, shadowBounds,
                        Color.FromArgb(180, 0, 0, 0), format);
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
            gate.Release();
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0;

    private static string TextRasterHash(TextOverlay overlay)
    {
        string signature = string.Join('\0', overlay.Text,
            overlay.Bounds.Width.ToString("R", CultureInfo.InvariantCulture),
            overlay.Bounds.Height.ToString("R", CultureInfo.InvariantCulture),
            overlay.FontSize.ToString("R", CultureInfo.InvariantCulture),
            overlay.Color.ToUpperInvariant(), overlay.Alignment.ToString());
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(signature));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static Color ParseColor(string hex) => Color.FromArgb(255,
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));

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
        string destination = Path.GetFullPath(destinationPath);
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
