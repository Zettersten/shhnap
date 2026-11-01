using System.Collections.Immutable;

namespace Shnapp.Core;

/// <summary>Stable timeline track identities and their top-to-bottom order.</summary>
public static class VideoProjectTracks
{
    public static Guid LayerId(VideoOverlay item) => item.LayerId == Guid.Empty ? item.Id : item.LayerId;
    public static Guid LayerId(ImageOverlay item) => item.LayerId == Guid.Empty ? item.Id : item.LayerId;
    public static Guid LayerId(TextOverlay item) => item.LayerId == Guid.Empty ? item.Id : item.LayerId;

    /// <summary>Visual tracks appear above the recording; audio tracks follow it.</summary>
    public static ImmutableArray<Guid> DefaultOrder(VideoProject project)
    {
        var order = new List<Guid>();
        order.AddRange(project.TextOverlays.Select((item, index) => (item, index))
            .GroupBy(entry => LayerId(entry.item))
            .OrderByDescending(group => group.First().item.ZIndex)
            .ThenByDescending(group => group.Max(entry => entry.index))
            .Select(group => group.Key));
        order.AddRange(project.ImageOverlays.Select((item, index) => (item, index))
            .GroupBy(entry => LayerId(entry.item))
            .OrderByDescending(group => group.First().item.ZIndex)
            .ThenByDescending(group => group.Max(entry => entry.index))
            .Select(group => group.Key));
        order.AddRange(project.VideoOverlays.Select((item, index) => (item, index))
            .GroupBy(entry => LayerId(entry.item))
            .OrderByDescending(group => group.First().item.ZIndex)
            .ThenByDescending(group => group.Max(entry => entry.index))
            .Select(group => group.Key));
        order.Add(Guid.Empty);
        HashSet<Guid> usedAudio = [.. project.AudioSegments.Select(item => item.AssetId)];
        order.AddRange(project.Assets.Where(asset => usedAudio.Contains(asset.Id))
            .OrderBy(asset => asset.Kind switch
            {
                VideoAssetKind.ComputerAudio => 0,
                VideoAssetKind.MicrophoneAudio => 1,
                _ => 2,
            }).Select(asset => asset.Id));
        return [.. order];
    }

    /// <summary>Reconciles saved order with tracks added after that order was saved.</summary>
    public static ImmutableArray<Guid> OrderedIds(VideoProject project)
    {
        ImmutableArray<Guid> fallback = DefaultOrder(project);
        if (project.TrackOrder.IsDefaultOrEmpty) return fallback;
        HashSet<Guid> present = [.. fallback];
        var order = project.TrackOrder.Where(present.Contains).Distinct().ToList();
        foreach (Guid id in fallback)
        {
            if (order.Contains(id)) continue;
            if (IsAudio(project, id)) order.Add(id);
            else order.Insert(0, id);
        }
        return [.. order];
    }

    public static bool IsAudio(VideoProject project, Guid id) => id != Guid.Empty &&
        project.AudioSegments.Any(item => item.AssetId == id);

    public static bool IsVisual(VideoProject project, Guid id) => id == Guid.Empty ||
        project.VideoOverlays.Any(item => LayerId(item) == id) ||
        project.ImageOverlays.Any(item => LayerId(item) == id) ||
        project.TextOverlays.Any(item => LayerId(item) == id);

    public static bool Contains(VideoProject project, Guid id) =>
        IsVisual(project, id) || IsAudio(project, id);

    /// <summary>Higher values appear in front of lower values.</summary>
    public static int VisualStackIndex(VideoProject project, Guid id)
    {
        ImmutableArray<Guid> order = OrderedIds(project);
        int position = order.IndexOf(id);
        int basePosition = order.IndexOf(Guid.Empty);
        return position < 0 || position > basePosition ? -1 : basePosition - position;
    }
}
