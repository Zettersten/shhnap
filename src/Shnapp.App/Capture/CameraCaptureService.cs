using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Shnapp.App.Capture;

/// <summary>A video capture device available to Shnapp.</summary>
internal sealed record CameraSource(string Id, string Name);

/// <summary>Owns a single camera preview and still-photo session.</summary>
internal sealed class CameraCaptureService : IDisposable
{
    private const ulong MaximumPhotoBytes = 32UL * 1024 * 1024;

    private MediaCapture? _capture;
    private MediaPlayer? _player;
    private MediaPlayerElement? _preview;

    internal event Action<string>? Failed;

    /// <summary>Lists currently connected video cameras without opening a camera stream.</summary>
    internal static async Task<IReadOnlyList<CameraSource>> EnumerateSourcesAsync()
    {
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(
            MediaDevice.GetVideoCaptureSelector());
        return devices.Select(device => new CameraSource(device.Id, device.Name)).ToArray();
    }

    /// <summary>Starts previewing one source in the supplied WinUI media element.</summary>
    internal async Task StartAsync(string sourceId, MediaPlayerElement preview)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(preview);
        Dispose();

        var capture = new MediaCapture();
        MediaPlayer? player = null;
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = sourceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                MemoryPreference = MediaCaptureMemoryPreference.Auto,
            });

            MediaFrameSource? frameSource = capture.FrameSources.Values.FirstOrDefault(source =>
                source.Info.SourceKind == MediaFrameSourceKind.Color &&
                source.Info.MediaStreamType == MediaStreamType.VideoPreview)
                ?? capture.FrameSources.Values.FirstOrDefault(source =>
                    source.Info.SourceKind == MediaFrameSourceKind.Color &&
                    source.Info.MediaStreamType == MediaStreamType.VideoRecord);
            if (frameSource is null)
            {
                throw new InvalidOperationException("This camera does not provide a video preview. Choose another camera.");
            }

            player = new MediaPlayer
            {
                RealTimePlayback = true,
                AutoPlay = false,
                Source = MediaSource.CreateFromMediaFrameSource(frameSource),
            };
            capture.Failed += Capture_Failed;
            player.MediaFailed += Player_MediaFailed;
            preview.SetMediaPlayer(player);
            _capture = capture;
            _player = player;
            _preview = preview;
            player.Play();
        }
        catch
        {
            if (ReferenceEquals(_capture, capture))
            {
                Dispose();
            }
            else
            {
                // Initialization can fail before the session becomes owned by this service.
                player?.Dispose();
                capture.Dispose();
            }
            throw;
        }
    }

    /// <summary>Captures a full-resolution still photo as PNG bytes.</summary>
    internal async Task<byte[]> CapturePngAsync()
    {
        MediaCapture capture = _capture ??
            throw new InvalidOperationException("Start the camera before taking a photo.");
        using var stream = new InMemoryRandomAccessStream();
        await capture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreatePng(), stream);
        if (stream.Size == 0 || stream.Size > MaximumPhotoBytes)
        {
            throw new InvalidDataException("The photo exceeds Shnapp's 32 MB image limit. Try another camera.");
        }

        stream.Seek(0);
        using var reader = new DataReader(stream);
        await reader.LoadAsync(checked((uint)stream.Size));
        byte[] png = new byte[checked((int)stream.Size)];
        reader.ReadBytes(png);
        return png;
    }

    private void Capture_Failed(MediaCapture sender, MediaCaptureFailedEventArgs args) =>
        Failed?.Invoke("The camera stopped working or was disconnected. Choose another camera or try again.");

    private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
        Failed?.Invoke("The camera preview stopped working. Choose another camera or try again.");

    public void Dispose()
    {
        MediaPlayer? player = _player;
        MediaPlayerElement? preview = _preview;
        MediaCapture? capture = _capture;
        _player = null;
        _preview = null;
        _capture = null;

        if (player is not null)
        {
            player.MediaFailed -= Player_MediaFailed;
            try { player.Pause(); }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or ObjectDisposedException) { }
        }

        if (preview is not null)
        {
            try { preview.SetMediaPlayer(null!); }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or ObjectDisposedException) { }
        }

        try { player?.Dispose(); }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or ObjectDisposedException) { }
        if (capture is not null)
        {
            capture.Failed -= Capture_Failed;
            try { capture.Dispose(); }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or ObjectDisposedException) { }
        }
    }
}
