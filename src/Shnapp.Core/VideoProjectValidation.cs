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
        Require(project.FrameRate is 30 or 60 or 120,
            "Video frame rate must be 30, 60, or 120 frames per second.");
        Require(!project.Assets.IsDefault && project.Assets.Length is > 0 and <= MaximumAssets,
            "A video project must contain a bounded asset list.");
        Require(!project.VideoSegments.IsDefault && project.VideoSegments.Length is > 0 and <= MaximumTimelineItems,
            "A video project must contain a bounded primary video lane.");
        Require(!project.AudioSegments.IsDefault && project.AudioSegments.Length <= MaximumTimelineItems,
            "Audio segments must be initialized and bounded.");
        Require(!project.ImageOverlays.IsDefault && project.ImageOverlays.Length <= MaximumTimelineItems,
            "Image overlays must be initialized and bounded.");
        Require(!project.VideoOverlays.IsDefault && project.VideoOverlays.Length <= MaximumTimelineItems,
            "Video overlays must be initialized and bounded.");
        Require(!project.TextOverlays.IsDefault && project.TextOverlays.Length <= MaximumTimelineItems,
            "Text overlays must be initialized and bounded.");
        Require(!project.Transitions.IsDefault && project.Transitions.Length <= MaximumTimelineItems,
            "Transitions must be initialized and bounded.");
        Require(!project.HiddenLayerIds.IsDefault &&
            project.HiddenLayerIds.Length <= MaximumTimelineItems &&
            project.HiddenLayerIds.Distinct().Count() == project.HiddenLayerIds.Length,
            "Hidden visual layers must have unique identifiers.");

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
                case VideoAssetKind.ImportedVideo:
                    Require(extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase),
                        "Imported videos must be MP4 files.");
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
        var itemDurations = new Dictionary<Guid, TimeSpan>();
        Require(project.VideoSegments.All(static segment => segment is not null),
            "Primary video segments cannot be null.");
        VideoSegment[] video = project.VideoSegments.OrderBy(static segment => segment.Start).ToArray();
        TimeSpan previousEnd = TimeSpan.Zero;
        foreach (VideoSegment segment in video)
        {
            Require(segment is not null && segment.Id != Guid.Empty && itemIds.Add(segment.Id),
                "Video segment identifiers must be unique and nonempty.");
            Require(assets.TryGetValue(segment.AssetId, out VideoAsset? videoAsset) &&
                videoAsset.Kind is VideoAssetKind.Recording or VideoAssetKind.ImportedVideo,
                "Primary video segments must reference a video asset.");
            ValidateSourceInterval(segment.Start, segment.SourceIn, segment.SourceOut,
                videoAsset.Duration);
            Require(segment.Start >= previousEnd, "Primary video segments cannot overlap.");
            Require(segment.FadeIn >= TimeSpan.Zero && segment.FadeOut >= TimeSpan.Zero &&
                segment.FadeIn <= TimeSpan.FromSeconds(5) &&
                segment.FadeOut <= TimeSpan.FromSeconds(5) &&
                segment.FadeIn + segment.FadeOut <= segment.Duration,
                "Clip-edge fades must be nonnegative, at most five seconds, and fit the clip.");
            previousEnd = segment.End;
            itemDurations.Add(segment.Id, segment.Duration);
        }
        Require(previousEnd > TimeSpan.Zero,
            "A video project must retain a primary video segment.");

        foreach (VideoOverlay overlay in project.VideoOverlays)
        {
            Require(overlay is not null && overlay.Id != Guid.Empty && itemIds.Add(overlay.Id),
                "Video overlay identifiers must be unique and nonempty.");
            Require(assets.TryGetValue(overlay.AssetId, out VideoAsset? asset) &&
                asset.Kind is VideoAssetKind.Recording or VideoAssetKind.ImportedVideo,
                "Video overlays must reference a video asset.");
            ValidateSourceInterval(overlay.Start, overlay.SourceIn, overlay.SourceOut,
                asset.Duration);
            Require(overlay.ZIndex is >= 0 and <= 1_000_000,
                "Video overlay z-order is out of range.");
            ValidateBounds(overlay.Bounds, "Video overlay");
            ValidateSourceCrop(overlay.SourceCrop);
            itemDurations.Add(overlay.Id, overlay.Duration);
        }
        foreach (IGrouping<Guid, VideoOverlay> layer in project.VideoOverlays.GroupBy(item =>
            item.LayerId == Guid.Empty ? item.Id : item.LayerId))
        {
            TimeSpan end = TimeSpan.Zero;
            foreach (VideoOverlay overlay in layer.OrderBy(item => item.Start))
            {
                Require(overlay.Start >= end, "Video clips on one overlay layer cannot overlap.");
                end = overlay.End;
            }
        }

        foreach (AudioSegment segment in project.AudioSegments)
        {
            Require(segment is not null && segment.Id != Guid.Empty && itemIds.Add(segment.Id),
                "Audio segment identifiers must be unique and nonempty.");
            Require(assets.TryGetValue(segment.AssetId, out VideoAsset? asset) &&
                asset.Kind is VideoAssetKind.ComputerAudio or VideoAssetKind.MicrophoneAudio or
                    VideoAssetKind.ImportedAudio,
                "An audio segment must reference an audio asset.");
            ValidateSourceInterval(segment.Start, segment.SourceIn, segment.SourceOut, asset.Duration);
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
            Require(overlay.ZIndex is >= 0 and <= 1_000_000, "Image z-order is out of range.");
            ValidateBounds(overlay.Bounds, "Image");
            ValidateSourceCrop(overlay.SourceCrop);
            itemDurations.Add(overlay.Id, overlay.Duration);
        }

        foreach (TextOverlay overlay in project.TextOverlays)
        {
            Require(overlay is not null && overlay.Id != Guid.Empty && itemIds.Add(overlay.Id),
                "Text overlay identifiers must be unique and nonempty.");
            ValidateTimelineInterval(overlay.Start, overlay.Duration);
            Require(overlay.Text is { Length: > 0 and <= 1_024 } &&
                !string.IsNullOrWhiteSpace(overlay.Text) &&
                !overlay.Text.Any(static character => char.IsControl(character) &&
                    character is not '\r' and not '\n' and not '\t'),
                "Overlay text must be readable and at most 1,024 characters.");
            ValidateBounds(overlay.Bounds, "Text");
            Require(double.IsFinite(overlay.FontSize) && overlay.FontSize is >= 1 and <= 1_024,
                "Text size must be between 1 and 1,024 pixels.");
            Require(IsOpaqueHexColor(overlay.Color),
                "Text color must be a six-digit RGB hex value.");
            Require(overlay.FontFamily is { Length: > 0 and <= 128 } &&
                overlay.FontFamily == overlay.FontFamily.Trim() &&
                !overlay.FontFamily.Any(char.IsControl),
                "Text font family must be a short readable name.");
            Require(overlay.BackgroundColor is null || IsHexColor(overlay.BackgroundColor),
                "Text background must be an RGB or ARGB hex value.");
            Require(overlay.BorderColor is null || IsHexColor(overlay.BorderColor),
                "Text border must be an RGB or ARGB hex value.");
            Require(double.IsFinite(overlay.Padding) && overlay.Padding is >= 0 and <= 256 &&
                double.IsFinite(overlay.BorderThickness) &&
                overlay.BorderThickness is >= 0 and <= 64 &&
                double.IsFinite(overlay.CornerRadius) &&
                overlay.CornerRadius is >= 0 and <= 512,
                "Text padding, border, and corner radius must be finite and bounded.");
            Require(Enum.IsDefined(overlay.Alignment), "Text alignment is invalid.");
            Require(overlay.ZIndex is >= 0 and <= 1_000_000,
                "Text z-order is out of range.");
            itemDurations.Add(overlay.Id, overlay.Duration);
        }

        var outgoingEdges = new HashSet<(VideoTransitionLane Lane, Guid Id)>();
        var incomingEdges = new HashSet<(VideoTransitionLane Lane, Guid Id)>();
        var occupied = new Dictionary<Guid, TimeSpan>();
        foreach (VideoTransition transition in project.Transitions)
        {
            Require(transition is not null && transition.Id != Guid.Empty &&
                itemIds.Add(transition.Id),
                "Transition identifiers must be unique and nonempty.");
            Require(Enum.IsDefined(transition.Lane) && Enum.IsDefined(transition.Effect),
                "Transition lane or effect is invalid.");
            Require(VideoTransitionGeometry.TryGetSpan(project, transition, out VideoTransitionSpan span),
                "A transition must fit two touching clips on one lane.");
            Require(outgoingEdges.Add((transition.Lane, transition.OutgoingId)) &&
                incomingEdges.Add((transition.Lane, transition.IncomingId)),
                "A clip edge can have only one transition.");
            occupied[transition.OutgoingId] =
                occupied.GetValueOrDefault(transition.OutgoingId) + (span.Cut - span.Start);
            occupied[transition.IncomingId] =
                occupied.GetValueOrDefault(transition.IncomingId) + (span.End - span.Cut);
        }
        foreach ((Guid id, TimeSpan used) in occupied)
            Require(used <= itemDurations[id],
                "Transitions at both ends of a clip cannot overlap.");
        Require(project.Duration <= MaximumDuration,
            "The edited timeline duration must be bounded.");
    }

    private static void ValidateBounds(VideoRect bounds, string kind)
    {
        Require(double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
            double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
            bounds.Width is > 0 and <= 65_536 && bounds.Height is > 0 and <= 65_536 &&
            Math.Abs(bounds.X) <= 65_536 && Math.Abs(bounds.Y) <= 65_536,
            $"{kind} bounds must be finite video-pixel coordinates with positive size.");
    }

    private static void ValidateSourceCrop(VideoRect? crop)
    {
        if (crop is not { } value) return;
        const double tolerance = 0.000000001;
        Require(double.IsFinite(value.X) && double.IsFinite(value.Y) &&
            double.IsFinite(value.Width) && double.IsFinite(value.Height) &&
            value.X >= 0 && value.Y >= 0 && value.Width > 0 && value.Height > 0 &&
            value.X + value.Width <= 1 + tolerance &&
            value.Y + value.Height <= 1 + tolerance,
            "Source crop must fit inside the original media frame.");
    }

    private static bool IsOpaqueHexColor(string? color)
    {
        if (color is not { Length: 7 } || color[0] != '#') return false;
        for (int index = 1; index < color.Length; index++)
            if (!Uri.IsHexDigit(color[index])) return false;
        return true;
    }

    private static bool IsHexColor(string? color)
    {
        if (color is null || color.Length is not (7 or 9) || color[0] != '#')
            return false;
        for (int index = 1; index < color.Length; index++)
            if (!Uri.IsHexDigit(color[index])) return false;
        return true;
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
