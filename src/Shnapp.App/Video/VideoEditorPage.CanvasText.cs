using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Shnapp.Core;
using Windows.System;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    private readonly DispatcherTimer _canvasTextCommitTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(400),
    };
    private Guid _canvasTextId;
    private bool _canvasTextSyncing;
    private bool _canvasTextDirty;
    private VideoRect? _canvasTextDraftBounds;
    private CanvasTextGesture _canvasTextGesture;
    private uint? _canvasTextPointerId;
    private global::Windows.Foundation.Point _canvasTextPointerStart;
    private VideoRect _canvasTextPointerBounds;

    private enum CanvasTextGesture { None, Move, Resize }

    private void InitializeCanvasText()
    {
        _canvasTextCommitTimer.Tick += CanvasTextCommitTimer_Tick;
    }

    private void ReleaseCanvasText()
    {
        _canvasTextCommitTimer.Stop();
        _canvasTextCommitTimer.Tick -= CanvasTextCommitTimer_Tick;
        TextCanvas.Visibility = Visibility.Collapsed;
        _canvasTextId = Guid.Empty;
        _canvasTextPointerId = null;
    }

    private void PreviewText_Tapped(object sender, TappedRoutedEventArgs args)
    {
        if (sender is not Image { Tag: Guid id } || _editor is null) return;
        SelectClip(TimelineKind.Text, id);
        RefreshCanvasTextEditor();
        if (TextCanvas.Visibility == Visibility.Visible)
            CanvasTextBox.Focus(FocusState.Programmatic);
        args.Handled = true;
    }

    private void RefreshCanvasTextEditor()
    {
        if (_closed || _editor is null || _selectedKind != TimelineKind.Text ||
            !_visiblePreviewText.TryGetValue(_selectedId, out TextOverlay? overlay))
        {
            TextCanvas.Visibility = Visibility.Collapsed;
            SetSelectedTextRasterVisibility();
            return;
        }

        bool changedSelection = _canvasTextId != overlay.Id;
        _canvasTextId = overlay.Id;
        _canvasTextSyncing = true;
        if (changedSelection || !_canvasTextDirty && CanvasTextBox.Text != overlay.Text)
            CanvasTextBox.Text = overlay.Text;
        CanvasBoldButton.IsChecked = overlay.Bold;
        CanvasItalicButton.IsChecked = overlay.Italic;
        CanvasBackgroundButton.IsChecked = overlay.BackgroundColor is not null;
        _canvasTextSyncing = false;

        TextCanvas.Visibility = Visibility.Visible;
        ApplyCanvasTextStyle(overlay);
        UpdateCanvasTextBounds();
        SetSelectedTextRasterVisibility();
    }

    private void SetSelectedTextRasterVisibility()
    {
        foreach ((Guid id, Image view) in _previewTextViews)
            view.Visibility = TextCanvas.Visibility == Visibility.Visible &&
                id == _canvasTextId ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateCanvasTextBounds()
    {
        if (_editor is null || !double.IsFinite(VideoPlayer.Width) ||
            VideoPlayer.Width <= 0 || VideoPlayer.Height <= 0) return;
        VideoProject project = _editor.Current;
        TextCanvas.Width = VideoPlayer.Width;
        TextCanvas.Height = VideoPlayer.Height;
        TextCanvas.Clip = new RectangleGeometry
        {
            Rect = new global::Windows.Foundation.Rect(0, 0,
                VideoPlayer.Width, VideoPlayer.Height),
        };
        double scaleX = VideoPlayer.Width / project.Width;
        double scaleY = VideoPlayer.Height / project.Height;
        Canvas.SetLeft(TextSafeArea, VideoPlayer.Width * .05);
        Canvas.SetTop(TextSafeArea, VideoPlayer.Height * .05);
        TextSafeArea.Width = VideoPlayer.Width * .9;
        TextSafeArea.Height = VideoPlayer.Height * .9;

        if (TextCanvas.Visibility != Visibility.Visible ||
            !_visiblePreviewText.TryGetValue(_canvasTextId, out TextOverlay? overlay))
            return;
        VideoRect bounds = _canvasTextDraftBounds ?? overlay.Bounds;
        double left = bounds.X * scaleX;
        double top = bounds.Y * scaleY;
        double width = bounds.Width * scaleX;
        double height = bounds.Height * scaleY;
        Canvas.SetLeft(CanvasTextFrame, left);
        Canvas.SetTop(CanvasTextFrame, top);
        CanvasTextFrame.Width = width;
        CanvasTextFrame.Height = height;
        Canvas.SetLeft(CanvasTextMoveGrip, left + (width - CanvasTextMoveGrip.Width) / 2);
        Canvas.SetTop(CanvasTextMoveGrip, top >= 19 ? top - 18 : top + height + 3);
        Canvas.SetLeft(CanvasTextResizeGrip, left + width - 7);
        Canvas.SetTop(CanvasTextResizeGrip, top + height - 7);
        CanvasTextQuickBar.Width = Math.Min(224, Math.Max(1, VideoPlayer.Width));
        Canvas.SetLeft(CanvasTextQuickBar,
            Math.Clamp(left, 0, Math.Max(0, VideoPlayer.Width - CanvasTextQuickBar.Width)));
        double quickBarTop = top >= 56 ? top - 55 : top + height + 22;
        Canvas.SetTop(CanvasTextQuickBar,
            Math.Clamp(quickBarTop, 0, Math.Max(0, VideoPlayer.Height - CanvasTextQuickBar.Height)));
        CanvasTextBox.FontSize = Math.Max(8, overlay.FontSize * Math.Min(scaleX, scaleY));
        CanvasTextBox.Padding = new Thickness(overlay.Padding * Math.Min(scaleX, scaleY));
    }

    private void ApplyCanvasTextStyle(TextOverlay overlay)
    {
        if (_editor is null) return;
        double scale = Math.Min(VideoPlayer.Width / _editor.Current.Width,
            VideoPlayer.Height / _editor.Current.Height);
        CanvasTextBox.FontFamily = new FontFamily(overlay.FontFamily);
        CanvasTextBox.FontWeight = new global::Windows.UI.Text.FontWeight
        {
            Weight = overlay.Bold ? (ushort)700 : (ushort)400,
        };
        CanvasTextBox.FontStyle = overlay.Italic
            ? global::Windows.UI.Text.FontStyle.Italic
            : global::Windows.UI.Text.FontStyle.Normal;
        CanvasTextBox.FontSize = Math.Max(8, overlay.FontSize * scale);
        CanvasTextBox.Foreground = new SolidColorBrush(ParseCanvasColor(overlay.Color));
        CanvasTextBox.TextAlignment = overlay.Alignment switch
        {
            TextOverlayAlignment.Left => TextAlignment.Left,
            TextOverlayAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Center,
        };
        CanvasTextBox.Padding = new Thickness(overlay.Padding * scale);
        CanvasTextStyleFrame.Background = overlay.BackgroundColor is { } background
            ? new SolidColorBrush(ParseCanvasColor(background)) : new SolidColorBrush(global::Microsoft.UI.Colors.Transparent);
        CanvasTextStyleFrame.BorderBrush = overlay.BorderColor is { } border
            ? new SolidColorBrush(ParseCanvasColor(border)) : new SolidColorBrush(global::Microsoft.UI.Colors.Transparent);
        CanvasTextStyleFrame.BorderThickness = new Thickness(overlay.BorderThickness * scale);
        CanvasTextStyleFrame.CornerRadius = new CornerRadius(overlay.CornerRadius * scale);
    }

    private void PreviewCanvasTextStyleFromInspector()
    {
        if (_editor is null || _selectedKind != TimelineKind.Text ||
            _editor.Current.TextOverlays.FirstOrDefault(item => item.Id == _selectedId) is not { } overlay)
            return;
        string family = TextFontFamilyBox.Text.Trim();
        if (_textFontFamilies is not null &&
            !_textFontFamilies.Contains(family, StringComparer.CurrentCultureIgnoreCase))
            family = overlay.FontFamily;
        if (family.Length == 0) family = overlay.FontFamily;
        TextOverlay draft = overlay with
        {
            FontFamily = family,
            Bold = TextBoldButton.IsChecked == true,
            Italic = TextItalicButton.IsChecked == true,
            FontSize = double.IsFinite(TextFontSize.Value)
                ? Math.Clamp(TextFontSize.Value, 8, 512) : overlay.FontSize,
            Color = FormatTextColor(TextColorPicker.Color),
            Alignment = (TextOverlayAlignment)Math.Max(0, TextAlignmentBox.SelectedIndex),
            BackgroundColor = TextBackgroundEnabled.IsChecked == true
                ? FormatCanvasColor(TextBackgroundColorPicker.Color) : null,
            Padding = double.IsFinite(TextPaddingBox.Value)
                ? Math.Clamp(TextPaddingBox.Value, 0, 64) : overlay.Padding,
            BorderColor = TextBorderEnabled.IsChecked == true
                ? FormatCanvasColor(TextBorderColorPicker.Color) : null,
            BorderThickness = TextBorderEnabled.IsChecked == true &&
                double.IsFinite(TextBorderWidthBox.Value)
                ? Math.Clamp(TextBorderWidthBox.Value, 0, 24) : 0,
            CornerRadius = double.IsFinite(TextCornerRadiusBox.Value)
                ? Math.Clamp(TextCornerRadiusBox.Value, 0, 64) : overlay.CornerRadius,
        };
        ApplyCanvasTextStyle(draft);
        _canvasTextSyncing = true;
        CanvasBoldButton.IsChecked = draft.Bold;
        CanvasItalicButton.IsChecked = draft.Italic;
        CanvasBackgroundButton.IsChecked = draft.BackgroundColor is not null;
        _canvasTextSyncing = false;
    }

    private static global::Windows.UI.Color ParseCanvasColor(string hex)
    {
        if (hex.Length == 9 && hex[0] == '#')
            return global::Windows.UI.Color.FromArgb(
                Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16),
                Convert.ToByte(hex.Substring(5, 2), 16),
                Convert.ToByte(hex.Substring(7, 2), 16));
        if (hex.Length == 7 && hex[0] == '#')
            return global::Windows.UI.Color.FromArgb(255,
                Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16),
                Convert.ToByte(hex.Substring(5, 2), 16));
        return global::Microsoft.UI.Colors.White;
    }

    private static string FormatCanvasColor(global::Windows.UI.Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private void CanvasTextBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (_canvasTextSyncing || _editor is null || _canvasTextId == Guid.Empty) return;
        _canvasTextDirty = true;
        _loadingInspector = true;
        if (TextContentBox.Text != CanvasTextBox.Text)
            TextContentBox.Text = CanvasTextBox.Text;
        _loadingInspector = false;
        _canvasTextCommitTimer.Stop();
        _canvasTextCommitTimer.Start();
    }

    private void CanvasTextCommitTimer_Tick(object? sender, object args)
    {
        _canvasTextCommitTimer.Stop();
        if (!string.IsNullOrWhiteSpace(CanvasTextBox.Text)) FlushCanvasTextEdit();
    }

    private bool FlushCanvasTextEdit()
    {
        _canvasTextCommitTimer.Stop();
        if (!_canvasTextDirty || _editor is null || _canvasTextId == Guid.Empty)
            return true;
        if (string.IsNullOrWhiteSpace(CanvasTextBox.Text))
        {
            StatusText.Text = "Enter text or remove this layer.";
            return false;
        }
        if (_textInspectorDirty && _selectedKind == TimelineKind.Text &&
            _selectedId == _canvasTextId && !ApplyPendingInspectorChanges())
            return false;
        TextOverlay? overlay = _editor.Current.TextOverlays.FirstOrDefault(item => item.Id == _canvasTextId);
        if (overlay is null) return true;
        string text = CanvasTextBox.Text;
        if (overlay.Text != text && !TryEdit(() =>
                _editor.UpdateTextOverlay(overlay with { Text = text })))
            return false;
        _canvasTextDirty = false;
        return true;
    }

    private void CanvasTextBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape) return;
        if (FlushCanvasTextEdit())
            PlayPauseButton.Focus(FocusState.Programmatic);
        args.Handled = true;
    }

    private void CanvasTextGrip_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Border grip || _editor is null ||
            !_visiblePreviewText.TryGetValue(_canvasTextId, out TextOverlay? overlay) ||
            !grip.CapturePointer(args.Pointer)) return;
        _canvasTextGesture = ReferenceEquals(grip, CanvasTextResizeGrip)
            ? CanvasTextGesture.Resize : CanvasTextGesture.Move;
        _canvasTextPointerId = args.Pointer.PointerId;
        _canvasTextPointerStart = args.GetCurrentPoint(TextCanvas).Position;
        _canvasTextPointerBounds = overlay.Bounds;
        args.Handled = true;
    }

    private void CanvasTextGrip_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_editor is null || _canvasTextPointerId != args.Pointer.PointerId ||
            _canvasTextGesture == CanvasTextGesture.None) return;
        VideoProject project = _editor.Current;
        var position = args.GetCurrentPoint(TextCanvas).Position;
        double dx = (position.X - _canvasTextPointerStart.X) * project.Width / VideoPlayer.Width;
        double dy = (position.Y - _canvasTextPointerStart.Y) * project.Height / VideoPlayer.Height;
        VideoRect old = _canvasTextPointerBounds;
        _canvasTextDraftBounds = _canvasTextGesture == CanvasTextGesture.Move
            ? old with
            {
                X = Math.Clamp(old.X + dx, -65_536, 65_536),
                Y = Math.Clamp(old.Y + dy, -65_536, 65_536),
            }
            : old with
            {
                Width = Math.Clamp(old.Width + dx, 16, 65_536),
                Height = Math.Clamp(old.Height + dy, 16, 65_536),
            };
        UpdateCanvasTextBounds();
        args.Handled = true;
    }

    private void CanvasTextGrip_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_canvasTextPointerId != args.Pointer.PointerId) return;
        VideoRect? bounds = _canvasTextDraftBounds;
        _canvasTextPointerId = null;
        _canvasTextGesture = CanvasTextGesture.None;
        _canvasTextDraftBounds = null;
        if (sender is Border grip) grip.ReleasePointerCapture(args.Pointer);
        if (_editor is not null && bounds is { } target &&
            _editor.Current.TextOverlays.FirstOrDefault(item => item.Id == _canvasTextId) is { } overlay &&
            target != overlay.Bounds)
            TryEdit(() => _editor.UpdateTextOverlay(overlay with { Bounds = target }));
        UpdateCanvasTextBounds();
        args.Handled = true;
    }

    private void CanvasTextGrip_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_canvasTextPointerId != args.Pointer.PointerId) return;
        _canvasTextPointerId = null;
        _canvasTextGesture = CanvasTextGesture.None;
        _canvasTextDraftBounds = null;
        UpdateCanvasTextBounds();
    }

    private void CanvasTextStyle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_canvasTextSyncing || _editor is null || _canvasTextId == Guid.Empty) return;
        if (!FlushCanvasTextEdit()) return;
        if (_editor.Current.TextOverlays.FirstOrDefault(item => item.Id == _canvasTextId) is not { } overlay)
            return;
        TextOverlay changed = overlay with
        {
            Bold = CanvasBoldButton.IsChecked == true,
            Italic = CanvasItalicButton.IsChecked == true,
            BackgroundColor = CanvasBackgroundButton.IsChecked == true
                ? overlay.BackgroundColor ?? "#CC000000" : null,
        };
        ApplyCanvasTextStyle(changed);
        TryEdit(() => _editor.UpdateTextOverlay(changed));
    }

    private void CanvasTextSmaller_Click(object sender, RoutedEventArgs args) =>
        ChangeCanvasTextSize(-2);

    private void CanvasTextLarger_Click(object sender, RoutedEventArgs args) =>
        ChangeCanvasTextSize(2);

    private void ChangeCanvasTextSize(double change)
    {
        if (_editor is null || !FlushCanvasTextEdit() ||
            _editor.Current.TextOverlays.FirstOrDefault(item => item.Id == _canvasTextId) is not { } overlay)
            return;
        TextOverlay updated = overlay with { FontSize = Math.Clamp(overlay.FontSize + change, 8, 512) };
        ApplyCanvasTextStyle(updated);
        TryEdit(() => _editor.UpdateTextOverlay(updated));
    }

    private void CanvasFont_Click(object sender, RoutedEventArgs args)
    {
        if (_editor is null || !FlushCanvasTextEdit()) return;
        var menu = new MenuFlyout();
        string[] families = ["Segoe UI Variable Display", "Arial", "Impact", "Georgia", "Consolas"];
        foreach (string family in families)
        {
            var item = new MenuFlyoutItem { Text = family };
            item.Click += (_, _) =>
            {
                if (_editor?.Current.TextOverlays.FirstOrDefault(text => text.Id == _canvasTextId)
                    is not { } overlay) return;
                TryEdit(() => _editor.UpdateTextOverlay(overlay with { FontFamily = family }));
            };
            menu.Items.Add(item);
        }
        menu.ShowAt((FrameworkElement)sender);
    }

    private void CanvasPosition_Click(object sender, RoutedEventArgs args)
    {
        if (_editor is null || !FlushCanvasTextEdit()) return;
        Flyout? flyout = null;
        var grid = new Grid { RowSpacing = 3, ColumnSpacing = 3 };
        for (int index = 0; index < 3; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        string[] vertical = ["Top", "Center", "Bottom"];
        string[] horizontal = ["left", "center", "right"];
        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 3; column++)
        {
            int targetRow = row;
            int targetColumn = column;
            var icon = new Grid { Width = 13, Height = 13 };
            for (int i = 0; i < 3; i++)
            {
                icon.RowDefinitions.Add(new RowDefinition());
                icon.ColumnDefinitions.Add(new ColumnDefinition());
            }
            var dot = new Ellipse
            {
                Width = 4, Height = 4,
                Fill = new SolidColorBrush(global::Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(dot, row);
            Grid.SetColumn(dot, column);
            icon.Children.Add(dot);
            var button = new Button
            {
                Width = 28, Height = 28, MinWidth = 0, Padding = new Thickness(0),
                Content = icon,
            };
            AutomationProperties.SetName(button, $"{vertical[row]} {horizontal[column]}");
            ToolTipService.SetToolTip(button, $"{vertical[row]} {horizontal[column]}");
            button.Click += (_, _) =>
            {
                PlaceCanvasText(targetRow, targetColumn);
                flyout?.Hide();
            };
            Grid.SetRow(button, row);
            Grid.SetColumn(button, column);
            grid.Children.Add(button);
        }
        flyout = new Flyout { Content = grid };
        flyout.ShowAt((FrameworkElement)sender);
    }

    private void PlaceCanvasText(int row, int column)
    {
        if (_editor is null ||
            _editor.Current.TextOverlays.FirstOrDefault(item => item.Id == _canvasTextId) is not { } overlay)
            return;
        VideoProject project = _editor.Current;
        double x = column switch
        {
            0 => project.Width * .05,
            2 => project.Width * .95 - overlay.Bounds.Width,
            _ => (project.Width - overlay.Bounds.Width) / 2,
        };
        double y = row switch
        {
            0 => project.Height * .05,
            2 => project.Height * .95 - overlay.Bounds.Height,
            _ => (project.Height - overlay.Bounds.Height) / 2,
        };
        var bounds = overlay.Bounds with
        {
            X = Math.Clamp(x, 0, Math.Max(0, project.Width - overlay.Bounds.Width)),
            Y = Math.Clamp(y, 0, Math.Max(0, project.Height - overlay.Bounds.Height)),
        };
        TryEdit(() => _editor.UpdateTextOverlay(overlay with { Bounds = bounds }));
    }
}
