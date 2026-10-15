using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Controls;
using Shnapp.App.Capture;
using Shnapp.App.Windows;
using Shnapp.Core;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Windows.Storage.Streams;

namespace Shnapp.App;

internal sealed partial class AppController
{
    private DeviceWatcher? _cameraWatcher;
    private bool _cameraAvailable;

    private async Task<bool> RefreshCameraAvailabilityAsync()
    {
        bool available;
        try
        {
            available = (await CameraCaptureService.EnumerateSourcesAsync()).Count > 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Camera enumeration failed: {exception}");
            available = false;
        }

        if (!_quitting)
        {
            _cameraAvailable = available;
            _page.SetCameraAvailable(available);
            _tray.SetCameraAvailable(available);
        }

        return available;
    }

    private void StartCameraWatcher()
    {
        try
        {
            var watcher = DeviceInformation.CreateWatcher(MediaDevice.GetVideoCaptureSelector());
            watcher.Added += CameraWatcher_Changed;
            watcher.Removed += CameraWatcher_Removed;
            watcher.Updated += CameraWatcher_Updated;
            watcher.Start();
            _cameraWatcher = watcher;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Camera watcher could not start: {exception}");
        }
    }

    private void StopCameraWatcher()
    {
        DeviceWatcher? watcher = _cameraWatcher;
        _cameraWatcher = null;
        if (watcher is null) return;
        watcher.Added -= CameraWatcher_Changed;
        watcher.Removed -= CameraWatcher_Removed;
        watcher.Updated -= CameraWatcher_Updated;
        if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
        {
            watcher.Stop();
        }
    }

    private void CameraWatcher_Changed(DeviceWatcher sender, DeviceInformation device) =>
        QueueCameraAvailabilityRefresh();

    private void CameraWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate device) =>
        QueueCameraAvailabilityRefresh();

    private void CameraWatcher_Updated(DeviceWatcher sender, DeviceInformationUpdate device) =>
        QueueCameraAvailabilityRefresh();

    private void QueueCameraAvailabilityRefresh()
    {
        if (!_lifetime.IsCancellationRequested)
        {
            App.DispatcherQueue.TryEnqueue(() => Run(RefreshCameraAvailabilityAsync));
        }
    }

    private async Task<bool> RequireCameraAsync()
    {
        if (await RefreshCameraAvailabilityAsync()) return true;
        Show();
        _page.ShowMessage("No camera available",
            "Connect a camera, then try again. You can choose a preferred camera in Settings.",
            InfoBarSeverity.Warning);
        return false;
    }

    private async Task CaptureCameraAsync()
    {
        if (_dialogOpen || !await _captureGate.WaitAsync(0, _lifetime.Token)) return;

        bool wasVisible = NativeMethods.IsWindowVisible(App.WindowHandle);
        CanvasBitmap? bitmap = null;
        _activeCapture = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken token = _activeCapture.Token;
        try
        {
            _window.ShowMainPage();
            Show();
            if (!await RequireCameraAsync()) return;

            _page.CommitText();
            _saveTimer.Stop();
            await SaveCurrentAsync();
            _dialogOpen = true;
            byte[]? photo;
            try
            {
                photo = await CameraCaptureDialog.CaptureAsync(_page.XamlRoot,
                    _settings.CameraSourceId, token);
            }
            finally
            {
                _dialogOpen = false;
            }

            if (photo is null)
            {
                if (!wasVisible && !_quitting) _window.AppWindow.Hide();
                return;
            }

            using var memory = new MemoryStream(photo, writable: false);
            using IRandomAccessStream stream = memory.AsRandomAccessStream();
            bitmap = await CanvasBitmap.LoadAsync(_device, stream, 96);
            if ((long)bitmap.SizeInPixels.Width * bitmap.SizeInPixels.Height > 64_000_000)
            {
                throw new InvalidDataException("This camera photo exceeds the 64-megapixel capture limit.");
            }

            DateTimeOffset capturedAt = DateTimeOffset.Now;
            var document = new ShnappDocument
            {
                Title = $"Camera photo · {capturedAt:MMM d, HH:mm}",
                CreatedAt = capturedAt,
                CaptureKind = CaptureKind.Camera,
                PixelWidth = checked((int)bitmap.SizeInPixels.Width),
                PixelHeight = checked((int)bitmap.SizeInPixels.Height),
            };
            _saved = null;
            _page.OpenDocument(document, bitmap);
            Renderer.ClearPastedImages();
            bitmap = null;
            await SaveCurrentAsync();
            RecordNavigation(document.Id);
            _page.SetGalleryDocuments(await ListReadySummariesAsync());
            if (_settings.AutoCopy) await CopyAsync();
            _page.ViewModel.Status = "Camera photo captured · annotate or add another photo";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ShowCameraError(exception);
        }
        finally
        {
            bitmap?.Dispose();
            _activeCapture?.Dispose();
            _activeCapture = null;
            _captureGate.Release();
        }
    }

    private async Task InsertCameraPhotoAsync()
    {
        if (_page.Document is null || _dialogOpen ||
            !await _captureGate.WaitAsync(0, _lifetime.Token)) return;

        Guid targetDocument = _page.Document.Id;
        _activeCapture = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken token = _activeCapture.Token;
        try
        {
            _window.ShowMainPage();
            Show();
            if (!await RequireCameraAsync()) return;
            _page.CommitText();
            _dialogOpen = true;
            byte[]? photo;
            try
            {
                photo = await CameraCaptureDialog.CaptureAsync(_page.XamlRoot,
                    _settings.CameraSourceId, token);
            }
            finally
            {
                _dialogOpen = false;
            }

            if (photo is null || _page.Document?.Id != targetDocument) return;
            await _page.InsertCameraPhotoAsync(photo);
            await SaveCurrentAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ShowCameraError(exception);
        }
        finally
        {
            _activeCapture?.Dispose();
            _activeCapture = null;
            _captureGate.Release();
        }
    }

    private void ShowCameraError(Exception exception)
    {
        Show();
        System.Diagnostics.Debug.WriteLine($"Camera capture failed: {exception}");
        string message = exception is UnauthorizedAccessException ||
            exception.HResult == unchecked((int)0x80070005)
            ? "Allow Shnapp to access the camera in Windows Privacy & security settings, then try again."
            : exception is InvalidDataException or ArgumentException
                ? exception.Message
                : "The camera could not take a photo. Check its connection and close other apps using it, then try again.";
        _page.ShowMessage("Camera photo unavailable", message, InfoBarSeverity.Warning);
        _ = RefreshCameraAvailabilityAsync();
    }
}
