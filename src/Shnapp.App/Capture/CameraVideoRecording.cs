using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace Shnapp.App.Capture;

/// <summary>Owns a video-only camera recording beside a screen recording.</summary>
internal sealed class CameraVideoRecording : IAsyncDisposable
{
    private readonly MediaCapture _capture;
    private readonly LowLagMediaRecording _recording;
    private readonly DispatcherQueue _captureQueue;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private bool _started;
    private bool _finished;
    private bool _disposed;
    private bool _eventsAttached = true;

    private CameraVideoRecording(MediaCapture capture, LowLagMediaRecording recording,
        string path)
    {
        _capture = capture;
        _recording = recording;
        _captureQueue = DispatcherQueue.GetForCurrentThread() ??
            throw new InvalidOperationException("Camera capture needs a UI dispatcher.");
        Path = path;
        _capture.Failed += Capture_Failed;
        _capture.RecordLimitationExceeded += Capture_RecordLimitationExceeded;
    }

    internal string Path { get; }

    internal event Action<Exception>? Failed;

    internal static async Task<CameraVideoRecording> PrepareAsync(string path,
        string? preferredSourceId, CancellationToken token)
    {
        IReadOnlyList<CameraSource> sources =
            await CameraCaptureService.EnumerateSourcesAsync();
        CameraSource? source = string.IsNullOrWhiteSpace(preferredSourceId)
            ? sources.FirstOrDefault()
            : sources.FirstOrDefault(item => item.Id == preferredSourceId);
        if (source is null)
        {
            throw new InvalidOperationException(preferredSourceId is null
                ? "No camera is available. Turn off camera video or connect a camera."
                : "The selected camera is unavailable. Choose another camera in Settings.");
        }

        token.ThrowIfCancellationRequested();
        var capture = new MediaCapture();
        LowLagMediaRecording? recording = null;
        bool fileCreated = false;
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = source.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                MemoryPreference = MediaCaptureMemoryPreference.Auto,
            }).AsTask(token);
            await SelectEfficientRecordFormatAsync(capture, token);

            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(
                System.IO.Path.GetDirectoryName(path)!).AsTask(token);
            StorageFile file = await folder.CreateFileAsync(
                System.IO.Path.GetFileName(path), CreationCollisionOption.FailIfExists)
                .AsTask(token);
            fileCreated = true;
            MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(
                VideoEncodingQuality.Auto);
            profile.Audio = null;
            recording = await capture.PrepareLowLagRecordToStorageFileAsync(
                profile, file).AsTask(token);
            return new CameraVideoRecording(capture, recording, path);
        }
        catch
        {
            if (recording is not null)
            {
                try { await recording.FinishAsync(); }
                catch (Exception) { }
            }
            capture.Dispose();
            if (fileCreated)
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    private static async Task SelectEfficientRecordFormatAsync(MediaCapture capture,
        CancellationToken token)
    {
        // Use a format the camera itself advertises. Auto encoding then follows
        // this input instead of choosing an expensive 4K camera default.
        try
        {
            VideoEncodingProperties[] formats = capture.VideoDeviceController
                .GetAvailableMediaStreamProperties(MediaStreamType.VideoRecord)
                .OfType<VideoEncodingProperties>()
                .Where(format => format.Width > 0 && format.Height > 0 &&
                    FramesPerSecond(format) > 0)
                .ToArray();
            VideoEncodingProperties? selected = formats
                .Where(format => format.Width is >= 640 and <= 1280 &&
                    format.Height is >= 360 and <= 720 &&
                    FramesPerSecond(format) <= 30.5)
                .OrderByDescending(format => (ulong)format.Width * format.Height)
                .ThenByDescending(FramesPerSecond)
                .FirstOrDefault()
                ?? formats.Where(format => format.Width is >= 640 and <= 1280 &&
                        format.Height is >= 360 and <= 720)
                    .OrderByDescending(format => (ulong)format.Width * format.Height)
                    .ThenBy(FramesPerSecond)
                    .FirstOrDefault()
                ?? formats.Where(format => format.Width <= 1280 && format.Height <= 720)
                    .OrderByDescending(format => (ulong)format.Width * format.Height)
                    .ThenBy(FramesPerSecond)
                    .FirstOrDefault()
                ?? formats.OrderBy(format => (ulong)format.Width * format.Height)
                    .ThenBy(FramesPerSecond)
                    .FirstOrDefault();
            if (selected is not null)
                await capture.VideoDeviceController.SetMediaStreamPropertiesAsync(
                    MediaStreamType.VideoRecord, selected).AsTask(token);
        }
        catch (Exception error) when (error is COMException or ArgumentException or
            InvalidOperationException)
        {
            // Some drivers do not expose a settable record pin. Their default
            // format remains available through MediaEncodingProfile.Auto.
        }
    }

    private static double FramesPerSecond(VideoEncodingProperties format) =>
        format.FrameRate.Denominator == 0 ? double.PositiveInfinity :
        format.FrameRate.Numerator / (double)format.FrameRate.Denominator;

    internal async Task StartAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (_disposed || _finished) throw new ObjectDisposedException(nameof(CameraVideoRecording));
            if (_started) return;
            await OnCaptureThreadAsync(() => _recording.StartAsync().AsTask());
            _started = true;
        }
        finally { _operations.Release(); }
    }

    internal async Task PauseAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (_started && !_finished)
                await OnCaptureThreadAsync(() => _recording.PauseAsync(
                    MediaCapturePauseBehavior.RetainHardwareResources).AsTask());
        }
        finally { _operations.Release(); }
    }

    internal async Task ResumeAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (_started && !_finished)
                await OnCaptureThreadAsync(() => _recording.ResumeAsync().AsTask());
        }
        finally { _operations.Release(); }
    }

    internal async Task StopAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (_finished) return;
            if (_started)
                await OnCaptureThreadAsync(() => _recording.StopAsync().AsTask());
            // FinishAsync releases native recording resources. Drop event
            // subscriptions first while their MediaCapture source is still live.
            await OnCaptureThreadAsync(() =>
            {
                DetachCaptureEvents();
                return Task.CompletedTask;
            });
            await OnCaptureThreadAsync(() => _recording.FinishAsync().AsTask());
            _finished = true;
        }
        finally { _operations.Release(); }
    }

    private void Capture_Failed(MediaCapture sender, MediaCaptureFailedEventArgs args) =>
        Failed?.Invoke(new IOException("Camera video capture stopped: " + args.Message));

    private void Capture_RecordLimitationExceeded(MediaCapture sender) =>
        Failed?.Invoke(new IOException("The camera reached its recording limit."));

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await StopAsync(); }
        finally
        {
            // MediaCapture is not agile. The native event source and COM object
            // must be released on the dispatcher where capture was initialized.
            await OnCaptureThreadAsync(() =>
            {
                DetachCaptureEvents();
                _capture.Dispose();
                return Task.CompletedTask;
            });
        }
    }

    private void DetachCaptureEvents()
    {
        if (!_eventsAttached) return;
        _capture.Failed -= Capture_Failed;
        _capture.RecordLimitationExceeded -= Capture_RecordLimitationExceeded;
        _eventsAttached = false;
    }

    private Task OnCaptureThreadAsync(Func<Task> action)
    {
        if (_captureQueue.HasThreadAccess) return action();

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_captureQueue.TryEnqueue(async () =>
        {
            try
            {
                await action();
                completion.TrySetResult();
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }))
            completion.TrySetException(new InvalidOperationException(
                "The camera dispatcher is unavailable during recording cleanup."));
        return completion.Task;
    }
}
