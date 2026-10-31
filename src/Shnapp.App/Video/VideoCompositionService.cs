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
    internal static async Task<MediaComposition> BuildAsync(
        VideoProject project, VideoProjectStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(store);

        var composition = new MediaComposition();
        var files = new Dictionary<Guid, StorageFile>();

        async Task<StorageFile> FileForAsync(Guid assetId)
        {
            if (files.TryGetValue(assetId, out StorageFile? cached))
            {
                return cached;
            }

            VideoAsset asset = project.Assets.First(item => item.Id == assetId);
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

        if (!project.ImageOverlays.IsDefaultOrEmpty)
        {
            var layer = new MediaOverlayLayer();
            foreach (ImageOverlay image in project.ImageOverlays.OrderBy(item => item.ZIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                MediaClip clip = await MediaClip.CreateFromImageFileAsync(
                    await FileForAsync(image.AssetId), image.Duration);
                var overlay = new MediaOverlay(clip)
                {
                    Delay = image.Start,
                    Position = new Rect(image.Bounds.X, image.Bounds.Y,
                        image.Bounds.Width, image.Bounds.Height),
                    Opacity = 1,
                    AudioEnabled = false,
                };
                layer.Overlays.Add(overlay);
            }

            composition.OverlayLayers.Add(layer);
        }

        return composition;
    }

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
            profile.Video.Bitrate = checked((uint)Math.Clamp(
                (long)profile.Video.Width * profile.Video.Height * 8, 2_000_000, 40_000_000));

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
