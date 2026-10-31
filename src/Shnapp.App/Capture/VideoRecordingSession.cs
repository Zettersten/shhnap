using System.Diagnostics;
using System.Buffers.Binary;
using Microsoft.UI.Dispatching;
using ScreenRecorderLib;

namespace Shnapp.App.Capture;

internal sealed class VideoRecordingSession
{
    private const long QpcTicksPerSecond = 10_000_000;
    private const int AudioFramesPerSecond = 48_000;

    private readonly object _gate = new();
    private readonly Recorder _recorder;
    private readonly string _videoPath;
    private readonly string? _systemAudioPath;
    private readonly string? _microphoneAudioPath;
    private readonly int _width;
    private readonly int _height;
    private readonly VideoCaptureOptions _options;
    private readonly SynchronizationContext? _context;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _nativeFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _audioStartDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<VideoRecordingResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private VideoAudioStem? _systemAudio;
    private VideoAudioStem? _microphoneAudio;
    private VideoRecordingState _state = VideoRecordingState.Starting;
    private DateTimeOffset _startedAt;
    private long _startQpc;
    private long _pauseStartQpc;
    private long _lastResumeQpc;
    private long _pausedQpc;
    private long _endQpc;
    private bool _recordRequested;
    private bool _cancelRequested;
    private Exception? _audioError;
    private int _finalizeWatchdogArmed;

    internal VideoRecordingSession(
        Recorder recorder,
        string videoPath,
        int width,
        int height,
        VideoCaptureOptions options,
        SynchronizationContext? context)
    {
        _recorder = recorder;
        _videoPath = videoPath;
        _systemAudioPath = options.CaptureSystemAudio ? Path.Combine(
            Path.GetDirectoryName(videoPath)!, Path.GetFileNameWithoutExtension(videoPath) + ".system.wav") : null;
        _microphoneAudioPath = options.CaptureMicrophone ? Path.Combine(
            Path.GetDirectoryName(videoPath)!, Path.GetFileNameWithoutExtension(videoPath) + ".microphone.wav") : null;
        _width = width;
        _height = height;
        _options = options;
        _context = context;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _recorder.OnStatusChanged += OnStatusChanged;
        _recorder.OnRecordingComplete += OnRecordingComplete;
        _recorder.OnRecordingFailed += OnRecordingFailed;
        _ = CompleteAsync();
    }

    internal event EventHandler<VideoRecordingStateChangedEventArgs>? StateChanged;

    internal VideoRecordingState State
    {
        get { lock (_gate) { return _state; } }
    }

    internal TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                if (_startQpc == 0)
                {
                    return TimeSpan.Zero;
                }
                long now = _endQpc != 0 ? _endQpc : NowQpc();
                long paused = _pausedQpc + (_pauseStartQpc != 0 ? now - _pauseStartQpc : 0);
                return TimeSpan.FromTicks(Math.Max(0, now - _startQpc - paused));
            }
        }
    }

    internal Task<VideoRecordingResult> Completion => _completion.Task;

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Device resolution happens before the native recorder starts, so unavailable
            // microphone and loopback endpoints fail without leaving a live capture.
            if (_systemAudioPath is not null)
            {
                _systemAudio = new VideoAudioStem(_systemAudioPath, loopback: true, deviceId: null,
                    MapAudioPacket, OnAudioFailure);
            }
            if (_microphoneAudioPath is not null)
            {
                _microphoneAudio = new VideoAudioStem(_microphoneAudioPath, loopback: false,
                    _options.MicrophoneSourceId, MapAudioPacket, OnAudioFailure);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _recordRequested = true;
            try
            {
                _recorder.Record(_videoPath);
            }
            catch
            {
                _recordRequested = false;
                throw;
            }
            await _started.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _systemAudio?.Start();
            _microphoneAudio?.Start();
        }
        catch (Exception error)
        {
            _audioError ??= error;
            if (!_recordRequested)
            {
                _nativeFinished.TrySetException(error);
            }
            else
            {
                RequestStop(cancel: true);
            }
            throw;
        }
        finally
        {
            _audioStartDone.TrySetResult();
        }
    }

    internal Task PauseAsync()
    {
        lock (_gate)
        {
            if (_state != VideoRecordingState.Recording)
            {
                return Task.CompletedTask;
            }
            _pauseStartQpc = NowQpc();
            _state = VideoRecordingState.Paused;
        }
        try
        {
            _recorder.Pause();
            Publish(VideoRecordingState.Paused);
        }
        catch
        {
            lock (_gate)
            {
                _pauseStartQpc = 0;
                _state = VideoRecordingState.Recording;
            }
            throw;
        }
        return Task.CompletedTask;
    }

    internal Task ResumeAsync()
    {
        lock (_gate)
        {
            if (_state != VideoRecordingState.Paused)
            {
                return Task.CompletedTask;
            }
            long resumed = NowQpc();
            _pausedQpc += resumed - _pauseStartQpc;
            _pauseStartQpc = 0;
            _lastResumeQpc = resumed;
            _state = VideoRecordingState.Recording;
        }
        try
        {
            _recorder.Resume();
            Publish(VideoRecordingState.Recording);
        }
        catch
        {
            OnAudioFailure(new InvalidOperationException("Video recording could not resume."));
            throw;
        }
        return Task.CompletedTask;
    }

    internal async Task<VideoRecordingResult> StopAsync()
    {
        RequestStop(cancel: false);
        return await Completion.ConfigureAwait(false);
    }

    internal async Task CancelAsync()
    {
        RequestStop(cancel: true);
        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancellation and recording errors both remove this session's partial files.
        }
    }

    private void RequestStop(bool cancel)
    {
        bool shouldStop;
        lock (_gate)
        {
            _cancelRequested |= cancel;
            if (_state is VideoRecordingState.Finishing or VideoRecordingState.Completed or
                VideoRecordingState.Failed or VideoRecordingState.Cancelled)
            {
                return;
            }
            _endQpc = NowQpc();
            if (_pauseStartQpc != 0)
            {
                _pausedQpc += _endQpc - _pauseStartQpc;
                _pauseStartQpc = 0;
            }
            _state = VideoRecordingState.Finishing;
            shouldStop = _recordRequested;
        }
        Publish(VideoRecordingState.Finishing);
        if (!shouldStop)
        {
            _nativeFinished.TrySetCanceled();
            return;
        }
        ArmFinalizeWatchdog();
        try
        {
            _recorder.Stop();
        }
        catch (Exception error)
        {
            _nativeFinished.TrySetException(error);
        }
    }

    private void OnStatusChanged(object? sender, RecordingStatusEventArgs args)
    {
        if (args.Status == RecorderStatus.Recording)
        {
            bool initial = false;
            lock (_gate)
            {
                if (_startQpc == 0)
                {
                    _startQpc = NowQpc();
                    _lastResumeQpc = _startQpc;
                    _startedAt = DateTimeOffset.UtcNow;
                    if (_endQpc == 0)
                    {
                        _state = VideoRecordingState.Recording;
                        initial = true;
                    }
                }
            }
            _started.TrySetResult();
            if (initial)
            {
                Publish(VideoRecordingState.Recording);
            }
        }
        else if (args.Status == RecorderStatus.Finishing)
        {
            lock (_gate)
            {
                if (_endQpc == 0)
                {
                    _endQpc = NowQpc();
                }
                _state = VideoRecordingState.Finishing;
            }
            Publish(VideoRecordingState.Finishing);
        }
    }

    private void OnRecordingComplete(object? sender, RecordingCompleteEventArgs args)
    {
        if (!_started.Task.IsCompleted)
        {
            _started.TrySetException(new InvalidOperationException("Recording ended before video capture became live."));
        }
        _nativeFinished.TrySetResult();
    }

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs args)
    {
        var error = new IOException(string.IsNullOrWhiteSpace(args.Error)
            ? "Video capture failed."
            : $"Video capture failed: {args.Error}");
        _started.TrySetException(error);
        _nativeFinished.TrySetException(error);
    }

    private void OnAudioFailure(Exception error)
    {
        lock (_gate)
        {
            _audioError ??= error;
        }
        _ = Task.Run(() => RequestStop(cancel: false));
    }

    private void ArmFinalizeWatchdog()
    {
        if (Interlocked.Exchange(ref _finalizeWatchdogArmed, 1) != 0)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await _nativeFinished.Task.WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _nativeFinished.TrySetException(new TimeoutException(
                    "The video encoder did not finish within 90 seconds after stopping."));
            }
            catch (Exception)
            {
                // Completion already carries the native failure.
            }
        });
    }

    private AudioSlice? MapAudioPacket(long packetQpc, int frameCount)
    {
        lock (_gate)
        {
            if (_startQpc == 0 || frameCount <= 0)
            {
                return null;
            }
            long packetEnd = packetQpc + (long)Math.Ceiling(frameCount * (double)QpcTicksPerSecond / AudioFramesPerSecond);
            long start = Math.Max(packetQpc, Math.Max(_startQpc, _lastResumeQpc));
            long end = Math.Min(packetEnd, _pauseStartQpc != 0 ? _pauseStartQpc :
                _endQpc != 0 ? _endQpc : long.MaxValue);
            if (end <= start)
            {
                return null;
            }
            int sourceStart = Math.Clamp((int)Math.Ceiling((start - packetQpc) *
                (double)AudioFramesPerSecond / QpcTicksPerSecond), 0, frameCount);
            int sourceEnd = Math.Clamp((int)Math.Floor((end - packetQpc) *
                (double)AudioFramesPerSecond / QpcTicksPerSecond), sourceStart, frameCount);
            long outputStart = Math.Max(0, (long)Math.Round((start - _startQpc - _pausedQpc) *
                (double)AudioFramesPerSecond / QpcTicksPerSecond));
            return new AudioSlice(sourceStart, sourceEnd - sourceStart, outputStart);
        }
    }

    private async Task CompleteAsync()
    {
        Exception? failure = null;
        bool cancelled = false;
        TimeSpan duration = TimeSpan.Zero;
        try
        {
            await _nativeFinished.Task.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }
        await _audioStartDone.Task.ConfigureAwait(false);
        lock (_gate)
        {
            if (_endQpc == 0)
            {
                _endQpc = NowQpc();
            }
            cancelled = _cancelRequested;
            failure ??= _audioError;
        }
        duration = Elapsed;
        if (failure is null && !cancelled && duration <= TimeSpan.Zero)
        {
            failure = new IOException("Recording ended before a video frame could be captured.");
        }
        try
        {
            if (_systemAudio is not null)
            {
                await _systemAudio.StopAsync(duration, padToDuration: failure is null && !cancelled)
                    .ConfigureAwait(false);
            }
            if (_microphoneAudio is not null)
            {
                await _microphoneAudio.StopAsync(duration, padToDuration: failure is null && !cancelled)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            failure ??= error;
        }

        if (_systemAudio is not null)
        {
            try { await _systemAudio.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
        }
        if (_microphoneAudio is not null)
        {
            try { await _microphoneAudio.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
        }
        try
        {
            _recorder.OnStatusChanged -= OnStatusChanged;
            _recorder.OnRecordingComplete -= OnRecordingComplete;
            _recorder.OnRecordingFailed -= OnRecordingFailed;
            _recorder.Dispose();
        }
        catch (Exception error) { failure ??= error; }

        lock (_gate) { cancelled = _cancelRequested; }
        if (failure is not null || cancelled)
        {
            DeletePartialFiles();
            lock (_gate) { _state = cancelled ? VideoRecordingState.Cancelled : VideoRecordingState.Failed; }
            Publish(cancelled ? VideoRecordingState.Cancelled : VideoRecordingState.Failed, failure);
            if (cancelled)
            {
                _completion.TrySetCanceled();
            }
            else
            {
                _completion.TrySetException(failure!);
            }
            return;
        }

        TimeSpan? mp4Duration;
        try { mp4Duration = ProbeMp4Duration(_videoPath); }
        catch (Exception error)
        {
            failure = error;
            mp4Duration = null;
        }
        if (mp4Duration is null)
        {
            DeletePartialFiles();
            var error = failure ?? new IOException("The video encoder did not produce a complete MP4 file.");
            lock (_gate) { _state = VideoRecordingState.Failed; }
            Publish(VideoRecordingState.Failed, error);
            _completion.TrySetException(error);
            return;
        }
        lock (_gate) { _state = VideoRecordingState.Completed; }
        var result = new VideoRecordingResult(_videoPath, _systemAudioPath, _microphoneAudioPath,
            _startedAt, mp4Duration.Value, _width, _height);
        Publish(VideoRecordingState.Completed);
        _completion.TrySetResult(result);
    }

    private void DeletePartialFiles()
    {
        foreach (string? path in new[] { _videoPath, _systemAudioPath, _microphoneAudioPath })
        {
            if (path is null)
            {
                continue;
            }
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static TimeSpan? ProbeMp4Duration(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        using var file = File.OpenRead(path);
        bool hasType = false;
        bool hasMovie = false;
        bool hasMedia = false;
        TimeSpan? duration = null;
        while (TryReadBox(file, file.Length, out uint type, out long boxEnd))
        {
            hasType |= type == 0x66747970; // ftyp
            if (type == 0x6d6f6f76) // moov
            {
                hasMovie = true;
                duration = ReadMovieDuration(file, boxEnd);
            }
            hasMedia |= type == 0x6d646174; // mdat
            file.Position = boxEnd;
        }
        return hasType && hasMovie && hasMedia && duration > TimeSpan.Zero ? duration : null;
    }

    private static TimeSpan? ReadMovieDuration(FileStream file, long movieEnd)
    {
        while (TryReadBox(file, movieEnd, out uint type, out long boxEnd))
        {
            if (type == 0x6d766864) // mvhd
            {
                Span<byte> body = stackalloc byte[32];
                int needed = file.ReadByte() == 1 ? 31 : 19;
                if (file.Position + needed > boxEnd)
                {
                    return null;
                }
                file.ReadExactly(body[..needed]);
                bool versionOne = needed == 31;
                uint scale = BinaryPrimitives.ReadUInt32BigEndian(
                    body.Slice(versionOne ? 19 : 11, 4));
                ulong units = versionOne
                    ? BinaryPrimitives.ReadUInt64BigEndian(body.Slice(23, 8))
                    : BinaryPrimitives.ReadUInt32BigEndian(body.Slice(15, 4));
                if (scale == 0 || units == 0)
                {
                    return null;
                }
                Int128 ticks = (Int128)units * TimeSpan.TicksPerSecond / scale;
                return ticks > 0 && ticks <= long.MaxValue ? TimeSpan.FromTicks((long)ticks) : null;
            }
            file.Position = boxEnd;
        }
        return null;
    }

    private static bool TryReadBox(FileStream file, long limit, out uint type, out long boxEnd)
    {
        type = 0;
        boxEnd = 0;
        long start = file.Position;
        if (start > limit - 8)
        {
            return false;
        }
        Span<byte> header = stackalloc byte[8];
        file.ReadExactly(header);
        ulong size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        int headerSize = 8;
        if (size == 1)
        {
            file.ReadExactly(header);
            size = BinaryPrimitives.ReadUInt64BigEndian(header);
            headerSize = 16;
        }
        else if (size == 0)
        {
            size = (ulong)(limit - start);
        }
        if (size < (ulong)headerSize || size > (ulong)(limit - start))
        {
            return false;
        }
        boxEnd = start + (long)size;
        return true;
    }

    private void Publish(VideoRecordingState state, Exception? error = null)
    {
        EventHandler<VideoRecordingStateChangedEventArgs>? handler = StateChanged;
        if (handler is null)
        {
            return;
        }
        var args = new VideoRecordingStateChangedEventArgs(state, Elapsed, error);
        void Notify()
        {
            try { handler(this, args); }
            catch (Exception) { /* A UI listener must not interrupt encoder finalization. */ }
        }
        if (_context is not null)
        {
            _context.Post(_ => Notify(), null);
        }
        else if (_dispatcherQueue is not null)
        {
            _dispatcherQueue.TryEnqueue(Notify);
        }
        else
        {
            Notify();
        }
    }

    private static long NowQpc() => (long)((Int128)Stopwatch.GetTimestamp() *
        QpcTicksPerSecond / Stopwatch.Frequency);
}
