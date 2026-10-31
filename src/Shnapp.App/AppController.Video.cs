using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Shnapp.App.Capture;
using Shnapp.App.Video;
using Shnapp.App.Windows;
using Shnapp.Core;

namespace Shnapp.App;

internal sealed partial class AppController
{
    private VideoProjectStore? _videoStore;

    private VideoProjectStore VideoStore =>
        _videoStore ??= new VideoProjectStore(Path.Combine(Library.RootPath, "videos"));

    private async Task RefreshVideoLibraryAsync()
    {
        try
        {
            _page.SetVideoLibrary(await VideoStore.ListAsync(_lifetime.Token));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException)
        {
            _page.SetVideoLibrary([]);
            _page.ShowMessage("Video library unavailable", error.Message,
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
        }
    }

    internal void RecordVideo(CaptureKind kind) => Run(async () =>
    {
        try
        {
            await RecordVideoAsync(kind);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            if (_window.VideoPage is { } page)
            {
                page.ShowStatus("Recording failed: " + error.Message);
            }
            else
            {
                _page.ShowMessage("Video recording failed", error.Message,
                    Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            }
            Show();
        }
    });

    internal void OpenVideoProject(Guid id) => Run(() => OpenVideoProjectAsync(id));

    private async Task RecordVideoAsync(CaptureKind kind)
    {
        if (kind is not (CaptureKind.Region or CaptureKind.Window or CaptureKind.FullScreen))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        if (_dialogOpen || !await _captureGate.WaitAsync(0, _lifetime.Token))
        {
            return;
        }

        bool wasVisible = NativeMethods.IsWindowVisible(App.WindowHandle);
        _activeCapture = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken token = _activeCapture.Token;
        VideoRecordingSession? session = null;
        RecordingHudWindow? hud = null;
        DispatcherQueueTimer? clock = null;
        bool finalized = false;
        Guid projectId = Guid.Empty;
        string? projectDirectory = null;
        try
        {
            _window.ShowMainPage();
            if (_window.VideoPage is { } openVideo) { await openVideo.SaveAsync(); }
            NativeMethods.GetCursorPos(out NativeMethods.Point pointer);
            if (wasVisible)
            {
                _window.AppWindow.Hide();
                await WaitForCaptureReadyAsync(App.WindowHandle, token);
            }

            VideoSelection? selected = await SelectVideoTargetAsync(kind, pointer, token);
            if (selected is null)
            {
                if (wasVisible && !_quitting) { Show(); }
                return;
            }

            _page.CommitText();
            _saveTimer.Stop();
            await SaveCurrentAsync();

            projectId = Guid.NewGuid();
            projectDirectory = VideoStore.PrepareProjectDirectory(projectId);
            string outputPath = Path.Combine(projectDirectory, "source.mp4");
            var options = new VideoCaptureOptions(
                _settings.VideoFrameRate,
                _settings.VideoCaptureCursor,
                _settings.VideoCaptureSystemAudio,
                _settings.VideoCaptureMicrophone,
                _settings.VideoMicrophoneSourceId);
            session = await new VideoRecordingService().StartAsync(selected.Target, outputPath, options, token);

            hud = new RecordingHudWindow(selected.Bounds,
                _settings.VideoCaptureSystemAudio, _settings.VideoCaptureMicrophone);
            if (!hud.IsExcludedFromCapture)
            {
                throw new InvalidOperationException(
                    "Windows could not keep the recording controls out of the video.");
            }
            var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            hud.StopRequested += () => decision.TrySetResult(true);
            hud.CancelRequested += () => decision.TrySetResult(false);
            bool paused = false;
            hud.PauseRequested += async () =>
            {
                try
                {
                    if (paused) { await session.ResumeAsync(); }
                    else { await session.PauseAsync(); }
                    paused = !paused;
                    hud.SetPaused(paused);
                }
                catch (Exception)
                {
                    decision.TrySetResult(false);
                }
            };
            clock = App.DispatcherQueue.CreateTimer();
            clock.Interval = TimeSpan.FromMilliseconds(100);
            clock.IsRepeating = true;
            clock.Tick += (_, _) => hud.SetElapsed(session.Elapsed);
            clock.Start();
            hud.Activate();

            using CancellationTokenRegistration registration = token.Register(() => decision.TrySetResult(false));
            Task finished = await Task.WhenAny(decision.Task, session.Completion);
            VideoRecordingResult result;
            if (finished == session.Completion)
            {
                result = await session.Completion;
            }
            else if (await decision.Task)
            {
                hud.SetFinishing();
                result = await session.StopAsync();
            }
            else
            {
                hud.SetFinishing();
                await session.CancelAsync();
                return;
            }
            finalized = true;
            clock.Stop();
            hud.Close();
            hud = null;

            VideoProject project = VideoProject.CreateRecording(
                selected.Title + $" · {result.StartedAt:MMM d, HH:mm}",
                result.Duration,
                result.PixelWidth,
                result.PixelHeight,
                RelativeMediaPath(projectDirectory, result.VideoPath),
                result.SystemAudioPath is { } systemPath
                    ? RelativeMediaPath(projectDirectory, systemPath) : null,
                result.MicrophoneAudioPath is { } micPath
                    ? RelativeMediaPath(projectDirectory, micPath) : null,
                projectId,
                _settings.VideoFrameRate);
            // Once capture has finalized, persist its metadata even if shutdown was requested.
            try
            {
                await VideoStore.SaveAsync(project, CancellationToken.None);
            }
            catch (Exception error)
            {
                throw new IOException(
                    $"The recording remains at {result.VideoPath}, but it could not be added to the Library. " +
                    error.Message, error);
            }
            try
            {
                await ShowVideoProjectAsync(project);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    "The recording is saved in the Videos library, but the editor could not open it. " +
                    error.Message, error);
            }
        }
        finally
        {
            clock?.Stop();
            hud?.Close();
            if (session is not null && !finalized)
            {
                try { await session.CancelAsync(); }
                catch (Exception) { /* Preserve the original failure. */ }
            }
            if (!finalized && projectDirectory is not null)
            {
                try { Directory.Delete(projectDirectory, recursive: false); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _activeCapture?.Dispose();
            _activeCapture = null;
            _captureGate.Release();
            if (!finalized && wasVisible && !_quitting && !_lifetime.IsCancellationRequested)
            {
                Show();
            }
        }
    }

    private async Task<VideoSelection?> SelectVideoTargetAsync(
        CaptureKind kind, NativeMethods.Point pointer, CancellationToken token)
    {
        if (kind == CaptureKind.FullScreen)
        {
            nint monitor = NativeMethods.MonitorFromPoint(pointer, NativeMethods.MonitorDefaultToNearest);
            MonitorTarget display = NativeMethods.Monitors().FirstOrDefault(item => item.Handle == monitor)
                ?? throw new InvalidOperationException("The selected display is no longer available.");
            using CanvasBitmap preview = await _capture.MonitorAsync(monitor, token);
            using var picker = new SelectionWindow(preview, display.Bounds, null, previewDisplay: true);
            return await picker.PickAsync(token) is null ? null :
                new VideoSelection(VideoCaptureTarget.ForMonitor(monitor), display.Bounds, "Full-screen video");
        }

        (CanvasBitmap desktop, NativeMethods.Rect bounds) = await _capture.DesktopAsync(token);
        using (desktop)
        {
            IReadOnlyList<WindowTarget>? windows = kind == CaptureKind.Window
                ? NativeMethods.Windows(App.WindowHandle) : null;
            using var picker = new SelectionWindow(desktop, bounds, windows);
            CaptureSelection? selection = await picker.PickAsync(token);
            if (selection?.Window is { } window)
            {
                return new VideoSelection(
                    VideoCaptureTarget.ForWindow(window.Handle), window.Bounds, window.Title);
            }
            if (selection?.Region is { } region)
            {
                int left = bounds.Left + Math.Clamp((int)Math.Floor(region.X), 0, bounds.Width);
                int top = bounds.Top + Math.Clamp((int)Math.Floor(region.Y), 0, bounds.Height);
                int right = bounds.Left + Math.Clamp((int)Math.Ceiling(region.Right), 0, bounds.Width);
                int bottom = bounds.Top + Math.Clamp((int)Math.Ceiling(region.Bottom), 0, bounds.Height);
                if (right - left < 2 || bottom - top < 2)
                {
                    throw new InvalidOperationException("Choose a video region at least two pixels wide and high.");
                }
                var rectangle = new NativeMethods.Rect
                {
                    Left = left,
                    Top = top,
                    Right = right,
                    Bottom = bottom,
                };
                return new VideoSelection(VideoCaptureTarget.ForRegion(rectangle), rectangle, "Region video");
            }
        }
        return null;
    }

    private static string RelativeMediaPath(string directory, string path)
    {
        string relative = Path.GetRelativePath(directory, path);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A recording output escaped its video project directory.");
        }
        return relative;
    }

    private async Task OpenVideoProjectAsync(Guid id)
    {
        if (_activeCapture is not null || !await _captureGate.WaitAsync(0, _lifetime.Token))
        {
            return;
        }
        try
        {
            _page.CommitText();
            _saveTimer.Stop();
            await SaveCurrentAsync();
            VideoProject? project = await VideoStore.OpenAsync(id, _lifetime.Token);
            if (project is null)
            {
                _page.ShowMessage("Video unavailable", "This video is no longer in your Library.");
                return;
            }
            if (_window.VideoPage is { } openVideo) { await openVideo.SaveAsync(); }
            await ShowVideoProjectAsync(project);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private async Task ShowVideoProjectAsync(VideoProject project)
    {
        IReadOnlyList<ShnappSummary> gallery = await ListReadySummariesAsync();
        var page = new VideoEditorPage();
        page.Configure(VideoStore, project, ReadSavedImageForLayerAsync,
            id => File.Exists(Library.GetGalleryPreviewPath(id))
                ? Library.GetGalleryPreviewPath(id)
                : Library.GetPreviewPath(id), gallery);
        page.BackRequested += (_, _) => OpenLibrary();
        page.LibraryRequested += (_, _) => OpenLibrary();
        _window.ShowVideoEditor(page);
        _window.ApplyTheme(_settings.Theme switch
        {
            "Light" => Microsoft.UI.Xaml.ElementTheme.Light,
            "Dark" => Microsoft.UI.Xaml.ElementTheme.Dark,
            _ => Microsoft.UI.Xaml.ElementTheme.Default,
        });
        Show();
    }

    private sealed record VideoSelection(
        VideoCaptureTarget Target, NativeMethods.Rect Bounds, string Title);
}
