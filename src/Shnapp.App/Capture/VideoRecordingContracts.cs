using Shnapp.App.Windows;

namespace Shnapp.App.Capture;

internal abstract record VideoCaptureTarget
{
    private VideoCaptureTarget() { }

    internal sealed record Monitor(nint Handle) : VideoCaptureTarget;
    internal sealed record Window(nint Handle) : VideoCaptureTarget;
    internal sealed record Region(NativeMethods.Rect Bounds) : VideoCaptureTarget;

    internal static VideoCaptureTarget ForMonitor(nint monitor) => new Monitor(monitor);
    internal static VideoCaptureTarget ForWindow(nint window) => new Window(window);
    internal static VideoCaptureTarget ForRegion(NativeMethods.Rect bounds) => new Region(bounds);
}

internal sealed record VideoCaptureOptions(
    int FrameRate,
    bool CaptureCursor,
    bool CaptureSystemAudio,
    bool CaptureMicrophone,
    string? MicrophoneSourceId,
    bool CaptureCamera,
    string? CameraSourceId);

internal sealed record VideoRecordingResult(
    string VideoPath,
    string? SystemAudioPath,
    string? MicrophoneAudioPath,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int PixelWidth,
    int PixelHeight,
    string? CameraVideoPath,
    TimeSpan CameraStart,
    TimeSpan CameraDuration,
    string? CameraWarning,
    string? AudioWarning);

internal enum VideoRecordingState
{
    Starting,
    Recording,
    Paused,
    Finishing,
    Completed,
    Failed,
    Cancelled
}

internal sealed class VideoRecordingStateChangedEventArgs(
    VideoRecordingState state,
    TimeSpan elapsed,
    Exception? error = null) : EventArgs
{
    internal VideoRecordingState State { get; } = state;
    internal TimeSpan Elapsed { get; } = elapsed;
    internal Exception? Error { get; } = error;
}
