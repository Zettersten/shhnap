using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Shnapp.App.Windows;
using Microsoft.UI;

namespace Shnapp.App.Capture;

internal sealed class GraphicsCapture(CanvasDevice device)
{
    private readonly CanvasDevice _device = device ?? throw new ArgumentNullException(nameof(device));
    private static readonly Guid CaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    internal Task<CanvasBitmap> WindowAsync(nint hwnd, CancellationToken cancellationToken) =>
        CaptureAsync(CreateItem(hwnd, isMonitor: false), cancellationToken);

    internal Task<CanvasBitmap> MonitorAsync(nint monitor, CancellationToken cancellationToken) =>
        CaptureAsync(CreateItem(monitor, isMonitor: true), cancellationToken);

    private static GraphicsCaptureItem CreateItem(nint handle, bool isMonitor)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new NotSupportedException("Windows Graphics Capture is unavailable on this desktop.");
        }

        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid iid = CaptureItemId;
        int result = isMonitor
            ? interop.CreateForMonitor(handle, in iid, out nint item)
            : interop.CreateForWindow(handle, in iid, out item);
        Marshal.ThrowExceptionForHR(result);
        try
        {
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            Marshal.Release(item);
        }
    }

    private async Task<CanvasBitmap> CaptureAsync(GraphicsCaptureItem item, CancellationToken cancellationToken)
    {
        if (item.Size.Width <= 0 || item.Size.Height <= 0 ||
            (long)item.Size.Width * item.Size.Height > 64_000_000)
        {
            throw new InvalidOperationException("This capture is empty or exceeds Shnapp's 64-megapixel safety limit.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var completion = new TaskCompletionSource<CanvasBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        int completed = 0;
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            (IDirect3DDevice)_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;

        void OnFrame(Direct3D11CaptureFramePool sender, object args)
        {
            if (Interlocked.CompareExchange(ref completed, 1, 0) != 0)
            {
                return;
            }

            try
            {
                using Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
                if (frame is null || frame.ContentSize.Width <= 0 || frame.ContentSize.Height <= 0)
                {
                    throw new InvalidOperationException("Windows did not return a usable capture frame.");
                }

                using CanvasBitmap surface = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface);
                int width = Math.Min(frame.ContentSize.Width, (int)surface.SizeInPixels.Width);
                int height = Math.Min(frame.ContentSize.Height, (int)surface.SizeInPixels.Height);
                byte[] pixels = surface.GetPixelBytes(0, 0, width, height);
                CanvasBitmap bitmap = CanvasBitmap.CreateFromBytes(_device, pixels, width, height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
                if (!completion.TrySetResult(bitmap))
                {
                    bitmap.Dispose();
                }
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        pool.FrameArrived += OnFrame;
        using CancellationTokenRegistration registration = timeout.Token.Register(
            () => completion.TrySetCanceled(timeout.Token));
        try
        {
            session.StartCapture();
            return await completion.Task;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "The window did not provide a capture. Restore it and try again; protected content cannot be shnapped.");
        }
        finally
        {
            pool.FrameArrived -= OnFrame;
        }
    }

    internal async Task<(CanvasBitmap Bitmap, NativeMethods.Rect Bounds)> DesktopAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MonitorTarget> monitors = NativeMethods.Monitors();
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("No display is available for shnapping.");
        }

        NativeMethods.Rect bounds = new()
        {
            Left = monitors.Min(m => m.Bounds.Left),
            Top = monitors.Min(m => m.Bounds.Top),
            Right = monitors.Max(m => m.Bounds.Right),
            Bottom = monitors.Max(m => m.Bounds.Bottom),
        };
        if ((long)bounds.Width * bounds.Height > 64_000_000)
        {
            throw new InvalidOperationException("The combined desktop exceeds the 64-megapixel capture limit.");
        }

        var desktop = new CanvasRenderTarget(_device, bounds.Width, bounds.Height, 96);
        try
        {
            using CanvasDrawingSession drawing = desktop.CreateDrawingSession();
            drawing.Clear(Colors.Transparent);
            foreach (MonitorTarget monitor in monitors)
            {
                using CanvasBitmap bitmap = await MonitorAsync(monitor.Handle, cancellationToken);
                drawing.DrawImage(bitmap, monitor.Bounds.Left - bounds.Left, monitor.Bounds.Top - bounds.Top);
            }

            return (desktop, bounds);
        }
        catch
        {
            desktop.Dispose();
            throw;
        }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(nint window, in Guid iid, out nint result);

        [PreserveSig]
        int CreateForMonitor(nint monitor, in Guid iid, out nint result);
    }
}
