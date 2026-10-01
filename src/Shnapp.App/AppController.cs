using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shnapp.App.Capture;
using Shnapp.App.Editor;
using Shnapp.App.Windows;
using Shnapp.Core;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Shnapp.App;

internal sealed partial class AppController
{
    private readonly MainWindow _window;
    private readonly MainPage _page;
    private readonly LaunchOptions _options;
    private readonly CanvasDevice _device;
    private readonly GraphicsCapture _capture;
    private readonly TrayHost _tray;
    private readonly Action _releaseInstance;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly DispatcherQueueTimer _saveTimer;
    private ShnappSettings _settings = new();
    private ShnappDocument? _saved;
    private CancellationTokenSource? _activeCapture;
    private bool _quitting;
    private bool _dialogOpen;

    internal ShnappLibrary Library { get; }
    internal ShnappRenderer Renderer { get; }

    internal AppController(MainWindow window, LaunchOptions options, uint activationMessage, Action releaseInstance)
    {
        _window = window;
        _page = window.Page;
        _options = options;
        _releaseInstance = releaseInstance;
        _device = CanvasDevice.GetSharedDevice();
        Renderer = new(_device);
        _capture = new(_device);
        Library = new(options.DataRoot);
        _page.Configure(this);
        _tray = new(App.WindowHandle, activationMessage);
        _tray.CaptureRequested += Capture;
        _tray.LibraryRequested += OpenLibrary;
        _tray.SettingsRequested += OpenSettings;
        _tray.QuitRequested += Quit;
        _saveTimer = App.DispatcherQueue.CreateTimer();
        _saveTimer.IsRepeating = false;
        _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveTimer.Tick += (_, _) => Run(SaveCurrentAsync);
        _page.DocumentChanged += (_, _) =>
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        _window.AppWindow.Closing += (_, args) =>
        {
            if (!_quitting)
            {
                args.Cancel = true;
                if (_tray.IsPresent) { Hide(); }
                else { Quit(); }
            }
        };
    }

    internal async Task InitializeAsync()
    {
        try
        {
            _settings = await Library.LoadSettingsAsync(_lifetime.Token);
            ApplyTheme();
            IReadOnlyList<ShnappSummary> documents = await Library.ListSummariesAsync(_lifetime.Token);
            _page.ShowLibrary(documents);
            UpdateNavigationButtons();
            _page.SetGalleryDocuments(documents);
            _page.ViewModel.Status = "Ready to shnapp · close returns to the tray";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _page.ShowMessage("Local library unavailable", exception.Message + " Your capture can still be copied or exported.");
        }

        if (_tray.Conflicts.Count > 0)
        {
            _page.ShowMessage("Shortcut already in use",
                string.Join(", ", _tray.Conflicts) + " could not be registered. Close the conflicting app, restart Shnapp, or use New shnapp.",
                InfoBarSeverity.Warning);
        }
        else if (!_tray.IsPresent)
        {
            _page.ShowMessage("Tray icon unavailable", "Closing will quit Shnapp until the Windows notification area is available.",
                InfoBarSeverity.Warning);
        }
    }

    internal void Capture(CaptureKind kind) => Run(() => CaptureAsync(kind));
    internal void OpenLibrary() => Run(() => OpenLibraryAsync());
    internal void OpenDocument(Guid id) => Run(() => OpenDocumentAsync(id));
    internal void Copy() => Run(CopyAsync);
    internal void Export() => Run(ExportAsync);
    internal void SaveCurrent()
    {
        _saveTimer.Stop();
        Run(SaveCurrentAsync);
    }
    internal void Hide() => Run(HideAsync);
    internal void OpenSettings() => Run(SettingsAsync);
    internal void Quit() => Run(QuitAsync);

    /// <summary>Opens a saved shnapp's rendered PNG for adding it as an image layer.</summary>
    internal async Task<StorageFile> OpenSavedImageForLayerAsync(Guid id)
    {
        if (_page.Document?.Id == id)
        {
            _page.CommitText();
            _saveTimer.Stop();
            await SaveCurrentAsync();
        }

        if (await Library.OpenAsync(id, _lifetime.Token) is null)
        {
            throw new FileNotFoundException("This shnapp is no longer in your Library.");
        }

        EnsureLibraryImagesSafe(id, create: false);
        string path = Library.GetExportPath(id);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("This shnapp has no saved image to add.", path);
        }

        if (file.Length > 32L * 1024 * 1024)
        {
            throw new InvalidDataException("This shnapp is too large to add as an image layer (32 MB limit).");
        }

        return await StorageFile.GetFileFromPathAsync(path);
    }

    private async void Run(Func<Task> action)
    {
        try
        {
            if (!_quitting)
            {
                await action();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Show();
            _page.ShowMessage("Shnapp needs another try", exception.Message + " Your open shnapp remains in memory; try Copy or Save PNG.");
        }
    }

    private void Show()
    {
        _window.AppWindow.Show();
        _window.Activate();
        NativeMethods.SetForegroundWindow(App.WindowHandle);
    }

    private async Task CaptureAsync(CaptureKind kind)
    {
        if (_dialogOpen || !await _captureGate.WaitAsync(0, _lifetime.Token))
        {
            return;
        }

        bool wasVisible = NativeMethods.IsWindowVisible(App.WindowHandle);
        CanvasBitmap? bitmap = null;
        _activeCapture = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken cancellationToken = _activeCapture.Token;
        NativeMethods.GetCursorPos(out NativeMethods.Point pointer);
        var watch = Stopwatch.StartNew();
        try
        {
            _page.CommitText();
            _saveTimer.Stop();
            await SaveCurrentAsync();
            _window.AppWindow.Hide();
            await Task.Delay(100, cancellationToken);
            string title;
            if (kind == CaptureKind.FullScreen)
            {
                nint monitor = NativeMethods.MonitorFromPoint(pointer, NativeMethods.MonitorDefaultToNearest);
                bitmap = await _capture.MonitorAsync(monitor, cancellationToken);
                MonitorTarget display = NativeMethods.Monitors().FirstOrDefault(target => target.Handle == monitor)
                    ?? throw new InvalidOperationException("The selected display is no longer available.");
                using (var preview = new SelectionWindow(bitmap, display.Bounds, null, previewDisplay: true))
                {
                    if (await preview.PickAsync(cancellationToken) is null)
                    {
                        if (wasVisible && !_quitting) { Show(); }
                        return;
                    }
                }

                title = "Full screen";
            }
            else
            {
                IReadOnlyList<WindowTarget>? windows = kind == CaptureKind.Window ? NativeMethods.Windows(App.WindowHandle) : null;
                (CanvasBitmap desktop, NativeMethods.Rect bounds) = await _capture.DesktopAsync(cancellationToken);
                using (desktop)
                {
                    CaptureSelection? selection;
                    using (var picker = new SelectionWindow(desktop, bounds, windows))
                    {
                        selection = await picker.PickAsync(cancellationToken);
                    }

                    if (selection is null)
                    {
                        if (wasVisible && !_quitting) { Show(); }
                        return;
                    }

                    if (selection.Window is WindowTarget window)
                    {
                        await Task.Delay(60, cancellationToken);
                        bitmap = await _capture.WindowAsync(window.Handle, cancellationToken);
                        title = window.Title;
                    }
                    else if (selection.Region is ImageRect region)
                    {
                        int left = (int)Math.Floor(region.X);
                        int top = (int)Math.Floor(region.Y);
                        int width = (int)Math.Ceiling(region.Right) - left;
                        int height = (int)Math.Ceiling(region.Bottom) - top;
                        bitmap = CanvasBitmap.CreateFromBytes(_device, desktop.GetPixelBytes(left, top, width, height),
                            width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
                        title = "Free-form shnapp";
                    }
                    else
                    {
                        return;
                    }
                }
            }

            var document = new ShnappDocument
            {
                Title = title + $" · {DateTime.Now:MMM d, HH:mm}",
                CaptureKind = kind,
                PixelWidth = checked((int)bitmap.SizeInPixels.Width),
                PixelHeight = checked((int)bitmap.SizeInPixels.Height),
                HasWindowShadow = kind == CaptureKind.Window && _settings.WindowShadow,
            };
            _saved = null;
            _page.OpenDocument(document, bitmap);
            Renderer.ClearPastedImages();
            bitmap = null;
            Show();
            watch.Stop();
            Debug.WriteLine($"Shnapp {kind}: {watch.ElapsedMilliseconds} ms to editor.");
            await SaveCurrentAsync();
            RecordNavigation(document.Id);
            _page.SetGalleryDocuments(await Library.ListSummariesAsync(_lifetime.Token));
            if (_settings.AutoCopy)
            {
                await CopyAsync();
            }
        }
        finally
        {
            bitmap?.Dispose();
            _activeCapture.Dispose();
            _activeCapture = null;
            _captureGate.Release();
        }
    }

    private async Task SaveCurrentAsync()
    {
        _page.CommitTitleRename();
        await _saveGate.WaitAsync();
        try
        {
            ShnappDocument? document = _page.Document;
            CanvasBitmap? original = _page.Original;
            if (document is null || original is null || (ReferenceEquals(document, _saved) &&
                File.Exists(Library.GetExportPath(document.Id)) && File.Exists(Library.GetPreviewPath(document.Id)) &&
                File.Exists(Library.GetCompactPreviewPath(document.Id)) &&
                File.Exists(Library.GetGalleryPreviewPath(document.Id))))
            {
                return;
            }

            using CanvasRenderTarget flattened = Renderer.Flatten(original, document);
            await using ShnappLibrary.PreparedDocumentSave prepared =
                await Library.PrepareSaveAsync(document);
            EnsureLibraryImagesSafe(document.Id, create: true);
            if (!File.Exists(Library.GetOriginalPath(document.Id)))
            {
                await ShnappRenderer.SavePngAtomicAsync(original, Library.GetOriginalPath(document.Id), CancellationToken.None);
            }

            using CanvasRenderTarget gallery = Renderer.Thumbnail(flattened, 160, 160);
            await ShnappRenderer.SavePngAtomicAsync(gallery, Library.GetGalleryPreviewPath(document.Id), CancellationToken.None);
            using CanvasRenderTarget compact = Renderer.Thumbnail(flattened, 192, 128);
            await ShnappRenderer.SavePngAtomicAsync(compact, Library.GetCompactPreviewPath(document.Id), CancellationToken.None);
            using CanvasRenderTarget preview = Renderer.Thumbnail(flattened, 384, 256);
            await ShnappRenderer.SavePngAtomicAsync(preview, Library.GetPreviewPath(document.Id), CancellationToken.None);
            await ShnappRenderer.SavePngAtomicAsync(flattened, Library.GetExportPath(document.Id), CancellationToken.None);
            await prepared.CommitAsync();
            _saved = document;
            if (ReferenceEquals(document, _page.Document))
            {
                _page.UpdateDocumentMetadata();
                _page.ViewModel.Status = "Saved locally · Ctrl+C to copy · Ctrl+S to export";
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task CopyAsync()
    {
        _page.CommitText();
        ShnappDocument? document = _page.Document;
        CanvasBitmap? original = _page.Original;
        if (document is null || original is null)
        {
            return;
        }

        using CanvasRenderTarget flattened = Renderer.Flatten(original, document);
        using var stream = new InMemoryRandomAccessStream();
        await flattened.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        stream.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        int width = checked((int)decoder.PixelWidth);
        int height = checked((int)decoder.PixelHeight);
        PixelDataProvider pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        stream.Seek(0);
        int pngLength = checked((int)stream.Size);
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)pngLength);
        byte[] png = new byte[pngLength];
        reader.ReadBytes(png);
        TransparentClipboard.SetImage(App.WindowHandle, png, pixels.DetachPixelData(), width, height);
        _page.ViewModel.Status = "Copied · ready to paste";
    }

    private async Task ExportAsync()
    {
        _page.CommitText();
        ShnappDocument? document = _page.Document;
        CanvasBitmap? original = _page.Original;
        if (document is null || original is null || _dialogOpen)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = SafeFileName(document.Title),
            };
            picker.FileTypeChoices.Add("PNG shnapp", [".png"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is not null)
            {
                using CanvasRenderTarget flattened = Renderer.Flatten(original, document);
                await ShnappRenderer.SavePngAtomicAsync(flattened, file.Path, CancellationToken.None);
                _page.ViewModel.Status = "PNG exported · editable original stays in your Library";
            }
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task OpenLibraryAsync(bool recordHistory = true)
    {
        _activeCapture?.Cancel();
        await _captureGate.WaitAsync(_lifetime.Token);
        try
        {
            _page.CommitText();
            _saveTimer.Stop();
            await SaveCurrentAsync();
            IReadOnlyList<ShnappSummary> documents = await Library.ListSummariesAsync(_lifetime.Token);
            _page.ShowLibrary(documents);
            if (recordHistory) { RecordNavigation(null); }
            _page.SetGalleryDocuments(documents);
            Renderer.ClearPastedImages();
            _page.ViewModel.Status = "Saved on this PC · New shnapp or Ctrl+Shift+4 to capture";
            Show();
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private async Task OpenDocumentAsync(Guid id, bool recordHistory = true)
    {
        if (!await _captureGate.WaitAsync(0, _lifetime.Token))
        {
            return;
        }

        try
        {
            _page.CommitText();
            _saveTimer.Stop();
            await SaveCurrentAsync();
            ShnappDocument document = await Library.OpenAsync(id, _lifetime.Token)
                ?? throw new FileNotFoundException("This shnapp is no longer in your Library.");
            if ((long)document.PixelWidth * document.PixelHeight > 64_000_000)
            {
                throw new InvalidDataException("This shnapp exceeds the 64-megapixel image limit.");
            }
            EnsureLibraryImagesSafe(id, create: false);
            string path = Library.GetOriginalPath(id);
            using FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            CanvasBitmap bitmap = await CanvasBitmap.LoadAsync(_device, source.AsRandomAccessStream(), 96);
            if (bitmap.SizeInPixels.Width != document.PixelWidth || bitmap.SizeInPixels.Height != document.PixelHeight)
            {
                bitmap.Dispose();
                throw new InvalidDataException("This shnapp's original PNG does not match its editable document.");
            }

            try
            {
                await Renderer.PreloadImagesAsync(document);
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
            _saved = document;
            _page.OpenDocument(document, bitmap);
            if (recordHistory) { RecordNavigation(id); }
            _page.SetGalleryDocuments(await Library.ListSummariesAsync(_lifetime.Token));
            Renderer.RetainPastedImages(document);
            Show();
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private async Task HideAsync()
    {
        if (_dialogOpen || !_tray.IsPresent)
        {
            return;
        }

        _page.CommitText();
        _saveTimer.Stop();
        await SaveCurrentAsync();
        _page.ShowLibrary([]);
        RecordNavigation(null);
        Renderer.ClearPastedImages();
        _window.AppWindow.Hide();
    }

    private async Task SettingsAsync()
    {
        if (_dialogOpen || _activeCapture is not null)
        {
            return;
        }

        _dialogOpen = true;
        Show();
        try
        {
            var startup = new ToggleSwitch
            {
                IsOn = _settings.StartOnLogin,
                IsEnabled = !_options.Isolated,
            };
            var autoCopy = new ToggleSwitch { IsOn = _settings.AutoCopy };
            var shadow = new ToggleSwitch { IsOn = _settings.WindowShadow };
            var theme = new ComboBox { Header = "Theme", ItemsSource = new[] { "System", "Light", "Dark" }, SelectedItem = _settings.Theme };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(startup, "Start Shnapp at sign-in");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(autoCopy, "Copy new shnapps automatically");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(shadow, "Soft shadow on window shnapps");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(startup, "StartOnLogin");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(autoCopy, "AutoCopy");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(shadow, "WindowShadow");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(theme, "ShnappTheme");
            var content = new StackPanel { Spacing = 12, MinWidth = 320, MaxWidth = 420 };
            var captureGroup = new StackPanel { Spacing = 8 };
            captureGroup.Children.Add(PreferenceRow("Start Shnapp at sign-in", startup));
            captureGroup.Children.Add(PreferenceRow("Copy new shnapps automatically", autoCopy));
            captureGroup.Children.Add(PreferenceRow("Soft shadow on window shnapps", shadow));
            content.Children.Add(new TextBlock { Text = "CAPTURE", FontSize = 12, Opacity = 0.68 });
            content.Children.Add(captureGroup);
            content.Children.Add(new TextBlock { Text = "APPEARANCE", FontSize = 12, Opacity = 0.68 });
            content.Children.Add(theme);
            content.Children.Add(new TextBlock { Text = "SHORTCUTS", FontSize = 12, Opacity = 0.68 });
            content.Children.Add(new TextBlock
            {
                Text = "Window          Ctrl+Shift+4\nFull screen     Ctrl+Shift+3\nRegion          Ctrl+Shift+2",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock { Text = "LOCAL LIBRARY", FontSize = 12, Opacity = 0.68 });
            content.Children.Add(new TextBlock
            {
                Text = Library.RootPath,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                MaxWidth = 380,
            });
            content.Children.Add(new TextBlock
            {
                Text = _options.Isolated
                    ? "Verification library. Startup changes are disabled."
                    : "Editable originals stay here. Share the exported PNG when a capture contains private details.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.72,
                MaxWidth = 380,
            });
            var dialog = new ContentDialog
            {
                XamlRoot = _page.XamlRoot,
                RequestedTheme = _page.RequestedTheme,
                Title = "Settings",
                Content = new ScrollViewer
                {
                    Content = content,
                    MaxHeight = 460,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var settings = new ShnappSettings
                {
                    StartOnLogin = startup.IsOn,
                    AutoCopy = autoCopy.IsOn,
                    WindowShadow = shadow.IsOn,
                    Theme = (string)theme.SelectedItem,
                };
                bool startupChanged = !_options.Isolated && settings.StartOnLogin != _settings.StartOnLogin;
                if (startupChanged) { StartupPreference.Apply(settings.StartOnLogin); }
                try
                {
                    await Library.SaveSettingsAsync(settings);
                }
                catch
                {
                    if (startupChanged) { StartupPreference.Apply(_settings.StartOnLogin); }
                    throw;
                }

                _settings = settings;
                ApplyTheme();
                _page.ViewModel.Status = "Preferences saved";
            }
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private static Grid PreferenceRow(string label, ToggleSwitch toggle)
    {
        var row = new Grid { ColumnSpacing = 16, MinHeight = 42 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Opacity = toggle.IsEnabled ? 1 : 0.55,
        });
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }

    private void ApplyTheme() => _window.ApplyTheme(_settings.Theme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    });

    private async Task QuitAsync()
    {
        _activeCapture?.Cancel();
        _saveTimer.Stop();
        await _captureGate.WaitAsync();
        try
        {
            _page.CommitText();
            await SaveCurrentAsync();
            _quitting = true;
            await _lifetime.CancelAsync();
            _tray.Dispose();
            _page.DisposeCanvas();
            _device.Dispose();
            _releaseInstance();
            _window.Close();
            Application.Current.Exit();
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private static string SafeFileName(string title)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string name = new(title.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        name = name.Trim().TrimEnd('.', ' ');
        return string.IsNullOrEmpty(name) ? "shnapp" : name[..Math.Min(80, name.Length)];
    }

    private void EnsureLibraryImagesSafe(Guid id, bool create)
    {
        string directory = Library.GetDocumentDirectory(id);
        string[] paths =
        [
            Library.RootPath,
            Path.Combine(Library.RootPath, "shnapps"),
            directory,
            Library.GetOriginalPath(id),
            Library.GetPreviewPath(id),
            Library.GetCompactPreviewPath(id),
            Library.GetGalleryPreviewPath(id),
            Library.GetExportPath(id),
        ];
        CheckPaths();
        if (create)
        {
            Directory.CreateDirectory(directory);
            CheckPaths();
        }

        void CheckPaths()
        {
            foreach (string path in paths)
            {
                try
                {
                    if ((File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException("Library images and directories must not be linked files or reparse points.");
                    }
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
    }
}
