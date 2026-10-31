using System.Buffers;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Shnapp.App.Capture;

// Each WASAPI endpoint writes to its own bounded stream. No captured audio is mixed into the MP4.
internal sealed class VideoAudioStem : IAsyncDisposable
{
    private const int SampleRate = 48_000;
    private const int BytesPerFrame = 4; // 16-bit stereo PCM
    private const long MaxWavFrames = (uint.MaxValue - 44L) / BytesPerFrame;
    private static readonly byte[] Silence = new byte[16_384];

    private readonly WasapiRecorder _recorder;
    private readonly MMDeviceEnumerator? _deviceEnumerator;
    private readonly MMDevice? _device;
    private readonly WaveFileWriter _writer;
    private readonly Channel<AudioPacket> _packets;
    private readonly Task _writeTask;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<long, int, AudioSlice?> _mapPacket;
    private readonly Action<Exception> _reportFailure;
    private readonly string _path;
    private bool _started;
    private volatile bool _accepting;
    private volatile bool _stopRequested;
    private int _disposed;
    private long _writtenFrames;

    internal VideoAudioStem(
        string path,
        bool loopback,
        string? deviceId,
        Func<long, int, AudioSlice?> mapPacket,
        Action<Exception> reportFailure)
    {
        _path = path;
        _mapPacket = mapPacket;
        _reportFailure = reportFailure;

        try
        {
            var builder = new WasapiRecorderBuilder().WithFormat(new WaveFormat(SampleRate, 16, 2));
            if (loopback)
            {
                builder = builder.WithLoopbackCapture();
            }
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                _deviceEnumerator = new MMDeviceEnumerator();
                _device = _deviceEnumerator.GetDevice(deviceId);
                if (_device.State != DeviceState.Active || _device.DataFlow != (loopback ? DataFlow.Render : DataFlow.Capture))
                {
                    throw new InvalidOperationException("The selected audio device is not available for capture.");
                }
                builder = builder.WithDevice(_device);
            }

            _recorder = builder.Build();
            if (_recorder.WaveFormat.SampleRate != SampleRate ||
                _recorder.WaveFormat.BitsPerSample != 16 ||
                _recorder.WaveFormat.Channels != 2)
            {
                throw new InvalidOperationException("The audio device could not provide 48 kHz stereo PCM capture.");
            }

            _writer = new WaveFileWriter(path, _recorder.WaveFormat);
            _packets = Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
            _writeTask = Task.Run(WritePacketsAsync);
            _recorder.DataAvailable += OnDataAvailable;
            _recorder.RecordingStopped += OnRecordingStopped;
        }
        catch
        {
            _writer?.Dispose();
            _recorder?.Dispose();
            _device?.Dispose();
            _deviceEnumerator?.Dispose();
            throw;
        }
    }

    internal string Path => _path;

    internal void Start()
    {
        if (_started)
        {
            throw new InvalidOperationException("Audio capture has already started.");
        }
        _accepting = true;
        _started = true;
        try
        {
            _recorder.StartRecording();
        }
        catch
        {
            _accepting = false;
            _stopped.TrySetResult();
            throw;
        }
    }

    internal async Task StopAsync(TimeSpan duration, bool padToDuration)
    {
        _accepting = false;
        _stopRequested = true;
        if (_started && !_stopped.Task.IsCompleted)
        {
            _recorder.StopRecording();
            await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        _packets.Writer.TryComplete();
        await _writeTask.ConfigureAwait(false);
        if (padToDuration)
        {
            long requiredFrames = Math.Max(0, (long)Math.Ceiling(duration.TotalSeconds * SampleRate));
            WriteSilenceTo(requiredFrames);
        }
        _writer.Flush();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _accepting = false;
        _stopRequested = true;
        if (_started && !_stopped.Task.IsCompleted)
        {
            _recorder.StopRecording();
            try
            {
                await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Teardown must continue if the endpoint disappeared.
            }
        }
        _packets.Writer.TryComplete();
        try
        {
            await _writeTask.ConfigureAwait(false);
        }
        finally
        {
            Exception? cleanupError = null;
            try { _writer.Dispose(); } catch (Exception error) { cleanupError = error; }
            try { _recorder.Dispose(); } catch (Exception error) { cleanupError ??= error; }
            try { _device?.Dispose(); } catch (Exception error) { cleanupError ??= error; }
            try { _deviceEnumerator?.Dispose(); } catch (Exception error) { cleanupError ??= error; }
            if (cleanupError is not null)
            {
                throw cleanupError;
            }
        }
    }

    private void OnDataAvailable(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (!_accepting || data.Length < BytesPerFrame)
        {
            return;
        }
        try
        {
            AudioSlice? slice = _mapPacket(qpcPosition, data.Length / BytesPerFrame);
            if (slice is not { FrameCount: > 0 } mapped)
            {
                return;
            }
            int byteCount = checked(mapped.FrameCount * BytesPerFrame);
            byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
            if ((flags & AudioClientBufferFlags.Silent) != 0)
            {
                rented.AsSpan(0, byteCount).Clear();
            }
            else
            {
                data.Slice(mapped.SourceFrameOffset * BytesPerFrame, byteCount).CopyTo(rented);
            }
            if (!_packets.Writer.TryWrite(new AudioPacket(rented, byteCount, mapped.OutputStartFrame)))
            {
                ArrayPool<byte>.Shared.Return(rented);
                throw new IOException("Audio capture cannot keep up with disk writes.");
            }
        }
        catch (Exception exception)
        {
            _accepting = false;
            _reportFailure(exception);
        }
    }

    private void OnRecordingStopped(object? _, StoppedEventArgs args)
    {
        _accepting = false;
        if (args.Exception is { } error)
        {
            _reportFailure(error);
            _stopped.TrySetException(error);
        }
        else
        {
            if (!_stopRequested)
            {
                _reportFailure(new IOException("An audio capture device stopped unexpectedly."));
            }
            _stopped.TrySetResult();
        }
    }

    private async Task WritePacketsAsync()
    {
        try
        {
            await foreach (AudioPacket packet in _packets.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    WriteSilenceTo(packet.StartFrame);
                    int skippedFrames = (int)Math.Clamp(_writtenFrames - packet.StartFrame, 0, packet.Length / BytesPerFrame);
                    int byteCount = packet.Length - skippedFrames * BytesPerFrame;
                    if (byteCount > 0)
                    {
                        EnsureCapacity(_writtenFrames + byteCount / BytesPerFrame);
                        _writer.Write(packet.Buffer, skippedFrames * BytesPerFrame, byteCount);
                        _writtenFrames += byteCount / BytesPerFrame;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(packet.Buffer);
                }
            }
        }
        catch (Exception error)
        {
            _accepting = false;
            _reportFailure(error);
            throw;
        }
        finally
        {
            while (_packets.Reader.TryRead(out AudioPacket? packet))
            {
                ArrayPool<byte>.Shared.Return(packet.Buffer);
            }
        }
    }

    private void WriteSilenceTo(long frame)
    {
        EnsureCapacity(frame);
        while (_writtenFrames < frame)
        {
            int frames = (int)Math.Min((frame - _writtenFrames), Silence.Length / BytesPerFrame);
            _writer.Write(Silence, 0, frames * BytesPerFrame);
            _writtenFrames += frames;
        }
    }

    private static void EnsureCapacity(long frame)
    {
        if (frame > MaxWavFrames)
        {
            throw new IOException("The uncompressed audio track reached the WAV size limit.");
        }
    }

    private sealed record AudioPacket(byte[] Buffer, int Length, long StartFrame);
}

internal readonly record struct AudioSlice(int SourceFrameOffset, int FrameCount, long OutputStartFrame);
