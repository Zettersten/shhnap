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
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    private const ulong MaximumImageBytes = 32 * 1024 * 1024;
    private const string GalleryImageFormat = "Shnapp.GalleryImageId";
    private const string GalleryVideoFormat = "Shnapp.GalleryVideoId";
    private const double GalleryThumbSize = 56;
    private const double GalleryThumbSpacing = 6;
    private bool _importing;
    private bool _galleryAvailable;
    private bool _galleryPinned;
    private bool _galleryDragging;
    private Guid _galleryLastDraggedId;
    private long _galleryLastDropAt;
    private int _galleryPage;
    private int _galleryPageSize = 1;
    private double _galleryWheelDelta;
    private IReadOnlyList<GalleryMedia> _galleryMedia = [];
    private CancellationTokenSource? _galleryThumbnailCancellation;
    private CancellationTokenSource? _galleryImportCancellation;

    private sealed record GalleryMedia(Guid Id, string Title, DateTimeOffset Date, bool IsVideo);

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
        GalleryHotspot.Visibility = _galleryAvailable ? Visibility.Visible : Visibility.Collapsed;
        if (!_galleryAvailable) CloseGallery();
        _galleryPage = 0;
        RefreshGalleryPage();
    }

    private void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        int pageSize = Math.Clamp((int)((args.NewSize.Height - 84) /
            (GalleryThumbSize + GalleryThumbSpacing)), 1, 6);
        if (pageSize != _galleryPageSize)
        {
            _galleryPageSize = pageSize;
            _galleryPage = Math.Clamp(_galleryPage, 0,
                Math.Max(0, (_galleryMedia.Count - 1) / pageSize));
            RefreshGalleryPage();
        }
    }

    private void RefreshGalleryPage()
    {
        _galleryThumbnailCancellation?.Cancel();
        _galleryThumbnailCancellation?.Dispose();
        _galleryThumbnailCancellation = new CancellationTokenSource();
        CancellationToken token = _galleryThumbnailCancellation.Token;
        GalleryItemsHost.Children.Clear();
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
                BorderThickness = new Thickness(0),
            };
            string action = item.IsVideo
                ? "Click to open; drag or right-click to add its original recording and audio"
                : "Click or drag to add at the playhead";
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
            button.Click += GalleryItem_Click;
            preview.DragStarting += GalleryItem_DragStarting;
            preview.DropCompleted += GalleryItem_DropCompleted;
            GalleryItemsHost.Children.Add(button);
        }
        GalleryPrevious.IsEnabled = _galleryPage > 0;
        GalleryNext.IsEnabled = (_galleryPage + 1) * _galleryPageSize < _galleryMedia.Count;
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

    private void GalleryHotspot_PointerEntered(object sender, PointerRoutedEventArgs args) =>
        OpenGallery();

    private void GalleryHotspot_Click(object sender, RoutedEventArgs args) => OpenGallery(pin: true);

    private void PreviewHost_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_galleryAvailable || _galleryDragging) return;
        double x = args.GetCurrentPoint(PreviewHost).Position.X;
        if (x <= 28) OpenGallery();
        else if (x > 122 && !_galleryPinned) CloseGallery();
    }

    private void PreviewHost_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (!_galleryPinned && !_galleryDragging) CloseGallery();
    }

    private void OpenGallery(bool pin = false)
    {
        if (!_galleryAvailable) return;
        if (ActualWidth < 640) InspectorPanel.Visibility = Visibility.Collapsed;
        if (pin) _galleryPinned = true;
        GalleryPanel.Visibility = Visibility.Visible;
    }

    private void CloseGallery()
    {
        _galleryPinned = false;
        GalleryPanel.Visibility = Visibility.Collapsed;
    }

    private void ToggleGallery()
    {
        if (!_galleryAvailable) return;
        if (GalleryPanel.Visibility == Visibility.Visible) CloseGallery();
        else
        {
            OpenGallery(pin: true);
            GalleryItemsHost.Children.OfType<Button>().FirstOrDefault()?.Focus(FocusState.Keyboard);
        }
    }

    private void GalleryPanel_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (!_galleryPinned && !_galleryDragging) CloseGallery();
    }

    private void GalleryPrevious_Click(object sender, RoutedEventArgs args) => ChangeGalleryPage(-1);
    private void GalleryNext_Click(object sender, RoutedEventArgs args) => ChangeGalleryPage(1);

    private void ChangeGalleryPage(int direction)
    {
        int next = Math.Clamp(_galleryPage + direction, 0,
            Math.Max(0, (_galleryMedia.Count - 1) / _galleryPageSize));
        if (next == _galleryPage) return;
        _galleryPage = next;
        RefreshGalleryPage();
    }

    private void GalleryPanel_PointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        args.Handled = true;
        _galleryWheelDelta += args.GetCurrentPoint(GalleryPanel).Properties.MouseWheelDelta;
        if (Math.Abs(_galleryWheelDelta) < 100) return;
        int direction = _galleryWheelDelta < 0 ? 1 : -1;
        _galleryWheelDelta = 0;
        ChangeGalleryPage(direction);
    }

    private void GalleryItem_Click(object sender, RoutedEventArgs args)
    {
        if (_galleryDragging || sender is not Button { Tag: GalleryMedia media }) return;
        if (media.Id == _galleryLastDraggedId && _galleryLastDropAt != 0 &&
            Stopwatch.GetElapsedTime(_galleryLastDropAt).TotalMilliseconds < 500) return;
        if (media.IsVideo) VideoRequested?.Invoke(media.Id);
        else _ = ImportGalleryImageAsync(media.Id);
    }

    private void GalleryItem_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not Grid { Tag: GalleryMedia media }) return;
        _galleryDragging = true;
        _galleryLastDraggedId = media.Id;
        args.Data.SetData(media.IsVideo ? GalleryVideoFormat : GalleryImageFormat,
            media.Id.ToString("N"));
        args.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void GalleryItem_DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        _galleryLastDropAt = Stopwatch.GetTimestamp();
        _galleryDragging = false;
        if (args.DropResult == DataPackageOperation.Copy)
            CloseGallery();
        else
            OpenGallery(pin: true);
    }

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
        TimeSpan start = requested >= current.Duration
            ? current.Duration - Min(TimeSpan.FromSeconds(5), current.Duration)
            : requested < TimeSpan.Zero ? TimeSpan.Zero : requested;
        TimeSpan duration = Min(TimeSpan.FromSeconds(5), current.Duration - start);
        if (duration <= TimeSpan.Zero) throw new InvalidOperationException("This video has no room for an image.");

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
            TimeSpan start = _playhead >= current.Duration
                ? current.Duration - Min(sourceDuration, current.Duration)
                : _playhead;
            TimeSpan visible = Min(sourceDuration, current.Duration - start);
            if (visible <= TimeSpan.Zero)
                throw new InvalidOperationException("The audio file has no time inside this video.");
            await using FileStream source = new(file.Path, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 65536, FileOptions.Asynchronous);
            VideoAsset asset = await _store.ImportAssetAsync(current,
                VideoAssetKind.ImportedAudio, file.FileType, source, sourceDuration);
            asset = asset with { DisplayName = SafeMediaName(file.Name) };
            if (_closed) return;
            _editor.AddAsset(asset);
            var clip = new AudioSegment(Guid.NewGuid(), asset.Id, start,
                TimeSpan.Zero, visible);
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
