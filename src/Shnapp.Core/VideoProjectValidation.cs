using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Shnapp.Core;

internal static class VideoProjectValidation
{
    internal const int MaximumAssets = 4_096;
    internal const int MaximumTimelineItems = 16_384;
    internal static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(7);

    internal static void Validate(VideoProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Require(project.SchemaVersion == VideoProject.CurrentSchemaVersion,
            "Unsupported video project schema version.");
        Require(project.Id != Guid.Empty, "A video project identifier cannot be empty.");
        Require(project.Title is not null && project.Title == ShnappTitles.Normalize(project.Title),
            "A video project title must be normalized.");
        Require(project.CreatedAt != default && project.CreatedAt.Offset == TimeSpan.Zero,
            "A video project creation time must be UTC.");
        Require(project.Width is > 0 and <= 16_384 && project.Height is > 0 and <= 16_384,
            "Video dimensions must be between 1 and 16,384 pixels.");
        Require(project.FrameRate is 30 or 60,
            "Video frame rate must be 30 or 60 frames per second.");
        Require(!project.Assets.IsDefault && project.Assets.Length is > 0 and <= MaximumAssets,
            "A video project must contain a bounded asset list.");
        Require(!project.VideoSegments.IsDefault && project.VideoSegments.Length is > 0 and <= MaximumTimelineItems,
            "A video project must contain a bounded primary video lane.");
        Require(!project.AudioSegments.IsDefault && project.AudioSegments.Length <= MaximumTimelineItems,
            "Audio segments must be initialized and bounded.");
        Require(!project.ImageOverlays.IsDefault && project.ImageOverlays.Length <= MaximumTimelineItems,
            "Image overlays must be initialized and bounded.");

        var assets = new Dictionary<Guid, VideoAsset>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int recordings = 0;
        int computerStems = 0;
        int microphoneStems = 0;
        foreach (VideoAsset asset in project.Assets)
        {
            Require(asset is not null && asset.Id != Guid.Empty && Enum.IsDefined(asset.Kind),
                "A video asset is invalid.");
            Require(assets.TryAdd(asset.Id, asset), "Video asset identifiers must be unique.");
            Require(asset.DisplayName is null ||
                (asset.DisplayName.Length is > 0 and <= ShnappTitles.MaximumLength &&
                 asset.DisplayName == asset.DisplayName.Trim() &&
                 !asset.DisplayName.Any(char.IsControl)),
                "An imported media name must be short and readable.");
            ValidateRelativePath(asset.RelativePath);
            Require(paths.Add(asset.RelativePath), "Video asset paths must be unique.");
            string extension = Path.GetExtension(asset.RelativePath);
            switch (asset.Kind)
            {
                case VideoAssetKind.Recording:
                    recordings++;
                    Require(extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase),
                        "The recording source must be an MP4 file.");
                    break;
                case VideoAssetKind.ComputerAudio:
                    computerStems++;
                    goto case VideoAssetKind.MicrophoneAudio;
                case VideoAssetKind.MicrophoneAudio:
                    if (asset.Kind == VideoAssetKind.MicrophoneAudio) { microphoneStems++; }
                    Require(extension.Equals(".wav", StringComparison.OrdinalIgnoreCase),
                        "Recorded audio stems must be WAV files.");
                    break;
                case VideoAssetKind.ImportedAudio:
                    Require(extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".flac", StringComparison.OrdinalIgnoreCase),
                        "Imported audio must be WAV, MP3, or FLAC.");
                    break;
                case VideoAssetKind.Image:
                    Require(extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase),
                        "Overlay images must be PNG or JPEG files.");
                    break;
            }
            Require(asset.Kind == VideoAssetKind.Image
                    ? asset.Duration == TimeSpan.Zero
                    : asset.Duration > TimeSpan.Zero && asset.Duration <= MaximumDuration,
                "A media asset has an invalid source duration.");
        }
        Require(recordings == 1 && computerStems <= 1 && microphoneStems <= 1,
            "A video project must have one original MP4 and at most one of each recorded audio stem.");

        var itemIds = new HashSet<Guid>();
        Require(project.VideoSegments.All(static segment => segment is not null),
            "Primary video segments cannot be null.");
        VideoSegment[] video = project.VideoSegments.OrderBy(static segment => segment.Start).ToArray();
        TimeSpan previousEnd = TimeSpan.Zero;
        Guid recordingId = assets.Values.Single(static asset => asset.Kind == VideoAssetKind.Recording).Id;
        foreach (VideoSegment segment in video)
        {
            Require(segment is not null && segment.Id != Guid.Empty && itemIds.Add(segment.Id),
                "Video segment identifiers must be unique and nonempty.");
            Require(segment.AssetId == recordingId, "Primary video segments must reference the original MP4.");
            ValidateSourceInterval(segment.Start, segment.SourceIn, segment.SourceOut,
                assets[recordingId].Duration);
            Require(segment.Start >= previousEnd, "Primary video segments cannot overlap.");
            previousEnd = segment.End;
        }
        TimeSpan timelineEnd = previousEnd;
        Require(timelineEnd > TimeSpan.Zero && timelineEnd <= MaximumDuration,
            "The edited video duration must be positive and bounded.");

        foreach (AudioSegment segment in project.AudioSegments)
        {
            Require(segment is not null && segment.Id != Guid.Empty && itemIds.Add(segment.Id),
                "Audio segment identifiers must be unique and nonempty.");
            Require(assets.TryGetValue(segment.AssetId, out VideoAsset? asset) &&
                asset.Kind is VideoAssetKind.ComputerAudio or VideoAssetKind.MicrophoneAudio or
                    VideoAssetKind.ImportedAudio,
                "An audio segment must reference an audio asset.");
            ValidateSourceInterval(segment.Start, segment.SourceIn, segment.SourceOut, asset.Duration);
            Require(segment.End <= timelineEnd, "Audio cannot extend beyond the primary video.");
            Require(double.IsFinite(segment.Gain) && segment.Gain is >= 0 and <= 4,
                "Audio gain must be a finite multiplier from 0 to 4.");
        }

        foreach (ImageOverlay overlay in project.ImageOverlays)
        {
            Require(overlay is not null && overlay.Id != Guid.Empty && itemIds.Add(overlay.Id),
                "Image overlay identifiers must be unique and nonempty.");
            Require(assets.TryGetValue(overlay.AssetId, out VideoAsset? asset) &&
                asset.Kind == VideoAssetKind.Image, "An image overlay must reference an image asset.");
            ValidateTimelineInterval(overlay.Start, overlay.Duration);
            Require(overlay.End <= timelineEnd, "An image cannot extend beyond the primary video.");
            Require(overlay.ZIndex is >= 0 and <= 1_000_000, "Image z-order is out of range.");
            VideoRect bounds = overlay.Bounds;
            Require(double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
                double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
                bounds.Width is > 0 and <= 65_536 && bounds.Height is > 0 and <= 65_536 &&
                Math.Abs(bounds.X) <= 65_536 && Math.Abs(bounds.Y) <= 65_536,
                "Image bounds must be finite video-pixel coordinates with positive size.");
        }
    }

    internal static void ValidateRelativePath(string relativePath)
    {
        Require(!string.IsNullOrWhiteSpace(relativePath) && relativePath.Length <= 1_024 &&
            !Path.IsPathRooted(relativePath) && !relativePath.Contains('\\') &&
            !relativePath.StartsWith('/') && !relativePath.EndsWith('/'),
            "Asset paths must be project-relative paths with forward slashes.");
        string[] segments = relativePath.Split('/');
        foreach (string segment in segments)
        {
            Require(segment.Length is > 0 and <= 255 && segment is not "." and not ".." &&
                segment[^1] is not '.' and not ' ' &&
                segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*', '\0']) < 0 &&
                !segment.Any(char.IsControl) && !IsReservedDeviceName(segment),
                "Asset paths cannot contain traversal, reserved characters, or empty components.");
        }
    }

    private static bool IsReservedDeviceName(string segment)
    {
        string stem = segment.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return stem.Length == 4 && stem[3] is >= '1' and <= '9' &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateSourceInterval(TimeSpan start, TimeSpan sourceIn,
        TimeSpan sourceOut, TimeSpan sourceDuration)
    {
        Require(sourceIn >= TimeSpan.Zero && sourceOut > sourceIn && sourceOut <= sourceDuration,
            "A source interval must be positive and within its asset.");
        ValidateTimelineInterval(start, sourceOut - sourceIn);
    }

    private static void ValidateTimelineInterval(TimeSpan start, TimeSpan duration)
    {
        Require(start >= TimeSpan.Zero && start <= MaximumDuration &&
            duration > TimeSpan.Zero && duration <= MaximumDuration - start,
            "A timeline interval must be positive and bounded.");
    }

    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) { throw new ArgumentException(message); }
    }
}
