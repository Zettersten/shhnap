using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Shnapp.Core;
using Windows.UI;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    private CancellationTokenSource? _textEditCancellation;
    private VideoProject? _dipCutsSnapshot;
    private (VideoSegment Outgoing, VideoSegment Incoming)[] _dipCuts = [];

    private void AddText_Click(object sender, RoutedEventArgs args)
    {
        if (_editor is null || _closed) return;
        VideoProject project = _editor.Current;
        double width = project.Width * .8;
        double height = Math.Clamp(project.Height * .17, 64, project.Height * .3);
        var overlay = new TextOverlay(Guid.NewGuid(), _playhead, TimeSpan.FromSeconds(3),
            "Text", new VideoRect(project.Width * .1, project.Height * .7, width, height),
            Math.Clamp(project.Height / 18d, 24, 96));
        if (!TryEdit(() => _editor.AddTextOverlay(overlay))) return;
        _selectedKind = TimelineKind.Text;
        _selectedId = overlay.Id;
        RefreshTimeline();
        RefreshInspector();
        UpdateEditActionState();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || _selectedId != overlay.Id) return;
            TextContentBox.Focus(FocusState.Programmatic);
            TextContentBox.SelectAll();
        });
    }

    private void AddText_Invoked(KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_closed || _editor is null || IsTextEntryFocused()) return;
        args.Handled = true;
        AddText_Click(this, new RoutedEventArgs());
    }

    private VideoSegment? FindDipCut()
    {
        if (_editor is null) return null;
        VideoProject project = _editor.Current;
        if (!ReferenceEquals(_dipCutsSnapshot, project))
        {
            VideoSegment[] clips = project.VideoSegments.OrderBy(item => item.Start).ToArray();
            var cuts = new List<(VideoSegment, VideoSegment)>();
            for (int index = 0; index + 1 < clips.Length; index++)
                if (clips[index].End == clips[index + 1].Start)
                    cuts.Add((clips[index], clips[index + 1]));
            _dipCuts = [.. cuts];
            _dipCutsSnapshot = project;
        }
        double tolerance = 1d / Math.Clamp(project.FrameRate, 1, 240);
        foreach (var cut in _dipCuts)
            if (Math.Abs((_playhead - cut.Outgoing.End).TotalSeconds) <= tolerance)
                return cut.Outgoing;
        if (_selectedKind == TimelineKind.Video)
        {
            VideoSegment? nearest = null;
            long nearestDistance = long.MaxValue;
            foreach (var cut in _dipCuts)
            {
                if (_selectedId != cut.Outgoing.Id && _selectedId != cut.Incoming.Id)
                    continue;
                long distance = Math.Abs((_playhead - cut.Outgoing.End).Ticks);
                if (distance >= nearestDistance) continue;
                nearest = cut.Outgoing;
                nearestDistance = distance;
            }
            return nearest;
        }
        return null;
    }

    private void AddTransition_Click(object sender, RoutedEventArgs args)
    {
        if (_editor is null || FindDipCut() is not { } outgoing) return;
        if (TryEdit(() => _editor.ApplyDipToBlackAtCut(outgoing.Id,
            TimeSpan.FromSeconds(.35))))
            StatusText.Text = "Dip to black added at cut";
    }

    private void AddTransition_Invoked(KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_closed || _editor is null || IsNativeControlFocused() ||
            !AddTransitionButton.IsEnabled) return;
        args.Handled = true;
        AddTransition_Click(this, new RoutedEventArgs());
    }

    private void TextContent_Changed(object sender, TextChangedEventArgs args)
    {
        if (_loadingInspector || _selectedKind != TimelineKind.Text) return;
        MarkInspectorDirty();
        ScheduleTextAutoApply();
    }

    private void TextAlignment_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingInspector || _selectedKind != TimelineKind.Text) return;
        MarkInspectorDirty();
        ScheduleTextAutoApply();
    }

    private void TextColor_Changed(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (TextColorSwatch is not null)
            TextColorSwatch.Background = new SolidColorBrush(args.NewColor);
        if (_loadingInspector || _selectedKind != TimelineKind.Text) return;
        MarkInspectorDirty();
        ScheduleTextAutoApply();
    }

    private void ScheduleTextAutoApply()
    {
        _textEditCancellation?.Cancel();
        _textEditCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _textEditCancellation = cancellation;
        Guid selectedId = _selectedId;
        string text = TextContentBox.Text;
        _ = AutoApplyTextAsync(selectedId, text, cancellation.Token);
    }

    private async Task AutoApplyTextAsync(Guid selectedId, string text,
        CancellationToken token)
    {
        try
        {
            await Task.Delay(550, token);
            if (token.IsCancellationRequested || _closed) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed || token.IsCancellationRequested ||
                    _selectedKind != TimelineKind.Text || _selectedId != selectedId ||
                    TextContentBox.Text != text || string.IsNullOrWhiteSpace(text)) return;
                ApplyPendingInspectorChanges();
            });
        }
        catch (OperationCanceledException) { }
    }

    private void CancelTextAutoApply()
    {
        _textEditCancellation?.Cancel();
        _textEditCancellation?.Dispose();
        _textEditCancellation = null;
    }

    private static Color ParseTextColor(string value)
    {
        if (value.Length != 7 || value[0] != '#') return Colors.White;
        return Color.FromArgb(255,
            byte.Parse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static string FormatTextColor(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
