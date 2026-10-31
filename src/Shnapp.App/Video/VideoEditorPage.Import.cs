using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Shnapp.App.Editor;
using Shnapp.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    private const ulong MaximumImageBytes = 32 * 1024 * 1024;
    private bool _importing;

    private void BuildGallery()
    {
        GalleryItemsHost.Children.Clear();
        foreach (ShnappSummary item in _galleryItems.Take(16))
        {
            Guid id = item.Id;
            string previewPath = _getGalleryPreviewPath?.Invoke(id) ?? string.Empty;
            var entry = new LibraryEntry(item, previewPath, previewPixelWidth: 128);
            var thumbnail = new Image
            {
                Source = entry.Preview,
                Stretch = Stretch.UniformToFill,
                Width = 48,
                Height = 48,
            };
            var preview = new Border
            {
                Width = 48,
                Height = 48,
                CornerRadius = new CornerRadius(6),
                Background = (Brush)Application.Current.Resources["ShnappPreviewBrush"],
                Child = thumbnail,
            };
            var details = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
            };
            details.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            details.Children.Add(new TextBlock
            {
                Text = item.ModifiedAt.ToLocalTime().ToString("MMM d, yyyy"),
                FontSize = 10,
                Foreground = (Brush)Application.Current.Resources["ShnappToolbarInactiveIconBrush"],
            });
            var row = new Grid
            {
                ColumnSpacing = 8,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(details, 1);
            row.Children.Add(preview);
            row.Children.Add(details);
            var button = new Button
            {
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(4),
                Height = 56,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            AutomationProperties.SetName(button, $"Add {item.Title} to the video");
            ToolTipService.SetToolTip(button, $"Add {item.Title} at the playhead");
            button.Click += (_, _) => _ = ImportGalleryImageAsync(id);
            GalleryItemsHost.Children.Add(button);
        }

        if (GalleryItemsHost.Children.Count == 0)
        {
            GalleryItemsHost.Children.Add(new TextBlock
            {
                Text = "Saved screenshots appear here.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private void GalleryHotspot_PointerEntered(object sender, PointerRoutedEventArgs args) =>
        OpenGallery();

    private void OpenGallery()
    {
        if (ActualWidth < 640) InspectorPanel.Visibility = Visibility.Collapsed;
        GalleryPanel.Visibility = Visibility.Visible;
    }

    private void GalleryPanel_PointerExited(object sender, PointerRoutedEventArgs args) =>
        GalleryPanel.Visibility = Visibility.Collapsed;

    private void CloseGallery_Click(object sender, RoutedEventArgs args) =>
        GalleryPanel.Visibility = Visibility.Collapsed;

    private void AddImage_Click(object sender, RoutedEventArgs args)
    {
        if (GalleryPanel.Visibility == Visibility.Visible)
            GalleryPanel.Visibility = Visibility.Collapsed;
        else OpenGallery();
        StatusText.Text = "Choose a recent image or press Ctrl+V to paste";
    }

    private void Paste_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or NumberBox)
            return;
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

    private async Task ImportGalleryImageAsync(Guid id)
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
            await ImportImageAsync(normalized, width, height);
            GalleryPanel.Visibility = Visibility.Collapsed;
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

    private async Task ImportImageAsync(byte[] png, uint imageWidth, uint imageHeight)
    {
        if (_store is null || _editor is null || _closed) return;
        VideoProjectEditor editor = _editor;
        VideoProject current = editor.Current;
        TimeSpan start = _playhead >= current.Duration
            ? current.Duration - Min(TimeSpan.FromSeconds(5), current.Duration)
            : _playhead;
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
        await SaveAsync();
        StatusText.Text = "Image added to timeline";
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
