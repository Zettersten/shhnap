using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Shnapp.App.Editor;
using Shnapp.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    private const ulong MaximumImageBytes = 32 * 1024 * 1024;
    private const string GalleryImageFormat = "Shnapp.GalleryImageId";
    private const string GalleryVideoFormat = "Shnapp.GalleryVideoId";
    private const double GalleryThumbSize = 56;
    private const double GalleryThumbSpacing = 6;
    private const double GalleryHiddenOffset = -96;
    private const double GalleryRevealDistance = 28;
    private const double GalleryHideDistance = 122;
    private bool _importing;
    private bool _galleryAvailable;
    private bool _galleryDragging;
    private Button? _galleryPressedButton;
    private Point _galleryPressedPosition;
    private Guid _galleryLastDraggedId;
    private long _galleryLastDropAt;
    private Guid _galleryLastOpenedId;
    private long _galleryLastOpenedAt;
    private int _galleryPage;
    private int _galleryPageSize = 1;
    private double _galleryWheelDelta;
    private double _galleryRevealTarget;
    private long _galleryAnimationTimestamp;
    private bool _galleryAnimationRunning;
    private readonly Dictionary<Grid, double> _galleryTargets = [];
    private readonly UISettings _galleryMotionSettings = new();
    private Button? _galleryHoveredButton;
    private IReadOnlyList<GalleryMedia> _galleryMedia = [];
    private CancellationTokenSource? _galleryThumbnailCancellation;
    private CancellationTokenSource? _galleryImportCancellation;

    private sealed record GalleryMedia(Guid Id, string Title, DateTimeOffset Date, bool IsVideo);
    public event Action<Guid>? ImageRequested;

    private void BuildGallery()
    {
        _galleryMedia = _galleryItems.Select(item => new GalleryMedia(item.Id,
                item.Title, item.ModifiedAt, false))
            .Concat(_videoGalleryItems.Where(item => item.Id != _editor?.Current.Id)
                .Select(item => new GalleryMedia(item.Id, item.Title, item.CreatedAt, true)))
            .OrderByDescending(item => item.Date)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _galleryAvailable = _galleryMedia.Count > 0;
        if (!_galleryAvailable) CloseGallery();
        _galleryPage = 0;
        _galleryWheelDelta = 0;
        RefreshGalleryPage();
    }

    private void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateVideoPlayerBounds(args.NewSize.Width, args.NewSize.Height);
        int pageSize = Math.Clamp((int)((args.NewSize.Height - 88) /
            (GalleryThumbSize + GalleryThumbSpacing)), 1, 6);
        if (pageSize != _galleryPageSize)
        {
            _galleryPageSize = pageSize;
            _galleryPage = Math.Clamp(_galleryPage, 0,
                Math.Max(0, (_galleryMedia.Count - 1) / pageSize));
            RefreshGalleryPage();
        }
    }

    private void RefreshGalleryPage(bool animatePage = false)
    {
        StopGalleryAnimation();
        _galleryThumbnailCancellation?.Cancel();
        _galleryThumbnailCancellation?.Dispose();
        _galleryThumbnailCancellation = new CancellationTokenSource();
        CancellationToken token = _galleryThumbnailCancellation.Token;
        GalleryItemsHost.Children.Clear();
        _galleryTargets.Clear();
        _galleryHoveredButton = null;
        int index = 0;
        foreach (GalleryMedia item in _galleryMedia.Skip(_galleryPage * _galleryPageSize)
            .Take(_galleryPageSize))
        {
            var thumbnail = new Image
            {
                Stretch = Stretch.UniformToFill,
                Width = GalleryThumbSize,
                Height = GalleryThumbSize,
                IsHitTestVisible = false,
            };
            if (!item.IsVideo)
            {
                ShnappSummary? summary = _galleryItems.FirstOrDefault(image => image.Id == item.Id);
                if (summary is not null)
                {
                    string path = _getGalleryPreviewPath?.Invoke(item.Id) ?? string.Empty;
                    thumbnail.Source = new LibraryEntry(summary, path,
                        previewPixelWidth: 160).Preview;
                }
            }
            else
            {
                _ = LoadVideoThumbnailAsync(item.Id, thumbnail, token);
            }

            var preview = new Grid
            {
                Width = GalleryThumbSize,
                Height = GalleryThumbSize,
                Background = new SolidColorBrush(Colors.Transparent),
                Tag = item,
                CanDrag = true,
            };
            preview.Children.Add(new Border
            {
                Background = (Brush)Application.Current.Resources["ShnappPreviewBrush"],
                CornerRadius = new CornerRadius(8),
                IsHitTestVisible = false,
            });
            preview.Children.Add(thumbnail);
            if (item.IsVideo)
            {
                var playBadge = new Border
                {
                    Width = 20,
                    Height = 20,
                    CornerRadius = new CornerRadius(10),
                    Background = new SolidColorBrush(ColorHelper.FromArgb(180, 0, 0, 0)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 3, 3),
                    IsHitTestVisible = false,
                    Child = new FontIcon
                    {
                        Glyph = "\uE768",
                        FontFamily = new FontFamily("Segoe Fluent Icons"),
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Colors.White),
                    },
                };
                preview.Children.Add(playBadge);
            }
            var button = new Button
            {
                Content = preview,
                Tag = item,
                Width = GalleryThumbSize,
                Height = GalleryThumbSize,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = (Brush)Application.Current.Resources["ShnappBlueBrush"],
                BorderThickness = new Thickness(0),
            };
            var transparent = new SolidColorBrush(Colors.Transparent);
            button.Resources["ButtonBackgroundPointerOver"] = transparent;
            button.Resources["ButtonBackgroundPressed"] = transparent;
            button.Resources["ButtonBackgroundDisabled"] = transparent;
            button.Resources["ButtonBorderBrushPointerOver"] = button.BorderBrush;
            button.Resources["ButtonBorderBrushPressed"] = button.BorderBrush;
            string action = item.IsVideo
                ? "Click to open; use + to add its original recording and audio"
                : "Click to open; use + or drag to add at the playhead";
            AutomationProperties.SetName(button, $"{item.Title}, {(item.IsVideo ? "video" : "image")}. {action}.");
            AutomationProperties.SetAutomationId(button, $"VideoGallery_{item.Id:N}");
            ToolTipService.SetToolTip(button, $"{item.Title}\n{action}");
            if (item.IsVideo)
            {
                var actions = new MenuFlyout();
                var open = new MenuFlyoutItem { Text = "Open video" };
                open.Click += (_, _) => VideoRequested?.Invoke(item.Id);
                var insert = new MenuFlyoutItem { Text = "Add original recording at playhead" };
                insert.Click += (_, _) => _ = ImportGalleryVideoAsync(item.Id, _playhead);
                actions.Items.Add(open);
                actions.Items.Add(insert);
                button.ContextFlyout = actions;
            }
            else
            {
                var actions = new MenuFlyout();
                var open = new MenuFlyoutItem { Text = "Open image" };
                open.Click += (_, _) => ImageRequested?.Invoke(item.Id);
                var insert = new MenuFlyoutItem { Text = "Add image layer at playhead" };
                insert.Click += (_, _) => _ = ImportGalleryImageAsync(item.Id);
                actions.Items.Add(open);
                actions.Items.Add(insert);
                button.ContextFlyout = actions;
            }
            button.Click += GalleryItem_Click;
            preview.Tapped += GalleryItem_Tapped;
            button.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(GalleryItem_PointerPressed), true);
            button.AddHandler(UIElement.PointerReleasedEvent,
                new PointerEventHandler(GalleryItem_PointerReleased), true);
            button.PointerEntered += GalleryItem_PointerEntered;
            button.PointerExited += GalleryItem_PointerExited;
            button.GotFocus += GalleryItem_FocusChanged;
            button.LostFocus += GalleryItem_FocusChanged;
            button.PointerMoved += GalleryPanel_PointerMoved;
            button.PointerWheelChanged += GalleryPanel_PointerWheelChanged;
            preview.DragStarting += GalleryItem_DragStarting;
            preview.DropCompleted += GalleryItem_DropCompleted;
            var itemHost = new Grid
            {
                Width = GalleryThumbSize,
                Height = GalleryThumbSize,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform { ScaleX = 1, ScaleY = 1 },
            };
            itemHost.Children.Add(button);
            var addButton = new Button
            {
                Width = 20,
                Height = 20,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 3, 3, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(ColorHelper.FromArgb(220, 16, 28, 45)),
                BorderThickness = new Thickness(0),
                Tag = item,
                Content = new FontIcon
                {
                    Glyph = "\uE710",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Colors.White),
                },
            };
            AutomationProperties.SetName(addButton,
                item.IsVideo ? $"Add original recording of {item.Title} at playhead" :
                    $"Add {item.Title} as an image layer at playhead");
            ToolTipService.SetToolTip(addButton,
                item.IsVideo ? "Add original recording at playhead" : "Add image layer at playhead");
            addButton.Click += GalleryItem_AddClick;
            itemHost.Children.Add(addButton);
            Canvas.SetLeft(itemHost, 1);
            Canvas.SetTop(itemHost, index * (GalleryThumbSize + GalleryThumbSpacing));
            GalleryItemsHost.Children.Add(itemHost);
            _galleryTargets[itemHost] = 1;
            index++;
        }
        GalleryItemsHost.Height = Math.Max(0,
            index * (GalleryThumbSize + GalleryThumbSpacing) - GalleryThumbSpacing);
        GalleryPrevious.IsEnabled = _galleryPage > 0;
        GalleryPrevious.Opacity = GalleryPrevious.IsEnabled ? 1 : .3;
        GalleryNext.IsEnabled = (_galleryPage + 1) * _galleryPageSize < _galleryMedia.Count;
        GalleryNext.Opacity = GalleryNext.IsEnabled ? 1 : .3;
        GalleryPanel.Visibility = _galleryRevealTarget > 0 ? Visibility.Visible : Visibility.Collapsed;
        GalleryItemsHost.Opacity = animatePage && _galleryMotionSettings.AnimationsEnabled ? .55 : 1;
        if (GalleryPanel.Visibility == Visibility.Visible)
            AnimateGalleryTowardTargets();
    }

    private async Task LoadVideoThumbnailAsync(Guid id, Image target, CancellationToken token)
    {
        if (_store is null) return;
        try
        {
            VideoProject source = await _store.OpenAsync(id, token);
            VideoAsset? recording = source.Assets.FirstOrDefault(asset =>
                asset.Kind == VideoAssetKind.Recording);
            if (recording is null || token.IsCancellationRequested) return;
            string path = _store.ResolveAssetPath(source, recording);
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            using StorageItemThumbnail thumbnail = await file.GetThumbnailAsync(
                ThumbnailMode.VideosView, 160, ThumbnailOptions.UseCurrentScale);
            if (token.IsCancellationRequested || thumbnail.Size == 0) return;
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            if (!token.IsCancellationRequested && !_closed) target.Source = bitmap;
        }
        catch (Exception) { /* A missing shell thumbnail leaves the video badge visible. */ }
    }

    private void PreviewHost_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_galleryAvailable || _galleryDragging) return;
        double x = args.GetCurrentPoint(PreviewHost).Position.X;
        if (x <= GalleryRevealDistance) OpenGallery();
        else if (x > GalleryHideDistance && !GalleryHasKeyboardFocus()) CloseGallery();
    }

    private void PreviewHost_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (!GalleryHasKeyboardFocus() && !_galleryDragging) CloseGallery();
    }

    private void OpenGallery()
    {
        if (!_galleryAvailable) return;
        if (ActualWidth < 640) InspectorPanel.Visibility = Visibility.Collapsed;
        SetGalleryReveal(true);
    }

    private void CloseGallery() => SetGalleryReveal(false);

    private void ToggleGallery()
    {
        if (!_galleryAvailable) return;
        if (_galleryRevealTarget > 0)
        {
            CloseGallery();
            VideoPlayer.Focus(FocusState.Programmatic);
        }
        else
        {
            OpenGallery();
            if (GalleryItemsHost.Children.FirstOrDefault() is Grid first)
                first.Children.OfType<Button>().FirstOrDefault()?.Focus(FocusState.Keyboard);
        }
    }

    private void GalleryPanel_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        foreach (Grid item in _galleryTargets.Keys)
            _galleryTargets[item] = 1;
        AnimateGalleryTowardTargets();
        if (!GalleryHasKeyboardFocus() && !_galleryDragging) CloseGallery();
    }

    private bool GalleryHasKeyboardFocus()
    {
        DependencyObject? focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, GalleryPanel)) return true;
            focused = VisualTreeHelper.GetParent(focused);
        }
        return false;
    }

    private void GalleryPrevious_Click(object sender, RoutedEventArgs args) => ChangeGalleryPage(-1);
    private void GalleryNext_Click(object sender, RoutedEventArgs args) => ChangeGalleryPage(1);

    private void ChangeGalleryPage(int direction)
    {
        int next = Math.Clamp(_galleryPage + direction, 0,
            Math.Max(0, (_galleryMedia.Count - 1) / _galleryPageSize));
        if (next == _galleryPage) return;
        _galleryPage = next;
        RefreshGalleryPage(animatePage: true);
    }

    private void GalleryPanel_PointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        args.Handled = true;
        if (_galleryMedia.Count <= _galleryPageSize) return;
        _galleryWheelDelta += args.GetCurrentPoint(GalleryPanel).Properties.MouseWheelDelta;
        if (Math.Abs(_galleryWheelDelta) < 100) return;
        int direction = _galleryWheelDelta < 0 ? 1 : -1;
        _galleryWheelDelta = 0;
        ChangeGalleryPage(direction);
        GalleryPanel_PointerMoved(sender, args);
    }

    private void GalleryItem_Click(object sender, RoutedEventArgs args)
    {
        if (_galleryDragging || sender is not Button { Tag: GalleryMedia media }) return;
        ActivateGalleryMedia(media);
    }

    private void GalleryItem_Tapped(object sender, TappedRoutedEventArgs args)
    {
        if (_galleryDragging || sender is not Grid { Tag: GalleryMedia media }) return;
        ActivateGalleryMedia(media);
        args.Handled = true;
    }

    private void GalleryItem_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Button button && args.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
        {
            _galleryPressedButton = button;
            _galleryPressedPosition = args.GetCurrentPoint(button).Position;
        }
    }

    private void GalleryItem_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Button { Tag: GalleryMedia media } button ||
            !ReferenceEquals(_galleryPressedButton, button)) return;
        _galleryPressedButton = null;
        Point released = args.GetCurrentPoint(button).Position;
        if (!_galleryDragging && Math.Abs(released.X - _galleryPressedPosition.X) < 12 &&
            Math.Abs(released.Y - _galleryPressedPosition.Y) < 12)
            ActivateGalleryMedia(media);
    }

    private void ActivateGalleryMedia(GalleryMedia media)
    {
        long now = Stopwatch.GetTimestamp();
        if (_galleryLastOpenedId == media.Id && _galleryLastOpenedAt != 0 &&
            Stopwatch.GetElapsedTime(_galleryLastOpenedAt, now).TotalMilliseconds < 600) return;
        if (media.Id == _galleryLastDraggedId && _galleryLastDropAt != 0 &&
            Stopwatch.GetElapsedTime(_galleryLastDropAt).TotalMilliseconds < 500) return;
        _galleryLastOpenedId = media.Id;
        _galleryLastOpenedAt = now;
        if (media.IsVideo) VideoRequested?.Invoke(media.Id);
        else ImageRequested?.Invoke(media.Id);
    }

    private void GalleryItem_AddClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: GalleryMedia media }) return;
        _ = ImportGalleryMediaAsync(media.Id, media.IsVideo, _playhead);
    }

    private void GalleryItem_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not Grid { Tag: GalleryMedia media }) return;
        _galleryDragging = true;
        _galleryPressedButton = null;
        _galleryLastDraggedId = media.Id;
        args.Data.SetData(media.IsVideo ? GalleryVideoFormat : GalleryImageFormat,
            media.Id.ToString("N"));
        args.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void GalleryItem_DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        _galleryLastDropAt = Stopwatch.GetTimestamp();
        _galleryDragging = false;
        if (args.DropResult == DataPackageOperation.Copy) CloseGallery();
        else OpenGallery();
    }

    private void GalleryItem_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Button button) return;
        _galleryHoveredButton = button;
        UpdateGalleryButtonBorder(button);
    }

    private void GalleryItem_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Button button) return;
        if (ReferenceEquals(_galleryHoveredButton, button)) _galleryHoveredButton = null;
        UpdateGalleryButtonBorder(button);
    }

    private void GalleryItem_FocusChanged(object sender, RoutedEventArgs args)
    {
        if (sender is Button button) UpdateGalleryButtonBorder(button);
    }

    private void UpdateGalleryButtonBorder(Button button)
    {
        bool highlighted = ReferenceEquals(_galleryHoveredButton, button) ||
            button.FocusState != FocusState.Unfocused;
        button.BorderThickness = new Thickness(highlighted ? 2 : 0);
    }

    private void GalleryPanel_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_galleryMedia.Count <= 2 || _galleryDragging) return;
        double pointerY = args.GetCurrentPoint(GalleryItemsHost).Position.Y;
        for (int index = 0; index < GalleryItemsHost.Children.Count; index++)
        {
            if (GalleryItemsHost.Children[index] is not Grid item) continue;
            double centerY = index * (GalleryThumbSize + GalleryThumbSpacing) +
                GalleryThumbSize / 2;
            double distance = (pointerY - centerY) / (GalleryThumbSize + GalleryThumbSpacing);
            double scale = 1 + .32 * Math.Exp(-distance * distance / 1.6);
            Canvas.SetZIndex(item, (int)Math.Round(scale * 100));
            _galleryTargets[item] = scale;
        }
        AnimateGalleryTowardTargets();
    }

    private void SetGalleryReveal(bool reveal)
    {
        if (reveal && !_galleryAvailable) return;
        double target = reveal ? 1 : 0;
        if (_galleryRevealTarget == target) return;
        _galleryRevealTarget = target;
        if (reveal) GalleryPanel.Visibility = Visibility.Visible;
        AnimateGalleryTowardTargets();
    }

    private void AnimateGalleryTowardTargets()
    {
        if (!_galleryMotionSettings.AnimationsEnabled)
        {
            StopGalleryAnimation();
            foreach ((Grid item, double target) in _galleryTargets)
            {
                if (item.RenderTransform is not ScaleTransform scale) continue;
                scale.ScaleX = target;
                scale.ScaleY = target;
            }
            GalleryItemsHost.Opacity = 1;
            GalleryPanel.Opacity = _galleryRevealTarget;
            if (GalleryPanel.RenderTransform is TranslateTransform translation)
                translation.X = GalleryHiddenOffset * (1 - _galleryRevealTarget);
            if (_galleryRevealTarget == 0) GalleryPanel.Visibility = Visibility.Collapsed;
            return;
        }
        if (_galleryAnimationRunning) return;
        _galleryAnimationTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += GalleryAnimation_Rendering;
        _galleryAnimationRunning = true;
    }

    private void GalleryAnimation_Rendering(object? sender, object args)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = Math.Clamp(
            Stopwatch.GetElapsedTime(_galleryAnimationTimestamp, now).TotalMilliseconds, 0, 50);
        _galleryAnimationTimestamp = now;
        double step = 1 - Math.Exp(-elapsed / 42);
        bool settled = true;
        foreach ((Grid item, double target) in _galleryTargets)
        {
            if (item.RenderTransform is not ScaleTransform scale) continue;
            double next = scale.ScaleX + (target - scale.ScaleX) * step;
            if (Math.Abs(next - target) < .005) next = target;
            else settled = false;
            scale.ScaleX = next;
            scale.ScaleY = next;
        }

        if (GalleryItemsHost.Opacity < .995)
        {
            GalleryItemsHost.Opacity += (1 - GalleryItemsHost.Opacity) * step;
            settled = false;
        }
        else GalleryItemsHost.Opacity = 1;

        double reveal = GalleryPanel.Opacity +
            (_galleryRevealTarget - GalleryPanel.Opacity) * step;
        if (Math.Abs(reveal - _galleryRevealTarget) < .005)
            reveal = _galleryRevealTarget;
        else settled = false;
        GalleryPanel.Opacity = reveal;
        if (GalleryPanel.RenderTransform is TranslateTransform translation)
            translation.X = GalleryHiddenOffset * (1 - reveal);
        if (!settled) return;
        if (_galleryRevealTarget == 0) GalleryPanel.Visibility = Visibility.Collapsed;
        StopGalleryAnimation();
    }

    private void StopGalleryAnimation()
    {
        if (!_galleryAnimationRunning) return;
        CompositionTarget.Rendering -= GalleryAnimation_Rendering;
        _galleryAnimationRunning = false;
    }

    private void GalleryPanel_Unloaded(object sender, RoutedEventArgs args) =>
        StopGalleryAnimation();

    private void GalleryMedia_DragOver(object sender, DragEventArgs args)
    {
        if (args.DataView.Contains(GalleryVideoFormat) || args.DataView.Contains(GalleryImageFormat))
        {
            args.AcceptedOperation = DataPackageOperation.Copy;
            args.Handled = true;
        }
    }

    private async void GalleryMedia_Drop(object sender, DragEventArgs args)
    {
        if (!args.DataView.Contains(GalleryVideoFormat) &&
            !args.DataView.Contains(GalleryImageFormat)) return;
        args.Handled = true;
        args.AcceptedOperation = DataPackageOperation.Copy;
        var deferral = args.GetDeferral();
        try
        {
            await DropGalleryMediaAsync(args.DataView, _playhead);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task DropGalleryMediaAsync(DataPackageView data, TimeSpan at)
    {
        try
        {
            bool isVideo = data.Contains(GalleryVideoFormat);
            string format = isVideo ? GalleryVideoFormat : GalleryImageFormat;
            if (!data.Contains(format) || await data.GetDataAsync(format) is not string raw ||
                !Guid.TryParse(raw, out Guid id)) return;
            await ImportGalleryMediaAsync(id, isVideo, at);
        }
        catch (Exception exception)
        {
            if (!_closed) StatusText.Text = $"Could not add media: {exception.Message}";
        }
    }

    private Task ImportGalleryMediaAsync(Guid id, bool isVideo, TimeSpan at) => isVideo
        ? ImportGalleryVideoAsync(id, at) : ImportGalleryImageAsync(id, at);

    private void AddImage_Click(object sender, RoutedEventArgs args)
    {
        ToggleGallery();
    }

    private void Paste_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextEntryFocused()) return;
        args.Handled = true;
        _ = PasteImageAsync();
    }

    private async Task PasteImageAsync()
    {
        if (_importing || _closed) return;
        if (!ApplyPendingInspectorChanges()) return;
        _importing = true;
        try
        {
            DataPackageView contents = Clipboard.GetContent();
            IRandomAccessStream? source = null;
            if (contents.Contains("PNG"))
            {
                object data = await contents.GetDataAsync("PNG");
                source = data switch
                {
                    IRandomAccessStreamReference reference => await reference.OpenReadAsync(),
                    IRandomAccessStream stream => stream,
                    IBuffer buffer => await StreamFromBufferAsync(buffer),
                    _ => null,
                };
            }
            if (source is null && contents.Contains(StandardDataFormats.Bitmap))
                source = await (await contents.GetBitmapAsync()).OpenReadAsync();
            if (source is null && contents.Contains(StandardDataFormats.StorageItems))
            {
                IReadOnlyList<IStorageItem> items = await contents.GetStorageItemsAsync();
                StorageFile? image = items.OfType<StorageFile>().FirstOrDefault(file =>
                    file.FileType.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg");
                if (image is not null) source = await image.OpenReadAsync();
            }
            if (source is null)
            {
                StatusText.Text = "Copy an image, then press Ctrl+V";
                return;
            }
            using (source)
            {
                (byte[] png, uint width, uint height) = await NormalizePngAsync(source);
                await ImportImageAsync(png, width, height);
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not paste image: {exception.Message}";
        }
        finally
        {
            _importing = false;
        }
    }

    private async Task ImportGalleryImageAsync(Guid id, TimeSpan? at = null)
    {
        if (_importing || _closed || _readGalleryImage is null) return;
        if (!ApplyPendingInspectorChanges()) return;
        _importing = true;
        try
        {
            byte[] png = await _readGalleryImage(id);
            if ((ulong)png.LongLength > MaximumImageBytes)
                throw new InvalidDataException("Images must be 32 MB or smaller.");
            using var source = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(source))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            source.Seek(0);
            (byte[] normalized, uint width, uint height) = await NormalizePngAsync(source);
            await ImportImageAsync(normalized, width, height, at);
            CloseGallery();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not add image: {exception.Message}";
        }
        finally
        {
            _importing = false;
        }
    }

    private async Task ImportImageAsync(byte[] png, uint imageWidth, uint imageHeight,
        TimeSpan? at = null)
    {
        if (_store is null || _editor is null || _closed) return;
        VideoProjectEditor editor = _editor;
        VideoProject current = editor.Current;
        TimeSpan requested = at ?? _playhead;
        TimeSpan start = requested < TimeSpan.Zero ? TimeSpan.Zero :
            Min(requested, TimeSpan.FromDays(7));
        TimeSpan duration = Min(TimeSpan.FromSeconds(5), TimeSpan.FromDays(7) - start);
        if (duration <= TimeSpan.Zero)
            throw new InvalidOperationException("The timeline cannot exceed seven days.");

        await using var source = new MemoryStream(png, writable: false);
        VideoAsset asset = await _store.ImportAssetAsync(current, VideoAssetKind.Image,
            ".png", source, TimeSpan.Zero);
        if (_closed || !ReferenceEquals(_editor, editor)) return;
        editor.AddAsset(asset);
        double width = Math.Min(current.Width * .42, imageWidth);
        double height = width * imageHeight / imageWidth;
        if (height > current.Height * .64)
        {
            height = current.Height * .64;
            width = height * imageWidth / imageHeight;
        }
        var bounds = new VideoRect((current.Width - width) / 2,
            (current.Height - height) / 2, width, height);
        var image = new ImageOverlay(Guid.NewGuid(), asset.Id, start,
            duration, bounds, current.ImageOverlays.Length);
        editor.AddImageOverlay(image);
        _selectedKind = TimelineKind.Image;
        _selectedId = image.Id;
        RefreshInspector();
        RefreshTimeline();
        FocusSelectedClip();
        await SaveAsync();
        StatusText.Text = "Image added to timeline";
    }

    private async Task ImportGalleryVideoAsync(Guid id, TimeSpan at)
    {
        if (_importing || _closed || _store is null || _editor is null ||
            id == _editor.Current.Id) return;
        if (!ApplyPendingInspectorChanges()) return;
        _importing = true;
        var cancellation = new CancellationTokenSource();
        _galleryImportCancellation = cancellation;
        var copied = new List<VideoAsset>();
        VideoProject? target = null;
        bool committed = false;
        try
        {
            VideoProjectStore store = _store;
            VideoProjectEditor editor = _editor;
            target = editor.Current;
            VideoProject source = await store.OpenAsync(id, cancellation.Token);
            VideoAsset original = source.Assets.Single(asset =>
                asset.Kind == VideoAssetKind.Recording);
            if (target.Duration + original.Duration > TimeSpan.FromDays(7))
                throw new InvalidOperationException("The combined video would exceed seven days.");
            StatusText.Text = $"Adding {source.Title}…";
            VideoAsset video = await CopyAssetAsync(original, source,
                VideoAssetKind.ImportedVideo, source.Title);
            var stems = new List<(VideoAsset Asset, AudioSegment Segment)>();
            foreach (VideoAsset stem in source.Assets.Where(asset =>
                asset.Kind is VideoAssetKind.ComputerAudio or VideoAssetKind.MicrophoneAudio))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                VideoAsset audio = await CopyAssetAsync(stem, source,
                    VideoAssetKind.ImportedAudio,
                    source.Title + (stem.Kind == VideoAssetKind.ComputerAudio
                        ? " · computer audio" : " · microphone"));
                TimeSpan audible = Min(audio.Duration, video.Duration);
                stems.Add((audio, new AudioSegment(Guid.NewGuid(), audio.Id,
                    TimeSpan.Zero, TimeSpan.Zero, audible)));
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closed || !ReferenceEquals(_editor, editor)) return;
            TimeSpan insertAt = at < TimeSpan.Zero ? TimeSpan.Zero : Min(at, editor.Current.Duration);
            Guid clipId = editor.InsertVideoAsset(video, insertAt, stems);
            committed = true;
            _selectedKind = TimelineKind.Video;
            _selectedId = clipId;
            Seek(insertAt);
            RefreshInspector();
            RefreshTimeline();
            FocusSelectedClip();
            CloseGallery();
            await SaveAsync();
            StatusText.Text = $"Added original recording of {source.Title} at {FormatTime(insertAt)}";

            async Task<VideoAsset> CopyAssetAsync(VideoAsset asset, VideoProject owner,
                VideoAssetKind kind, string name)
            {
                string path = store.ResolveAssetPath(owner, asset);
                await using FileStream file = new(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, bufferSize: 65536, FileOptions.Asynchronous);
                VideoAsset imported = await store.ImportAssetAsync(target, kind,
                    Path.GetExtension(path), file, asset.Duration, cancellation.Token);
                imported = imported with { DisplayName = SafeMediaName(name) };
                copied.Add(imported);
                return imported;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_closed) StatusText.Text = $"Could not add video: {exception.Message}";
        }
        finally
        {
            if (!committed && target is not null && _store is not null)
            {
                foreach (VideoAsset asset in copied)
                {
                    try
                    {
                        string path = _store.ResolveAssetPath(target with
                        {
                            Assets = target.Assets.Add(asset),
                        }, asset);
                        File.Delete(path);
                    }
                    catch (Exception) { /* Keep the original import error. */ }
                }
            }
            if (ReferenceEquals(_galleryImportCancellation, cancellation))
                _galleryImportCancellation = null;
            cancellation.Dispose();
            _importing = false;
        }
    }

    private void AddAudio_Click(object sender, RoutedEventArgs args) => _ = ImportAudioAsync();

    private async Task ImportAudioAsync()
    {
        if (_importing || _closed || _editor is null || _store is null) return;
        if (!ApplyPendingInspectorChanges()) return;
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".wav");
        picker.FileTypeFilter.Add(".mp3");
        picker.FileTypeFilter.Add(".flac");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null) return;
        _importing = true;
        try
        {
            TimeSpan sourceDuration = await VideoCompositionService.ProbeAudioDurationAsync(file.Path);
            VideoProject current = _editor.Current;
            TimeSpan start = Min(_playhead, current.Duration);
            if (start + sourceDuration > TimeSpan.FromDays(7))
                throw new InvalidOperationException("Audio would make the timeline longer than seven days.");
            await using FileStream source = new(file.Path, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 65536, FileOptions.Asynchronous);
            VideoAsset asset = await _store.ImportAssetAsync(current,
                VideoAssetKind.ImportedAudio, file.FileType, source, sourceDuration);
            asset = asset with { DisplayName = SafeMediaName(file.Name) };
            if (_closed) return;
            _editor.AddAsset(asset);
            var clip = new AudioSegment(Guid.NewGuid(), asset.Id, start,
                TimeSpan.Zero, sourceDuration);
            _editor.AddAudioSegment(clip);
            _selectedKind = TimelineKind.Audio;
            _selectedId = clip.Id;
            RefreshInspector();
            RefreshTimeline();
            FocusSelectedClip();
            await SaveAsync();
            StatusText.Text = $"Added {file.Name}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not add audio: {exception.Message}";
        }
        finally
        {
            _importing = false;
        }
    }

    private static async Task<IRandomAccessStream> StreamFromBufferAsync(IBuffer buffer)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(buffer);
        stream.Seek(0);
        return stream;
    }

    private static string SafeMediaName(string name)
    {
        string readable = new(name.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        string trimmed = readable[..Math.Min(readable.Length, ShnappTitles.MaximumLength)].Trim();
        return trimmed.Length == 0 ? "Added audio" : trimmed;
    }

    private static async Task<(byte[] Bytes, uint Width, uint Height)> NormalizePngAsync(
        IRandomAccessStream input)
    {
        if (input.Size > MaximumImageBytes)
            throw new InvalidDataException("Images must be 32 MB or smaller.");
        input.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
        uint width = decoder.PixelWidth;
        uint height = decoder.PixelHeight;
        if (width is 0 or > 12000 || height is 0 or > 12000 ||
            (long)width * height > 40_000_000)
            throw new InvalidDataException("Image dimensions are too large.");

        using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        using var output = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
            BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        if (output.Size > MaximumImageBytes)
            throw new InvalidDataException("PNG image is larger than 32 MB.");
        output.Seek(0);
        using var reader = new DataReader(output);
        await reader.LoadAsync(checked((uint)output.Size));
        byte[] bytes = new byte[checked((int)output.Size)];
        reader.ReadBytes(bytes);
        return (bytes, width, height);
    }
}
