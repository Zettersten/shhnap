using System.Globalization;
using Microsoft.Graphics.Canvas.Text;
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
    private bool _textInspectorDirty;
    private string[]? _textFontFamilies;
    private sealed record TransitionCut(VideoTransitionLane Lane, Guid OutgoingId,
        Guid IncomingId, TimeSpan At);
    private VideoProject? _transitionCutsSnapshot;
    private TransitionCut[] _transitionCuts = [];

    private void InitializeTransitionControls()
    {
        foreach (VideoTransitionEffect effect in Enum.GetValues<VideoTransitionEffect>())
        {
            var item = new ComboBoxItem
            {
                Content = TransitionName(effect),
                Tag = effect,
                IsEnabled = VideoTransitionGeometry.CanRender(effect, VideoTransitionLane.Video),
            };
            TransitionEffectBox.Items.Add(item);
        }
    }

    private void AddText_Click(object sender, RoutedEventArgs args)
    {
        if (_editor is null || _closed) return;
        _player.Pause();
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
            RefreshCanvasTextEditor();
            CanvasTextBox.Focus(FocusState.Programmatic);
            CanvasTextBox.SelectAll();
        });
    }

    private void AddText_Invoked(KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_closed || _editor is null || IsTextEntryFocused()) return;
        args.Handled = true;
        AddText_Click(this, new RoutedEventArgs());
    }

    private TransitionCut? FindTransitionCut()
    {
        if (_editor is null) return null;
        VideoProject project = _editor.Current;
        if (!ReferenceEquals(_transitionCutsSnapshot, project))
        {
            var cuts = new List<TransitionCut>();
            VideoSegment[] videos = [.. project.VideoSegments.OrderBy(item => item.Start)];
            for (int index = 0; index + 1 < videos.Length; index++)
                if (videos[index].End == videos[index + 1].Start)
                    cuts.Add(new TransitionCut(VideoTransitionLane.Video,
                        videos[index].Id, videos[index + 1].Id, videos[index].End));
            foreach (var layer in project.ImageOverlays.GroupBy(ImageLayerKey))
            {
                ImageOverlay[] items = [.. layer.OrderBy(item => item.Start)];
                for (int index = 0; index + 1 < items.Length; index++)
                    if (items[index].End == items[index + 1].Start)
                        cuts.Add(new TransitionCut(VideoTransitionLane.Image,
                            items[index].Id, items[index + 1].Id, items[index].End));
            }
            foreach (var layer in project.TextOverlays.GroupBy(item =>
                item.LayerId == Guid.Empty ? item.Id : item.LayerId))
            {
                TextOverlay[] items = [.. layer.OrderBy(item => item.Start)];
                for (int index = 0; index + 1 < items.Length; index++)
                    if (items[index].End == items[index + 1].Start)
                        cuts.Add(new TransitionCut(VideoTransitionLane.Text,
                            items[index].Id, items[index + 1].Id, items[index].End));
            }
            _transitionCuts = [.. cuts];
            _transitionCutsSnapshot = project;
        }
        if (_selectedKind == TimelineKind.Transition)
        {
            VideoTransition? selected = project.Transitions.FirstOrDefault(item =>
                item.Id == _selectedId);
            if (selected is not null)
                return _transitionCuts.FirstOrDefault(cut =>
                    cut.Lane == selected.Lane &&
                    cut.OutgoingId == selected.OutgoingId &&
                    cut.IncomingId == selected.IncomingId);
        }
        double tolerance = 1d / Math.Clamp(project.FrameRate, 1, 240);
        foreach (TransitionCut cut in _transitionCuts)
            if (Math.Abs((_playhead - cut.At).TotalSeconds) <= tolerance)
                return cut;
        VideoTransitionLane? selectedLane = _selectedKind switch
        {
            TimelineKind.Video => VideoTransitionLane.Video,
            TimelineKind.Image => VideoTransitionLane.Image,
            TimelineKind.Text => VideoTransitionLane.Text,
            _ => null,
        };
        if (selectedLane is not null)
        {
            TransitionCut? nearest = null;
            long nearestDistance = long.MaxValue;
            foreach (TransitionCut cut in _transitionCuts)
            {
                if (cut.Lane != selectedLane) continue;
                if (_selectedId != cut.OutgoingId && _selectedId != cut.IncomingId)
                    continue;
                long distance = Math.Abs((_playhead - cut.At).Ticks);
                if (distance >= nearestDistance) continue;
                nearest = cut;
                nearestDistance = distance;
            }
            return nearest;
        }
        return null;
    }

    private void AddTransition_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button || FindTransitionCut() is not { } cut) return;
        ShowTransitionMenu(button, cut.Lane, cut.OutgoingId, cut.IncomingId);
    }

    private void AddTransition_Invoked(KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_closed || _editor is null || IsNativeControlFocused() ||
            !AddTransitionButton.IsEnabled) return;
        args.Handled = true;
        if (FindTransitionCut() is { } cut)
            ApplyTransitionChoice(cut.Lane, cut.OutgoingId, cut.IncomingId,
                VideoTransitionEffect.Fade);
    }

    private void ShowTransitionMenu(Button anchor, VideoTransitionLane lane,
        Guid outgoingId, Guid incomingId)
    {
        if (_editor is null) return;
        var menu = new MenuFlyout();
        foreach (VideoTransitionEffect effect in Enum.GetValues<VideoTransitionEffect>())
        {
            if (!VideoTransitionGeometry.CanRender(effect, lane)) continue;
            var item = new MenuFlyoutItem { Text = TransitionName(effect) };
            item.Click += (_, _) => ApplyTransitionChoice(lane, outgoingId, incomingId,
                effect);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.ShowAt(anchor);
    }

    private void ApplyTransitionChoice(VideoTransitionLane lane, Guid outgoingId,
        Guid incomingId, VideoTransitionEffect effect)
    {
        if (_editor is null) return;
        VideoProject project = _editor.Current;
        VideoTransition? existing = project.Transitions.FirstOrDefault(item =>
            item.Lane == lane && item.OutgoingId == outgoingId &&
            item.IncomingId == incomingId);
        Guid id = existing?.Id ?? Guid.Empty;
        TimeSpan duration = existing?.Duration ??
            DefaultTransitionDuration(project, lane, outgoingId, incomingId);
        if (!TryEdit(() =>
        {
            if (existing is null)
                id = _editor.AddTransition(lane, outgoingId, incomingId,
                    effect, duration);
            else
                _editor.UpdateTransition(existing with { Effect = effect });
        })) return;
        _selectedKind = TimelineKind.Transition;
        _selectedId = id;
        RefreshTimeline();
        RefreshInspector();
        UpdateEditActionState();
        StatusText.Text = $"{TransitionName(effect)} added at cut";
    }

    private static TimeSpan DefaultTransitionDuration(VideoProject project,
        VideoTransitionLane lane, Guid outgoingId, Guid incomingId)
    {
        double outgoing = lane switch
        {
            VideoTransitionLane.Video => project.VideoSegments.First(item => item.Id == outgoingId).Duration.TotalSeconds,
            VideoTransitionLane.Image => project.ImageOverlays.First(item => item.Id == outgoingId).Duration.TotalSeconds,
            _ => project.TextOverlays.First(item => item.Id == outgoingId).Duration.TotalSeconds,
        };
        double incoming = lane switch
        {
            VideoTransitionLane.Video => project.VideoSegments.First(item => item.Id == incomingId).Duration.TotalSeconds,
            VideoTransitionLane.Image => project.ImageOverlays.First(item => item.Id == incomingId).Duration.TotalSeconds,
            _ => project.TextOverlays.First(item => item.Id == incomingId).Duration.TotalSeconds,
        };
        return TimeSpan.FromSeconds(Math.Min(.6, 2 * Math.Min(outgoing, incoming)));
    }

    private void TransitionEffect_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingInspector || _editor is null ||
            _selectedKind != TimelineKind.Transition ||
            TransitionEffectBox.SelectedItem is not ComboBoxItem { Tag: VideoTransitionEffect effect } ||
            _editor.Current.Transitions.FirstOrDefault(item => item.Id == _selectedId)
                is not { } selected ||
            !VideoTransitionGeometry.CanRender(effect, selected.Lane)) return;
        double maximum = MaximumTransitionDuration(_editor.Current, selected, effect);
        if (maximum <= 0) return;
        TryEdit(() => _editor.UpdateTransition(selected with
        {
            Effect = effect,
            Duration = TimeSpan.FromSeconds(Math.Min(selected.Duration.TotalSeconds, maximum)),
        }));
    }

    private void TransitionDuration_Changed(NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (_loadingInspector || _editor is null ||
            _selectedKind != TimelineKind.Transition ||
            !double.IsFinite(sender.Value) || sender.Value <= 0) return;
        VideoTransition? selected = _editor.Current.Transitions.FirstOrDefault(item =>
            item.Id == _selectedId);
        if (selected is null) return;
        TryEdit(() => _editor.UpdateTransition(selected with
        {
            Duration = TimeSpan.FromSeconds(sender.Value),
        }));
    }

    private static double MaximumTransitionDuration(VideoProject project,
        VideoTransition selected, VideoTransitionEffect effect)
    {
        double outgoing = selected.Lane switch
        {
            VideoTransitionLane.Video => project.VideoSegments.First(item =>
                item.Id == selected.OutgoingId).Duration.TotalSeconds,
            VideoTransitionLane.Image => project.ImageOverlays.First(item =>
                item.Id == selected.OutgoingId).Duration.TotalSeconds,
            _ => project.TextOverlays.First(item =>
                item.Id == selected.OutgoingId).Duration.TotalSeconds,
        };
        double incoming = selected.Lane switch
        {
            VideoTransitionLane.Video => project.VideoSegments.First(item =>
                item.Id == selected.IncomingId).Duration.TotalSeconds,
            VideoTransitionLane.Image => project.ImageOverlays.First(item =>
                item.Id == selected.IncomingId).Duration.TotalSeconds,
            _ => project.TextOverlays.First(item =>
                item.Id == selected.IncomingId).Duration.TotalSeconds,
        };
        foreach (VideoTransition other in project.Transitions)
        {
            if (other.Id == selected.Id ||
                !VideoTransitionGeometry.TryGetSpan(project, other,
                    out VideoTransitionSpan span)) continue;
            if (other.OutgoingId == selected.OutgoingId)
                outgoing -= (span.Cut - span.Start).TotalSeconds;
            if (other.IncomingId == selected.OutgoingId)
                outgoing -= (span.End - span.Cut).TotalSeconds;
            if (other.OutgoingId == selected.IncomingId)
                incoming -= (span.Cut - span.Start).TotalSeconds;
            if (other.IncomingId == selected.IncomingId)
                incoming -= (span.End - span.Cut).TotalSeconds;
        }
        double presetLimit = effect is VideoTransitionEffect.DipToBlack or
            VideoTransitionEffect.DipToWhite ? 10 : 2;
        return Math.Max(0, Math.Min(presetLimit, 2 * Math.Min(outgoing, incoming)));
    }

    private void TextContent_Changed(object sender, TextChangedEventArgs args)
    {
        if (_loadingInspector || _selectedKind != TimelineKind.Text) return;
        if (TextCanvas.Visibility == Visibility.Visible &&
            CanvasTextBox.Text != TextContentBox.Text)
        {
            _canvasTextSyncing = true;
            CanvasTextBox.Text = TextContentBox.Text;
            _canvasTextSyncing = false;
        }
        MarkInspectorDirty();
        ScheduleTextAutoApply();
    }

    private void TextAlignment_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingInspector || _selectedKind != TimelineKind.Text) return;
        PreviewCanvasTextStyleFromInspector();
        MarkInspectorDirty();
        ScheduleTextAutoApply();
    }

    private void TextColor_Changed(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (TextColorSwatch is not null)
            TextColorSwatch.Background = new SolidColorBrush(args.NewColor);
        if (_loadingInspector || _selectedKind != TimelineKind.Text) return;
        PreviewCanvasTextStyleFromInspector();
        MarkInspectorDirty();
        ScheduleTextAutoApply();
    }

    private void TextFontFamily_GotFocus(object sender, RoutedEventArgs args)
    {
        _textFontFamilies ??= LoadTextFontFamilies();
        TextFontFamilyBox.ItemsSource = _textFontFamilies;
        TextFontFamilyBox.IsSuggestionListOpen = _textFontFamilies.Length > 0;
    }

    private static string[] LoadTextFontFamilies()
    {
        string[] installed;
        try { installed = [.. CanvasTextFormat.GetSystemFontFamilies()]; }
        catch { installed = []; }
        string[] preferred = ["Segoe UI Variable Display", "Arial", "Impact",
            "Georgia", "Consolas"];
        return preferred.Concat(installed)
            .Where(name => !string.IsNullOrWhiteSpace(name) && !name.StartsWith('@'))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private void TextFontFamily_Changed(AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_loadingInspector) return;
        _textFontFamilies ??= LoadTextFontFamilies();
        string query = sender.Text.Trim();
        sender.ItemsSource = query.Length == 0 ? _textFontFamilies :
            _textFontFamilies.Where(name => name.Contains(query,
                StringComparison.CurrentCultureIgnoreCase)).Take(40).ToArray();
        if (_selectedKind != TimelineKind.Text ||
            !_textFontFamilies.Contains(query, StringComparer.CurrentCultureIgnoreCase)) return;
        PreviewCanvasTextStyleFromInspector();
        MarkInspectorDirty();
        ScheduleTextAutoApply();
    }

    private void TextFontFamily_Submitted(AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        string chosen = (args.ChosenSuggestion as string ?? args.QueryText).Trim();
        _textFontFamilies ??= LoadTextFontFamilies();
        string? installed = _textFontFamilies.FirstOrDefault(name =>
            string.Equals(name, chosen, StringComparison.CurrentCultureIgnoreCase));
        if (installed is null)
        {
            StatusText.Text = "Choose an installed font.";
            return;
        }
        sender.Text = installed;
        sender.IsSuggestionListOpen = false;
        PreviewCanvasTextStyleFromInspector();
        MarkInspectorDirty();
        ScheduleTextAutoApply();
    }

    private void TextStyle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_loadingInspector || _selectedKind != TimelineKind.Text) return;
        TextBackgroundColorButton.IsEnabled = TextBackgroundEnabled.IsChecked == true;
        TextBorderColorButton.IsEnabled = TextBorderEnabled.IsChecked == true;
        TextBorderWidthBox.IsEnabled = TextBorderEnabled.IsChecked == true;
        PreviewCanvasTextStyleFromInspector();
        MarkInspectorDirty();
        ScheduleTextAutoApply();
    }

    private void TextStyleNumber_Changed(NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (_loadingInspector || _selectedKind != TimelineKind.Text) return;
        PreviewCanvasTextStyleFromInspector();
        MarkInspectorDirty();
        ScheduleTextAutoApply();
    }

    private void TextBackgroundColor_Changed(ColorPicker sender,
        ColorChangedEventArgs args)
    {
        if (TextBackgroundSwatch is not null)
            TextBackgroundSwatch.Background = new SolidColorBrush(args.NewColor);
        if (_loadingInspector || _selectedKind != TimelineKind.Text) return;
        PreviewCanvasTextStyleFromInspector();
        MarkInspectorDirty();
        ScheduleTextAutoApply();
    }

    private void TextBorderColor_Changed(ColorPicker sender,
        ColorChangedEventArgs args)
    {
        if (TextBorderSwatch is not null)
            TextBorderSwatch.Background = new SolidColorBrush(args.NewColor);
        if (_loadingInspector || _selectedKind != TimelineKind.Text) return;
        PreviewCanvasTextStyleFromInspector();
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
            await Task.Delay(180, token);
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
