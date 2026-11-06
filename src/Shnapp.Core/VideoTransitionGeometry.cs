namespace Shnapp.Core;

/// <summary>Geometry of one transition around a cut in project time.</summary>
public readonly record struct VideoTransitionSpan(TimeSpan Cut, TimeSpan Start, TimeSpan End)
{
    public TimeSpan HalfDuration => Cut - Start;
}

/// <summary>Visual state of both clips at a point inside a transition.</summary>
/// <remarks>Offsets are fractions of each clip's bounds; scales are relative to its normal size.</remarks>
public readonly record struct VideoTransitionSample(double OutgoingOpacity,
    double IncomingOpacity, double OutgoingOffsetX, double OutgoingOffsetY,
    double IncomingOffsetX, double IncomingOffsetY, double OutgoingScale,
    double IncomingScale, string? BackdropColor);

/// <summary>Resolves transitions against the current immutable timeline.</summary>
public static class VideoTransitionGeometry
{
    /// <summary>Presets with matching timeline preview and native MP4 composition.</summary>
    public static bool CanRender(VideoTransitionEffect effect, VideoTransitionLane lane) =>
        Enum.IsDefined(lane) && Enum.IsDefined(effect);

    public static VideoTransitionSample Sample(VideoTransitionEffect effect, double progress)
    {
        double p = Math.Clamp(double.IsFinite(progress) ? progress : 0, 0, 1);
        return effect switch
        {
            VideoTransitionEffect.Fade => new(1 - p, p, 0, 0, 0, 0, 1, 1, null),
            VideoTransitionEffect.DipToBlack => Dip(p, "#000000"),
            VideoTransitionEffect.DipToWhite => Dip(p, "#FFFFFF"),
            VideoTransitionEffect.SlideLeft => new(1, 1, -p, 0, 1 - p, 0, 1, 1, null),
            VideoTransitionEffect.SlideRight => new(1, 1, p, 0, p - 1, 0, 1, 1, null),
            VideoTransitionEffect.SlideUp => new(1, 1, 0, -p, 0, 1 - p, 1, 1, null),
            VideoTransitionEffect.SlideDown => new(1, 1, 0, p, 0, p - 1, 1, 1, null),
            VideoTransitionEffect.ZoomIn => new(1 - p, p, 0, 0, 0, 0,
                1 + p * .25, .75 + p * .25, null),
            VideoTransitionEffect.ZoomOut => new(1 - p, p, 0, 0, 0, 0,
                1 - p * .25, 1.25 - p * .25, null),
            _ => throw new ArgumentOutOfRangeException(nameof(effect)),
        };
    }

    private static VideoTransitionSample Dip(double p, string color) => new(
        Math.Max(0, 1 - 2 * p), Math.Max(0, 2 * p - 1),
        0, 0, 0, 0, 1, 1, color);

    public static bool TryGetSpan(VideoProject project, VideoTransition transition,
        out VideoTransitionSpan span)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(transition);
        span = default;
        if (transition.Duration <= TimeSpan.Zero ||
            transition.Duration > TimeSpan.FromSeconds(10) ||
            transition.Effect is not (VideoTransitionEffect.DipToBlack or
                VideoTransitionEffect.DipToWhite) &&
            transition.Duration > TimeSpan.FromSeconds(2) ||
            transition.OutgoingId == transition.IncomingId)
            return false;

        TimeSpan outgoingStart;
        TimeSpan outgoingEnd;
        TimeSpan incomingStart;
        TimeSpan incomingEnd;
        TimeSpan outgoingOppositeFade = TimeSpan.Zero;
        TimeSpan incomingOppositeFade = TimeSpan.Zero;
        switch (transition.Lane)
        {
            case VideoTransitionLane.Video:
                VideoSegment? outgoingVideo = project.VideoSegments.FirstOrDefault(item =>
                    item.Id == transition.OutgoingId);
                VideoSegment? incomingVideo = project.VideoSegments.FirstOrDefault(item =>
                    item.Id == transition.IncomingId);
                if (outgoingVideo is null || incomingVideo is null ||
                    outgoingVideo.FadeOut > TimeSpan.Zero ||
                    incomingVideo.FadeIn > TimeSpan.Zero) return false;
                outgoingStart = outgoingVideo.Start;
                outgoingEnd = outgoingVideo.End;
                incomingStart = incomingVideo.Start;
                incomingEnd = incomingVideo.End;
                outgoingOppositeFade = outgoingVideo.FadeIn;
                incomingOppositeFade = incomingVideo.FadeOut;
                break;
            case VideoTransitionLane.Image:
                ImageOverlay? outgoingImage = project.ImageOverlays.FirstOrDefault(item =>
                    item.Id == transition.OutgoingId);
                ImageOverlay? incomingImage = project.ImageOverlays.FirstOrDefault(item =>
                    item.Id == transition.IncomingId);
                if (outgoingImage is null || incomingImage is null ||
                    LayerKey(outgoingImage.Id, outgoingImage.LayerId) !=
                    LayerKey(incomingImage.Id, incomingImage.LayerId)) return false;
                outgoingStart = outgoingImage.Start;
                outgoingEnd = outgoingImage.End;
                incomingStart = incomingImage.Start;
                incomingEnd = incomingImage.End;
                break;
            case VideoTransitionLane.VideoOverlay:
                VideoOverlay? outgoingOverlay = project.VideoOverlays.FirstOrDefault(item =>
                    item.Id == transition.OutgoingId);
                VideoOverlay? incomingOverlay = project.VideoOverlays.FirstOrDefault(item =>
                    item.Id == transition.IncomingId);
                if (outgoingOverlay is null || incomingOverlay is null ||
                    LayerKey(outgoingOverlay.Id, outgoingOverlay.LayerId) !=
                    LayerKey(incomingOverlay.Id, incomingOverlay.LayerId)) return false;
                outgoingStart = outgoingOverlay.Start;
                outgoingEnd = outgoingOverlay.End;
                incomingStart = incomingOverlay.Start;
                incomingEnd = incomingOverlay.End;
                break;
            case VideoTransitionLane.Text:
                TextOverlay? outgoingText = project.TextOverlays.FirstOrDefault(item =>
                    item.Id == transition.OutgoingId);
                TextOverlay? incomingText = project.TextOverlays.FirstOrDefault(item =>
                    item.Id == transition.IncomingId);
                if (outgoingText is null || incomingText is null ||
                    LayerKey(outgoingText.Id, outgoingText.LayerId) !=
                    LayerKey(incomingText.Id, incomingText.LayerId)) return false;
                outgoingStart = outgoingText.Start;
                outgoingEnd = outgoingText.End;
                incomingStart = incomingText.Start;
                incomingEnd = incomingText.End;
                break;
            default:
                return false;
        }

        TimeSpan half = TimeSpan.FromTicks(transition.Duration.Ticks / 2);
        TimeSpan incomingHalf = transition.Duration - half;
        if (outgoingEnd != incomingStart ||
            outgoingEnd - outgoingStart - outgoingOppositeFade < half ||
            incomingEnd - incomingStart - incomingOppositeFade < incomingHalf)
            return false;
        span = new VideoTransitionSpan(outgoingEnd, outgoingEnd - half,
            outgoingEnd + incomingHalf);
        return true;
    }

    private static Guid LayerKey(Guid id, Guid layerId) =>
        layerId == Guid.Empty ? id : layerId;
}
