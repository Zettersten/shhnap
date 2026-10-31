using System.ComponentModel;
using System.Runtime.InteropServices;
using ScreenRecorderLib;
using Shnapp.App.Windows;

namespace Shnapp.App.Capture;

internal sealed class VideoRecordingService
{
    internal async Task<VideoRecordingSession> StartAsync(
        VideoCaptureTarget target,
        string outputPath,
        VideoCaptureOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (options.FrameRate is not (30 or 60 or 120))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Video frame rate must be 30, 60, or 120 fps.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Video output must be an MP4 file.", nameof(outputPath));
        }
        if (File.Exists(fullPath))
        {
            throw new IOException($"Video output already exists: {fullPath}");
        }
        string stem = Path.Combine(Path.GetDirectoryName(fullPath)!, Path.GetFileNameWithoutExtension(fullPath));
        if ((options.CaptureSystemAudio && File.Exists(stem + ".system.wav")) ||
            (options.CaptureMicrophone && File.Exists(stem + ".microphone.wav")))
        {
            throw new IOException("A recording audio track already exists at the selected output path.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        CaptureGeometry geometry = ResolveTarget(target, options.CaptureCursor);
        var recorderOptions = RecorderOptions.Default;
        recorderOptions.SourceOptions = new SourceOptions { RecordingSources = geometry.Sources };
        recorderOptions.OutputOptions = new OutputOptions
        {
            RecorderMode = RecorderMode.Video,
            OutputFrameSize = new ScreenSize(geometry.Width, geometry.Height)
        };
        recorderOptions.VideoEncoderOptions = new VideoEncoderOptions
        {
            Encoder = new H264VideoEncoder(),
            Framerate = options.FrameRate,
            IsHardwareEncodingEnabled = true,
            IsThrottlingDisabled = false,
            IsFixedFramerate = false,
            IsMp4FastStartEnabled = true
        };
        recorderOptions.AudioOptions = new AudioOptions { IsAudioEnabled = false };
        recorderOptions.MouseOptions = new MouseOptions { IsMousePointerEnabled = options.CaptureCursor };

        Recorder recorder = Recorder.CreateRecorder(recorderOptions);
        var session = new VideoRecordingSession(
            recorder, fullPath, geometry.Width, geometry.Height, options,
            SynchronizationContext.Current);
        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static CaptureGeometry ResolveTarget(VideoCaptureTarget target, bool captureCursor) => target switch
    {
        VideoCaptureTarget.Monitor monitor => ResolveMonitor(monitor.Handle, captureCursor),
        VideoCaptureTarget.Window window => ResolveWindow(window.Handle, captureCursor),
        VideoCaptureTarget.Region region => ResolveRegion(region.Bounds, captureCursor),
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    private static CaptureGeometry ResolveMonitor(nint handle, bool captureCursor)
    {
        MonitorInfoEx monitor = GetMonitor(handle);
        var source = CreateDisplaySource(monitor.DeviceName, captureCursor);
        return new CaptureGeometry([source], Even(monitor.Monitor.Width), Even(monitor.Monitor.Height));
    }

    private static CaptureGeometry ResolveWindow(nint handle, bool captureCursor)
    {
        if (handle == 0 || !NativeMethods.IsWindowVisible(handle) || !NativeMethods.GetWindowRect(handle, out var bounds))
        {
            throw new ArgumentException("The selected window is no longer available.", nameof(handle));
        }
        ValidateSize(bounds.Width, bounds.Height);
        var source = new WindowRecordingSource(handle)
        {
            IsCursorCaptureEnabled = captureCursor
        };
        return new CaptureGeometry([source], Even(bounds.Width), Even(bounds.Height));
    }

    private static CaptureGeometry ResolveRegion(NativeMethods.Rect region, bool captureCursor)
    {
        ValidateSize(region.Width, region.Height);
        List<RecordingSourceBase> sources = [];
        List<nint> monitors = [];
        bool enumerated = NativeMethods.EnumDisplayMonitors(0, 0,
            (nint handle, nint hdc, ref NativeMethods.Rect bounds, nint data) =>
        {
            monitors.Add(handle);
            return true;
        }, 0);
        if (!enumerated)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate displays for region capture.");
        }
        foreach (nint handle in monitors)
        {
            MonitorInfoEx monitor = GetMonitor(handle);
            NativeMethods.Rect intersection = new()
            {
                Left = Math.Max(region.Left, monitor.Monitor.Left),
                Top = Math.Max(region.Top, monitor.Monitor.Top),
                Right = Math.Min(region.Right, monitor.Monitor.Right),
                Bottom = Math.Min(region.Bottom, monitor.Monitor.Bottom)
            };
            if (intersection.Width <= 0 || intersection.Height <= 0)
            {
                continue;
            }

            var source = CreateDisplaySource(monitor.DeviceName, captureCursor);
            source.SourceRect = new ScreenRect(
                intersection.Left - monitor.Monitor.Left,
                intersection.Top - monitor.Monitor.Top,
                intersection.Width,
                intersection.Height);
            source.Position = new ScreenPoint(intersection.Left - region.Left, intersection.Top - region.Top);
            source.OutputSize = new ScreenSize(intersection.Width, intersection.Height);
            source.AnchorPoint = Anchor.TopLeft;
            source.Stretch = StretchMode.Fill;
            sources.Add(source);
        }
        if (sources.Count == 0)
        {
            throw new InvalidOperationException("The selected region does not overlap an available display.");
        }
        return new CaptureGeometry(sources, Even(region.Width), Even(region.Height));
    }

    private static DisplayRecordingSource CreateDisplaySource(string name, bool captureCursor) => new(name)
    {
        RecorderApi = RecorderApi.WindowsGraphicsCapture,
        IsCursorCaptureEnabled = captureCursor
    };

    private static MonitorInfoEx GetMonitor(nint handle)
    {
        if (handle == 0)
        {
            throw new ArgumentException("The selected display is no longer available.", nameof(handle));
        }
        MonitorInfoEx info = new() { Size = (uint)Marshal.SizeOf<MonitorInfoEx>(), DeviceName = string.Empty };
        if (!GetMonitorInfoEx(handle, ref info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not inspect the selected display.");
        }
        ValidateSize(info.Monitor.Width, info.Monitor.Height);
        return info;
    }

    private static int Even(int value) => checked(value + (value & 1));

    private static void ValidateSize(int width, int height)
    {
        if (width < 2 || height < 2 || width > 16384 || height > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Capture dimensions must be between 2 and 16384 pixels per side.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        internal uint Size;
        internal NativeMethods.Rect Monitor;
        internal NativeMethods.Rect WorkArea;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
    }

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoEx(nint monitor, ref MonitorInfoEx info);

    private sealed record CaptureGeometry(List<RecordingSourceBase> Sources, int Width, int Height);
}
