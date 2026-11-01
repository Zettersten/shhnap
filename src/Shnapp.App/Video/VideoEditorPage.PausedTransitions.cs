using Shnapp.Core;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    private static bool TryGetActiveTransition(VideoProject project,
        VideoTransitionLane lane, TimeSpan position,
        out VideoTransition? active, out VideoTransitionSpan span)
    {
        foreach (VideoTransition transition in project.Transitions)
        {
            if (transition.Lane != lane ||
                !VideoTransitionGeometry.TryGetSpan(project, transition,
                    out VideoTransitionSpan candidate) ||
                position < candidate.Start || position >= candidate.End)
                continue;
            active = transition;
            span = candidate;
            return true;
        }
        active = null;
        span = default;
        return false;
    }

    private static PausedSource[] ActivePausedOverlays(VideoProject project,
        TimeSpan position)
    {
        var sources = new List<PausedSource>();
        var fullCanvas = new VideoRect(0, 0, project.Width, project.Height);
        var fullCrop = new VideoRect(0, 0, 1, 1);
        if (!project.HiddenLayerIds.Contains(Guid.Empty) &&
            TryGetActiveTransition(project, VideoTransitionLane.Video, position,
                out VideoTransition? primaryTransition, out VideoTransitionSpan primarySpan) &&
            primaryTransition is not null &&
            primaryTransition.Effect is not (VideoTransitionEffect.DipToBlack or
                VideoTransitionEffect.DipToWhite))
        {
            VideoSegment? outgoing = project.VideoSegments.FirstOrDefault(item =>
                item.Id == primaryTransition.OutgoingId);
            VideoSegment? incoming = project.VideoSegments.FirstOrDefault(item =>
                item.Id == primaryTransition.IncomingId);
            if (outgoing is not null && incoming is not null)
            {
                VideoTransitionSample sample = SampleAt(primaryTransition,
                    primarySpan, position);
                if (sample.OutgoingOpacity > .001)
                    sources.Add(new PausedSource(outgoing.Id, outgoing.AssetId,
                        TransitionSourceTime(project, outgoing.AssetId,
                            outgoing.SourceIn, outgoing.SourceOut, outgoing.Start,
                            position, primarySpan, outgoing: true,
                            overlay: false),
                        TransformPausedBounds(fullCanvas, sample.OutgoingOffsetX,
                            sample.OutgoingOffsetY, sample.OutgoingScale),
                        fullCrop, -100, sample.OutgoingOpacity));
                if (sample.IncomingOpacity > .001)
                    sources.Add(new PausedSource(incoming.Id, incoming.AssetId,
                        TransitionSourceTime(project, incoming.AssetId,
                            incoming.SourceIn, incoming.SourceOut, incoming.Start,
                            position, primarySpan, outgoing: false,
                            overlay: false),
                        TransformPausedBounds(fullCanvas, sample.IncomingOffsetX,
                            sample.IncomingOffsetY, sample.IncomingScale),
                        fullCrop, -99, sample.IncomingOpacity));
            }
        }

        var transitions = new List<(VideoTransition Transition,
            VideoTransitionSpan Span, VideoOverlay Outgoing, VideoOverlay Incoming)>();
        var consumed = new HashSet<Guid>();
        foreach (VideoTransition transition in project.Transitions)
        {
            if (transition.Lane != VideoTransitionLane.VideoOverlay ||
                !VideoTransitionGeometry.TryGetSpan(project, transition,
                    out VideoTransitionSpan span) ||
                position < span.Start || position >= span.End)
                continue;
            VideoOverlay? outgoing = project.VideoOverlays.FirstOrDefault(item =>
                item.Id == transition.OutgoingId);
            VideoOverlay? incoming = project.VideoOverlays.FirstOrDefault(item =>
                item.Id == transition.IncomingId);
            if (outgoing is null || incoming is null ||
                !CanvasLayerVisible(project, outgoing.Id, outgoing.LayerId))
                continue;
            transitions.Add((transition, span, outgoing, incoming));
            consumed.Add(outgoing.Id);
            consumed.Add(incoming.Id);
        }

        foreach (VideoOverlay overlay in project.VideoOverlays)
        {
            if (consumed.Contains(overlay.Id) ||
                position < overlay.Start || position >= overlay.End ||
                !CanvasLayerVisible(project, overlay.Id, overlay.LayerId))
                continue;
            sources.Add(new PausedSource(overlay.Id, overlay.AssetId,
                overlay.SourceIn + position - overlay.Start, overlay.Bounds,
                overlay.SourceCrop ?? fullCrop,
                OverlayPausedZ(project, overlay) + 1, Shape: overlay.Shape));
        }

        foreach (var (transition, span, outgoing, incoming) in transitions)
        {
            VideoTransitionSample sample = SampleAt(transition, span, position);
            int z = OverlayPausedZ(project, outgoing);
            if (sample.OutgoingOpacity > .001)
                sources.Add(new PausedSource(outgoing.Id, outgoing.AssetId,
                    TransitionSourceTime(project, outgoing.AssetId,
                        outgoing.SourceIn, outgoing.SourceOut, outgoing.Start,
                        position, span, outgoing: true, overlay: true),
                    TransformPausedBounds(outgoing.Bounds, sample.OutgoingOffsetX,
                        sample.OutgoingOffsetY, sample.OutgoingScale),
                    outgoing.SourceCrop ?? fullCrop, z + 1,
                    sample.OutgoingOpacity, outgoing.Shape));
            if (sample.IncomingOpacity > .001)
                sources.Add(new PausedSource(incoming.Id, incoming.AssetId,
                    TransitionSourceTime(project, incoming.AssetId,
                        incoming.SourceIn, incoming.SourceOut, incoming.Start,
                        position, span, outgoing: false, overlay: true),
                    TransformPausedBounds(incoming.Bounds, sample.IncomingOffsetX,
                        sample.IncomingOffsetY, sample.IncomingScale),
                    incoming.SourceCrop ?? fullCrop, z + 2,
                    sample.IncomingOpacity, incoming.Shape));
        }
        return [.. sources.OrderBy(item => item.ZIndex)];
    }

    private static PausedBackdrop[] ActivePausedBackdrops(VideoProject project,
        TimeSpan position)
    {
        var backdrops = new List<PausedBackdrop>();
        if (!project.HiddenLayerIds.Contains(Guid.Empty) &&
            TryGetActiveTransition(project, VideoTransitionLane.Video, position,
                out VideoTransition? primary, out VideoTransitionSpan primarySpan) &&
            primary is { Effect: VideoTransitionEffect.DipToBlack or
                VideoTransitionEffect.DipToWhite })
            backdrops.Add(new PausedBackdrop(primary.Id,
                new VideoRect(0, 0, project.Width, project.Height),
                primary.Effect == VideoTransitionEffect.DipToWhite,
                DipBackdropOpacity(primary, primarySpan, position), -50));

        foreach (VideoTransition transition in project.Transitions)
        {
            if (transition.Lane != VideoTransitionLane.VideoOverlay ||
                transition.Effect is not (VideoTransitionEffect.DipToBlack or
                    VideoTransitionEffect.DipToWhite) ||
                !VideoTransitionGeometry.TryGetSpan(project, transition,
                    out VideoTransitionSpan span) ||
                position < span.Start || position >= span.End)
                continue;
            VideoOverlay? outgoing = project.VideoOverlays.FirstOrDefault(item =>
                item.Id == transition.OutgoingId);
            VideoOverlay? incoming = project.VideoOverlays.FirstOrDefault(item =>
                item.Id == transition.IncomingId);
            if (outgoing is null || incoming is null ||
                !CanvasLayerVisible(project, outgoing.Id, outgoing.LayerId))
                continue;
            backdrops.Add(new PausedBackdrop(transition.Id,
                UnionPausedBounds(outgoing.Bounds, incoming.Bounds),
                transition.Effect == VideoTransitionEffect.DipToWhite,
                DipBackdropOpacity(transition, span, position),
                OverlayPausedZ(project, outgoing)));
        }
        return [.. backdrops];
    }

    private static VideoTransitionSample SampleAt(VideoTransition transition,
        VideoTransitionSpan span, TimeSpan position) =>
        VideoTransitionGeometry.Sample(transition.Effect,
            (position - span.Start).TotalSeconds /
            transition.Duration.TotalSeconds);

    private static double DipBackdropOpacity(VideoTransition transition,
        VideoTransitionSpan span, TimeSpan position)
    {
        double progress = (position - span.Start).TotalSeconds /
            transition.Duration.TotalSeconds;
        return Math.Clamp(1 - Math.Abs(progress * 2 - 1), 0, 1);
    }

    private static int OverlayPausedZ(VideoProject project, VideoOverlay overlay) =>
        VideoProjectTracks.VisualStackIndex(project,
            VideoProjectTracks.LayerId(overlay)) * 100;

    private static TimeSpan TransitionSourceTime(VideoProject project,
        Guid assetId, TimeSpan sourceIn, TimeSpan sourceOut, TimeSpan start,
        TimeSpan position, VideoTransitionSpan span, bool outgoing,
        bool overlay)
    {
        VideoAsset? asset = project.Assets.FirstOrDefault(item =>
            item.Id == assetId);
        TimeSpan at = sourceIn + position - start;
        bool holdOutgoing = outgoing && asset is not null &&
            (overlay ? at >= asset.Duration :
                position >= span.Cut &&
                sourceOut + (span.End - span.Cut) > asset.Duration);
        if (holdOutgoing)
            return TimeSpan.FromTicks(Math.Max(sourceIn.Ticks,
                sourceOut.Ticks - TimeSpan.TicksPerSecond /
                    Math.Max(1, project.FrameRate)));
        bool holdIncoming = !outgoing &&
            (overlay ? at < TimeSpan.Zero :
                position < span.Cut &&
                sourceIn < span.Cut - span.Start);
        if (holdIncoming)
            return sourceIn;
        return at;
    }

    private static VideoRect TransformPausedBounds(VideoRect bounds,
        double offsetX, double offsetY, double scale) => new(
            bounds.X + bounds.Width * offsetX -
                bounds.Width * (scale - 1) / 2,
            bounds.Y + bounds.Height * offsetY -
                bounds.Height * (scale - 1) / 2,
            bounds.Width * scale, bounds.Height * scale);

    private static VideoRect UnionPausedBounds(VideoRect first,
        VideoRect second)
    {
        double left = Math.Min(first.X, second.X);
        double top = Math.Min(first.Y, second.Y);
        double right = Math.Max(first.X + first.Width, second.X + second.Width);
        double bottom = Math.Max(first.Y + first.Height,
            second.Y + second.Height);
        return new VideoRect(left, top, right - left, bottom - top);
    }
}
