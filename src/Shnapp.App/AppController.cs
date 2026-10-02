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
    private readonly DispatcherQueueTimer _updateTimer;
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
        _releaseChecker = new(options.DataRoot);
        _page.Configure(this);
        _tray = new(App.WindowHandle, activationMessage);
        _tray.CaptureRequested += Capture;
        _tray.LibraryRequested += OpenLibrary;
        _tray.SettingsRequested += OpenSettings;
        _tray.UpdateNotificationClicked += OpenSettingsUpdates;
        _tray.QuitRequested += Quit;
        _saveTimer = App.DispatcherQueue.CreateTimer();
        _saveTimer.IsRepeating = false;
        _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveTimer.Tick += (_, _) => Run(SaveCurrentAsync);
        _updateTimer = App.DispatcherQueue.CreateTimer();
        _updateTimer.IsRepeating = true;
        _updateTimer.Interval = TimeSpan.FromHours(1);
        _updateTimer.Tick += (_, _) => Run(() => CheckForUpdatesAsync(manual: false));
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
        if (HasPackageIdentity && !_options.Isolated)
        {
            try
            {
                if (!ShnappLibraryMigration.IsComplete(Library.RootPath))
                {
                    string portableRoot = LaunchOptions.UnredirectedPortableDataRoot;
                    if (LaunchOptions.IsPortableInstanceRunning(portableRoot))
                    {
                        _page.ShowMessage("Close portable Shnapp to import",
                            "The portable copy is running. Quit it and restart this Store copy to import your library. " +
                            "Your portable data is unchanged.", InfoBarSeverity.Warning);
                    }
                    else
                    {
                        ShnappLibraryMigration.Result migration = await ShnappLibraryMigration.ImportOnceAsync(
                            portableRoot, Library.RootPath, _lifetime.Token);
                        if (!migration.Complete)
                        {
                            _page.ShowMessage("Portable library import incomplete",
                                "Some portable shnapps or settings could not be copied. Your original library is unchanged. " +
                                "Close the portable copy and restart Shnapp to retry.", InfoBarSeverity.Warning);
                        }
                        else if (migration.ImportedDocuments > 0)
                        {
                            _page.ShowMessage("Portable library imported",
                                $"Copied {migration.ImportedDocuments} shnapp(s) into this Store library. " +
                                "Your portable library is unchanged.", InfoBarSeverity.Success);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or
                UnauthorizedAccessException or ArgumentException)
            {
                _page.ShowMessage("Portable library import unavailable",
                    "Your portable library is unchanged. Close the portable copy and restart Shnapp to retry. " +
                    exception.Message, InfoBarSeverity.Warning);
            }
        }

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

        if (PackageManagerUpdateRunner.TakeResult(_options.DataRoot) is { } managerUpdate)
        {
            _page.ShowMessage(managerUpdate.ExitCode == 0 ? "Package update finished" : "Package update could not finish",
                managerUpdate.ExitCode == 0
                    ? $"{managerUpdate.Channel} finished checking and installing Shnapp. This copy is version {InstalledVersionText}."
                    : $"{managerUpdate.Channel} could not complete the update. Open About & Updates to try again.",
                managerUpdate.ExitCode == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }

        if (!_options.Isolated && !HasPackageIdentity)
        {
            try
            {
                // Scoop, WinGet, and Chocolatey may install each version in a new folder.
                StartupPreference.ReconcilePortable(_settings.StartOnLogin);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
            {
                _page.ShowMessage("Sign-in setting unavailable", exception.Message, InfoBarSeverity.Warning);
            }
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

        Run(async () => { await CheckForUpdatesAsync(manual: false); });
        if (!HasPackageIdentity) { _updateTimer.Start(); }
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
    internal void OpenSettingsUpdates() => Run(() => SettingsAsync(showUpdates: true));
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
            _page.ShowMessage("Shnapp needs another try", exception.Message + " Your open shnapp remains in memory; try Copy or Save.");
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
        _window.ShowMainPage();
        CanvasBitmap? bitmap = null;
        Task? previousSave = null;
        _activeCapture = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken cancellationToken = _activeCapture.Token;
        NativeMethods.GetCursorPos(out NativeMethods.Point pointer);
        DateTimeOffset capturedAt = DateTimeOffset.Now;
        var watch = Stopwatch.StartNew();
        try
        {
            _saveTimer.Stop();
            // Capture first: saving the current document can take longer than a changing
            // desktop frame. A visible editor must leave the screen before its first frame.
            if (wasVisible)
            {
                _window.AppWindow.Hide();
                await Task.Delay(50, cancellationToken);
            }

            string title;
            if (kind == CaptureKind.FullScreen)
            {
                nint monitor = NativeMethods.MonitorFromPoint(pointer, NativeMethods.MonitorDefaultToNearest);
                bitmap = await _capture.MonitorAsync(monitor, cancellationToken);
                MonitorTarget display = NativeMethods.Monitors().FirstOrDefault(target => target.Handle == monitor)
                    ?? throw new InvalidOperationException("The selected display is no longer available.");
                _page.CommitText();
                previousSave = SaveCurrentAsync();
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
                (CanvasBitmap desktop, NativeMethods.Rect bounds) = await _capture.DesktopAsync(cancellationToken);
                using (desktop)
                {
                    // The picker and finished image share one desktop snapshot. In window mode
                    // this intentionally captures the pixels visible at invocation, including
                    // any overlapping window, instead of recapturing a later live window frame.
                    IReadOnlyList<WindowTarget>? windows = kind == CaptureKind.Window
                        ? NativeMethods.Windows(App.WindowHandle) : null;
                    _page.CommitText();
                    previousSave = SaveCurrentAsync();
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
                        bitmap = CropSnapshot(desktop, new ImageRect(
                            (double)window.Bounds.Left - bounds.Left,
                            (double)window.Bounds.Top - bounds.Top,
                            window.Bounds.Width,
                            window.Bounds.Height));
                        title = window.Title;
                    }
                    else if (selection.Region is ImageRect region)
                    {
                        bitmap = CropSnapshot(desktop, region);
                        title = "Free-form shnapp";
                    }
                    else
                    {
                        return;
                    }
                }
            }

            if (previousSave is not null)
            {
                await previousSave;
            }
            var document = new ShnappDocument
            {
                Title = title + $" · {capturedAt:MMM d, HH:mm}",
                CreatedAt = capturedAt,
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
            try
            {
                if (previousSave is not null)
                {
                    await previousSave;
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
    }

    private CanvasBitmap CropSnapshot(CanvasBitmap snapshot, ImageRect region)
    {
        int sourceWidth = checked((int)snapshot.SizeInPixels.Width);
        int sourceHeight = checked((int)snapshot.SizeInPixels.Height);
        int left = Math.Clamp((int)Math.Floor(region.X), 0, sourceWidth);
        int top = Math.Clamp((int)Math.Floor(region.Y), 0, sourceHeight);
        int right = Math.Clamp((int)Math.Ceiling(region.Right), 0, sourceWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(region.Bottom), 0, sourceHeight);
        int width = right - left;
        int height = bottom - top;
        if (width < 2 || height < 2)
        {
            throw new InvalidOperationException("The selected area is outside the captured desktop.");
        }

        return CanvasBitmap.CreateFromBytes(_device, snapshot.GetPixelBytes(left, top, width, height),
            width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
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
                File.Exists(Library.GetFittedPreviewPath(document.Id)) &&
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
            using CanvasRenderTarget fittedPreview = Renderer.Thumbnail(flattened, 384, 256,
                preserveEntireImage: true);
            await ShnappRenderer.SavePngAtomicAsync(fittedPreview, Library.GetFittedPreviewPath(document.Id), CancellationToken.None);
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
        _window.ShowMainPage();
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
        _window.ShowMainPage();
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

    private Task SettingsAsync() => SettingsAsync(showUpdates: false);

    private async Task SettingsAsync(bool showUpdates)
    {
        if (_dialogOpen || _activeCapture is not null)
        {
            return;
        }

        ShnappSettings displayedSettings = _settings;
        if (HasPackageIdentity && !_options.Isolated)
        {
            try
            {
                displayedSettings = _settings with
                {
                    StartOnLogin = await StartupPreference.IsPackagedEnabledAsync(),
                };
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // A development or sideloaded package may not have a registered startup task.
            }
        }

        var page = new SettingsPage();
        page.Configure(this, displayedSettings, _options.Isolated, Library.RootPath,
            InstalledVersionText, HasPackageIdentity, showUpdates);
        if (_lastUpdateResult is not null)
        {
            page.SetUpdateResult(_lastUpdateResult);
        }

        _window.ShowSettings(page);
        Show();
    }

    internal void CloseSettings() => _window.ShowMainPage();

    internal async Task SavePreferencesAsync(ShnappSettings settings)
    {
        bool packaged = HasPackageIdentity;
        bool startupChanged = !_options.Isolated &&
            (packaged || settings.StartOnLogin != _settings.StartOnLogin);
        if (startupChanged) { await StartupPreference.ApplyAsync(settings.StartOnLogin, packaged); }
        try
        {
            await Library.SaveSettingsAsync(settings);
        }
        catch
        {
            if (startupChanged) { await StartupPreference.ApplyAsync(_settings.StartOnLogin, packaged); }
            throw;
        }

        _settings = settings;
        ApplyTheme();
        _page.ViewModel.Status = "Preferences saved";
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
        _updateTimer.Stop();
        _storeUpdateTimer?.Stop();
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
            Library.GetFittedPreviewPath(id),
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
