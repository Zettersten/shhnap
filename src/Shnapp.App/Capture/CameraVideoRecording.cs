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
            VideoEncodingProperties recordFormat =
                await SelectEfficientRecordFormatAsync(capture, token);

            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(
                System.IO.Path.GetDirectoryName(path)!).AsTask(token);
            StorageFile file = await folder.CreateFileAsync(
                System.IO.Path.GetFileName(path), CreationCollisionOption.FailIfExists)
                .AsTask(token);
            fileCreated = true;
            MediaEncodingProfile profile = CreateBoundedRecordingProfile(recordFormat);
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

    private static async Task<VideoEncodingProperties> SelectEfficientRecordFormatAsync(MediaCapture capture,
        CancellationToken token)
    {
        // Limit camera input as well as output. Width/height can be portrait,
        // so compare the long and short edges instead of assuming landscape.
        VideoEncodingProperties? applied = null;
        try
        {
            VideoEncodingProperties[] formats = capture.VideoDeviceController
                .GetAvailableMediaStreamProperties(MediaStreamType.VideoRecord)
                .OfType<VideoEncodingProperties>()
                .Where(format => format.Width > 0 && format.Height > 0 &&
                    double.IsFinite(FramesPerSecond(format)) &&
                    FramesPerSecond(format) > 0)
                .ToArray();
            VideoEncodingProperties? selected = formats
                .Where(format => IsBounded(format) && IsUsefulSize(format) &&
                    FramesPerSecond(format) <= 30.5)
                .OrderByDescending(PixelCount)
                .ThenByDescending(FramesPerSecond)
                .FirstOrDefault()
                ?? formats.Where(format => IsBounded(format) && IsUsefulSize(format))
                    .OrderByDescending(PixelCount)
                    .ThenBy(FramesPerSecond)
                    .FirstOrDefault()
                ?? formats.Where(IsBounded)
                    .OrderByDescending(PixelCount)
                    .ThenBy(FramesPerSecond)
                    .FirstOrDefault()
                ?? formats.OrderBy(PixelCount)
                    .ThenBy(IsNativeH264)
                    .ThenBy(FramesPerSecond)
                    .FirstOrDefault();
            if (selected is not null)
            {
                await capture.VideoDeviceController.SetMediaStreamPropertiesAsync(
                    MediaStreamType.VideoRecord, selected).AsTask(token);
                applied = selected;
            }
        }
        catch (Exception error) when (error is COMException or ArgumentException or
            InvalidOperationException)
        {
            // Some drivers do not expose a settable record pin. Their default
            // record format is still inspected before choosing the encoder.
        }
        try
        {
            if (capture.VideoDeviceController.GetMediaStreamProperties(
                MediaStreamType.VideoRecord) is VideoEncodingProperties current &&
                current.Width > 0 && current.Height > 0)
                return current;
        }
        catch (Exception error) when (error is COMException or ArgumentException or
            InvalidOperationException) { }
        return applied ?? throw new InvalidOperationException(
            "The camera did not report a usable video recording format.");
    }

    private static MediaEncodingProfile CreateBoundedRecordingProfile(
        VideoEncodingProperties input)
    {
        // A bounded native H.264 stream can be passed through without an extra
        // encode. Other inputs need an explicit output profile: Auto may restore
        // the camera's 1080p or 4K default despite selecting a smaller input.
        if (IsBounded(input) && IsNativeH264(input) &&
            FramesPerSecond(input) <= 30.5)
        {
            MediaEncodingProfile native = MediaEncodingProfile.CreateMp4(
                VideoEncodingQuality.Auto);
            native.Audio = null;
            return native;
        }

        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(
            VideoEncodingQuality.HD720p);
        uint longEdge = Math.Max(input.Width, input.Height);
        uint shortEdge = Math.Min(input.Width, input.Height);
        double scale = Math.Min(1d, Math.Min(1280d / longEdge, 720d / shortEdge));
        uint width = Math.Max(2u, (uint)(Math.Floor(input.Width * scale / 2) * 2));
        uint height = Math.Max(2u, (uint)(Math.Floor(input.Height * scale / 2) * 2));
        profile.Video.Width = width;
        profile.Video.Height = height;
        uint bitrate = (uint)Math.Clamp(4_000_000d * width * height / (1280 * 720),
            750_000d, 4_000_000d);
        profile.Video.Bitrate = profile.Video.Bitrate == 0
            ? bitrate : Math.Min(profile.Video.Bitrate, bitrate);
        profile.Audio = null;
        return profile;
    }

    private static bool IsBounded(VideoEncodingProperties format) =>
        Math.Max(format.Width, format.Height) <= 1280 &&
        Math.Min(format.Width, format.Height) <= 720;

    private static bool IsUsefulSize(VideoEncodingProperties format) =>
        Math.Max(format.Width, format.Height) >= 640 &&
        Math.Min(format.Width, format.Height) >= 360;

    private static bool IsNativeH264(VideoEncodingProperties format) =>
        string.Equals(format.Subtype, "H264", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format.Subtype, "AVC1", StringComparison.OrdinalIgnoreCase);

    private static ulong PixelCount(VideoEncodingProperties format) =>
        (ulong)format.Width * format.Height;

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
