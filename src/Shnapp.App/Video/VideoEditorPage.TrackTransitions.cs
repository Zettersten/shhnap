using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shnapp.Core;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    private void AddTimelineClipContextMenu(Button clip, TimelineKind kind, Guid id)
    {
        if (kind is not (TimelineKind.Image or TimelineKind.Text)) return;
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => PopulateTimelineClipContextMenu(menu, kind, id);
        clip.ContextFlyout = menu;
    }

    private void PopulateTimelineClipContextMenu(MenuFlyout menu, TimelineKind kind, Guid id)
    {
        menu.Items.Clear();
        if (_editor is null) return;
        VideoProject project = _editor.Current;
        if (kind == TimelineKind.Image)
        {
            ImageOverlay? incoming = project.ImageOverlays.FirstOrDefault(item => item.Id == id);
            if (incoming is null || project.Transitions.Any(item => item.IncomingId == id)) return;
            Guid ownLayer = ImageLayerKey(incoming);
            if (project.ImageOverlays.Count(item => ImageLayerKey(item) == ownLayer) != 1) return;
            foreach (ImageOverlay outgoing in project.ImageOverlays.Where(item =>
                item.Id != id && item.End == incoming.Start &&
                ImageLayerKey(item) != ownLayer &&
                !project.Transitions.Any(transition => transition.OutgoingId == item.Id)))
            {
                string name = project.Assets.FirstOrDefault(asset => asset.Id == outgoing.AssetId)
                    ?.DisplayName ?? "image";
                AddTrackTransitionChoices(menu, kind, outgoing.Id, incoming.Id,
                    $"Transition from {name}");
            }
        }
        else
        {
            TextOverlay? incoming = project.TextOverlays.FirstOrDefault(item => item.Id == id);
            if (incoming is null || project.Transitions.Any(item => item.IncomingId == id)) return;
            Guid ownLayer = incoming.LayerId == Guid.Empty ? incoming.Id : incoming.LayerId;
            if (project.TextOverlays.Count(item =>
                (item.LayerId == Guid.Empty ? item.Id : item.LayerId) == ownLayer) != 1) return;
            foreach (TextOverlay outgoing in project.TextOverlays.Where(item =>
                item.Id != id && item.End == incoming.Start &&
                (item.LayerId == Guid.Empty ? item.Id : item.LayerId) != ownLayer &&
                !project.Transitions.Any(transition => transition.OutgoingId == item.Id)))
            {
                string name = outgoing.Text.Replace('\r', ' ').Replace('\n', ' ');
                if (name.Length > 22) name = name[..22] + "…";
                AddTrackTransitionChoices(menu, kind, outgoing.Id, incoming.Id,
                    $"Transition from {name}");
            }
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuFlyoutItem
            {
                Text = "No touching clip on another track",
                IsEnabled = false,
            });
    }

    private void AddTrackTransitionChoices(MenuFlyout menu, TimelineKind kind,
        Guid outgoingId, Guid incomingId, string label)
    {
        VideoTransitionLane lane = kind == TimelineKind.Image
            ? VideoTransitionLane.Image : VideoTransitionLane.Text;
        var submenu = new MenuFlyoutSubItem { Text = label };
        foreach (VideoTransitionEffect effect in Enum.GetValues<VideoTransitionEffect>())
        {
            if (!VideoTransitionGeometry.CanRender(effect, lane)) continue;
            var item = new MenuFlyoutItem { Text = TransitionName(effect) };
            item.Click += (_, _) => JoinTrackAndAddTransition(kind,
                outgoingId, incomingId, effect);
            submenu.Items.Add(item);
        }
        if (submenu.Items.Count != 0) menu.Items.Add(submenu);
    }

    private void JoinTrackAndAddTransition(TimelineKind kind, Guid outgoingId,
        Guid incomingId, VideoTransitionEffect effect)
    {
        if (_editor is null || !FlushCanvasTextEdit() ||
            !ApplyPendingInspectorChanges()) return;
        VideoProject project = _editor.Current;
        if (kind == TimelineKind.Image)
        {
            ImageOverlay? outgoing = project.ImageOverlays.FirstOrDefault(item =>
                item.Id == outgoingId);
            ImageOverlay? incoming = project.ImageOverlays.FirstOrDefault(item =>
                item.Id == incomingId);
            if (outgoing is null || incoming is null || outgoing.End != incoming.Start) return;
            Guid layer = ImageLayerKey(outgoing);
            if (!TryEdit(() => _editor.UpdateImageOverlay(incoming with
                { LayerId = layer, ZIndex = outgoing.ZIndex }))) return;
            ApplyTransitionChoice(VideoTransitionLane.Image, outgoingId, incomingId, effect);
        }
        else
        {
            TextOverlay? outgoing = project.TextOverlays.FirstOrDefault(item =>
                item.Id == outgoingId);
            TextOverlay? incoming = project.TextOverlays.FirstOrDefault(item =>
                item.Id == incomingId);
            if (outgoing is null || incoming is null || outgoing.End != incoming.Start) return;
            Guid layer = outgoing.LayerId == Guid.Empty ? outgoing.Id : outgoing.LayerId;
            if (!TryEdit(() => _editor.UpdateTextOverlay(incoming with
                { LayerId = layer, ZIndex = outgoing.ZIndex }))) return;
            ApplyTransitionChoice(VideoTransitionLane.Text, outgoingId, incomingId, effect);
        }
    }
}
