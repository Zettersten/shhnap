using System.Runtime.InteropServices;
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
    private readonly SemaphoreSlim _operations = new(1, 1);
    private bool _started;
    private bool _finished;
    private bool _disposed;

    private CameraVideoRecording(MediaCapture capture, LowLagMediaRecording recording,
        string path)
    {
        _capture = capture;
        _recording = recording;
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
            await _recording.StartAsync();
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
                await _recording.PauseAsync(MediaCapturePauseBehavior.RetainHardwareResources);
        }
        finally { _operations.Release(); }
    }

    internal async Task ResumeAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (_started && !_finished) await _recording.ResumeAsync();
        }
        finally { _operations.Release(); }
    }

    internal async Task StopAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (_finished) return;
            if (_started) await _recording.StopAsync();
            await _recording.FinishAsync();
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
        _capture.Failed -= Capture_Failed;
        _capture.RecordLimitationExceeded -= Capture_RecordLimitationExceeded;
        try { await StopAsync(); }
        finally { _capture.Dispose(); }
    }
}
