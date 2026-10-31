using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Shnapp.Core;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.Playback;
using Windows.Storage.Pickers;

namespace Shnapp.App.Video;

/// <summary>A non-destructive editor for one recorded video project.</summary>
public sealed partial class VideoEditorPage : Page
{
    private VideoProjectStore? _store;
    private VideoProjectEditor? _editor;
    private Func<Guid, Task<byte[]>>? _readGalleryImage;
    private Func<Guid, string>? _getGalleryPreviewPath;
    private IReadOnlyList<ShnappSummary> _galleryItems = [];
    private readonly MediaPlayer _player = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _refreshDebounceCancellation;
    private CancellationTokenSource? _exportCancellation;
    private long _previewRevision;
    private TimeSpan _playhead;
    private TimeSpan? _markIn;
    private TimeSpan? _markOut;
    private bool _updatingTransport;
    private int _positionUpdateQueued;
    private bool _isExporting;
    private bool _closed;
    private bool _layoutReady;

    public event EventHandler? BackRequested;
    public event EventHandler? LibraryRequested;
    public event EventHandler? StateChanged;

    public bool CanUndo => _editor?.CanUndo == true;
    public bool CanRedo => _editor?.CanRedo == true;
    public string Title => _editor?.Current.Title ?? "Video";

    public VideoEditorPage()
    {
        InitializeComponent();
        Loaded += Page_Loaded;
        VideoPlayer.SetMediaPlayer(_player);
        _player.PlaybackSession.PositionChanged += PlaybackSession_PositionChanged;
        _player.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
    }

    public void Configure(VideoProjectStore store, VideoProject project,
        Func<Guid, Task<byte[]>> readGalleryImage,
        Func<Guid, string> getGalleryPreviewPath,
        IReadOnlyList<ShnappSummary> galleryItems)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(readGalleryImage);
        ArgumentNullException.ThrowIfNull(getGalleryPreviewPath);
        ArgumentNullException.ThrowIfNull(galleryItems);

        if (_editor is not null)
        {
            _editor.Changed -= Editor_Changed;
        }

        _store = store;
        _editor = new VideoProjectEditor(project);
        _editor.Changed += Editor_Changed;
        _readGalleryImage = readGalleryImage;
        _getGalleryPreviewPath = getGalleryPreviewPath;
        _galleryItems = galleryItems;
        _closed = false;
        _playhead = TimeSpan.Zero;
        _markIn = null;
        _markOut = null;
        ProjectTitleText.Text = project.Title;
        MetadataText.Text = $"{project.Width} × {project.Height}  ·  {FormatTime(project.Duration)}  ·  MP4";
        DurationText.Text = FormatTime(project.Duration);
        BuildGallery();
        RefreshTimeline();
        UpdateTransport();
        UpdateEditActionState();
        UpdateState();
        ++_previewRevision;
        if (_layoutReady) _ = RefreshPreviewAsync(_previewRevision);
    }

    public void Undo() => _editor?.Undo();
    public void Redo() => _editor?.Redo();

    public void ShowStatus(string message) => StatusText.Text = message;

    /// <summary>Flushes the latest immutable project snapshot before navigating away.</summary>
    public async Task SaveAsync()
    {
        VideoProjectEditor? editor = _editor;
        VideoProjectStore? store = _store;
        if (editor is null || store is null) return;

        await _saveGate.WaitAsync();
        try
        {
            await store.SaveAsync(editor.Current);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task ExportAsync()
    {
        if (_editor is null || _store is null || _isExporting) return;

        var picker = new FileSavePicker
        {
            SuggestedFileName = SanitizeFileName(_editor.Current.Title),
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
        };
        picker.FileTypeChoices.Add("MP4 video", new List<string> { ".mp4" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        global::Windows.Storage.StorageFile? destination = await picker.PickSaveFileAsync();
        if (destination is null || _closed) return;

        _isExporting = true;
        var exportCancellation = new CancellationTokenSource();
        _exportCancellation = exportCancellation;
        UpdateState();
        try
        {
            VideoProject snapshot = _editor.Current;
            StatusText.Text = "Exporting 0%";
            var progress = new Progress<double>(value =>
            {
                if (!_closed) StatusText.Text = $"Exporting {Math.Clamp(value, 0, 100):F0}%";
            });
            await VideoCompositionService.ExportAsync(snapshot, _store,
                destination.Path, progress, exportCancellation.Token);
            if (!_closed) StatusText.Text = $"Exported {destination.Name}";
        }
        catch (OperationCanceledException)
        {
            if (!_closed) StatusText.Text = "Export canceled";
        }
        catch (Exception exception)
        {
            if (!_closed) StatusText.Text = $"Export failed: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_exportCancellation, exportCancellation))
                _exportCancellation = null;
            exportCancellation.Dispose();
            _isExporting = false;
            if (!_closed) UpdateState();
        }
    }

    /// <summary>Stops playback and releases native preview resources.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        ++_previewRevision;
        _refreshDebounceCancellation?.Cancel();
        _refreshDebounceCancellation?.Dispose();
        _refreshDebounceCancellation = null;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        _exportCancellation?.Cancel();
        if (_editor is not null) _editor.Changed -= Editor_Changed;
        _player.PlaybackSession.PositionChanged -= PlaybackSession_PositionChanged;
        _player.PlaybackSession.PlaybackStateChanged -= PlaybackSession_PlaybackStateChanged;
        try { _player.Pause(); _player.Source = null; }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or ObjectDisposedException) { }
        try { VideoPlayer.SetMediaPlayer(null!); }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or ObjectDisposedException) { }
        try { _player.Dispose(); }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or ObjectDisposedException) { }
    }

    private void Editor_Changed(object? sender, EventArgs args)
    {
        if (_closed || _editor is null) return;
        VideoProject project = _editor.Current;
        ProjectTitleText.Text = project.Title;
        MetadataText.Text = $"{project.Width} × {project.Height}  ·  {FormatTime(project.Duration)}  ·  MP4";
        DurationText.Text = FormatTime(project.Duration);
        _playhead = Min(_playhead, project.Duration);
        RefreshTimeline();
        RefreshInspector();
        UpdateTransport();
        UpdateEditActionState();
        UpdateState();
        long revision = ++_previewRevision;
        _previewCancellation?.Cancel();
        _refreshDebounceCancellation?.Cancel();
        _refreshDebounceCancellation?.Dispose();
        _refreshDebounceCancellation = new CancellationTokenSource();
        _ = SaveAndPreviewAsync(revision, _refreshDebounceCancellation.Token);
    }

    private async Task SaveAndPreviewAsync(long revision, CancellationToken token)
    {
        try
        {
            // Multiple inspector edits and undo/redo steps share one preview rebuild.
            await Task.Delay(160, token);
            if (_closed || revision != _previewRevision) return;
            await SaveAsync();
            if (!_closed && revision == _previewRevision)
                await RefreshPreviewAsync(revision);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_closed && revision == _previewRevision)
                StatusText.Text = $"Save failed: {exception.Message}";
        }
    }

    private async Task RefreshPreviewAsync(long revision)
    {
        if (_closed || revision != _previewRevision || _editor is null || _store is null) return;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        CancellationToken token = _previewCancellation.Token;
        TimeSpan position = _playhead;
        bool wasPlaying = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        PreviewProgress.IsActive = true;
        try
        {
            VideoProject project = _editor.Current;
            (int previewWidth, int previewHeight) = GetPreviewSize(project);
            MediaComposition composition = await VideoCompositionService.BuildAsync(
                project, _store, token, (previewWidth, previewHeight));
            token.ThrowIfCancellationRequested();
            if (revision != _previewRevision || _closed) return;
            var stream = composition.GeneratePreviewMediaStreamSource(previewWidth, previewHeight);
            _player.Source = MediaSource.CreateFromMediaStreamSource(stream);
            _player.PlaybackSession.Position = position;
            if (wasPlaying) _player.Play();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (revision == _previewRevision && !_closed)
                StatusText.Text = $"Preview unavailable: {exception.Message}";
        }
        finally
        {
            if (revision == _previewRevision && !_closed) PreviewProgress.IsActive = false;
        }
    }

    private (int Width, int Height) GetPreviewSize(VideoProject project)
    {
        double sourceWidth = project.Width;
        double sourceHeight = project.Height;
        double viewWidth = VideoPlayer.ActualWidth;
        double viewHeight = VideoPlayer.ActualHeight;
        double scale = viewWidth > 0 && viewHeight > 0
            ? Math.Min(viewWidth / sourceWidth, viewHeight / sourceHeight) *
              Math.Clamp(VideoPlayer.XamlRoot?.RasterizationScale ?? 1, 1, 2)
            : 1280 / Math.Max(sourceWidth, sourceHeight);
        scale = Math.Min(scale, Math.Min(1, 1920 / Math.Max(sourceWidth, sourceHeight)));
        scale = Math.Min(scale, Math.Sqrt(1920d * 1080d / (sourceWidth * sourceHeight)));
        return (Math.Max(2, (int)Math.Floor(sourceWidth * scale / 2) * 2),
            Math.Max(2, (int)Math.Floor(sourceHeight * scale / 2) * 2));
    }

    private void PlaybackSession_PositionChanged(MediaPlaybackSession sender, object args)
    {
        if (_closed || Interlocked.Exchange(ref _positionUpdateQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _positionUpdateQueued, 0);
            if (_closed || _editor is null) return;
            _playhead = Min(sender.Position, _editor.Current.Duration);
            UpdateTransport();
            DrawPlayhead();
            UpdateEditActionState();
        })) Interlocked.Exchange(ref _positionUpdateQueued, 0);
    }

    private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        if (_closed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed) PlayPauseIcon.Glyph = sender.PlaybackState == MediaPlaybackState.Playing
                ? "\uE769" : "\uE768";
        });
    }

    private void PlayPause_Click(object sender, RoutedEventArgs args)
    {
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            _player.Pause();
        else
            _player.Play();
    }

    private void TransportSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        if (_updatingTransport || _editor is null) return;
        Seek(TimeSpan.FromSeconds(Math.Clamp(args.NewValue, 0,
            _editor.Current.Duration.TotalSeconds)));
    }

    private void Seek(TimeSpan time)
    {
        if (_editor is null) return;
        _playhead = Min(time, _editor.Current.Duration);
        _player.PlaybackSession.Position = _playhead;
        UpdateTransport();
        DrawPlayhead();
        UpdateEditActionState();
    }

    private void UpdateTransport()
    {
        if (_editor is null) return;
        _updatingTransport = true;
        double maximum = Math.Max(0.01, _editor.Current.Duration.TotalSeconds);
        if (TransportSlider.Maximum != maximum) TransportSlider.Maximum = maximum;
        double value = Math.Clamp(_playhead.TotalSeconds, 0, maximum);
        if (Math.Abs(TransportSlider.Value - value) >= 0.001) TransportSlider.Value = value;
        string time = FormatTime(_playhead);
        if (CurrentTimeText.Text != time) CurrentTimeText.Text = time;
        _updatingTransport = false;
    }

    private void UpdateState()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");

    private static string FormatPreciseTime(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"hh\:mm\:ss\.ff") : value.ToString(@"mm\:ss\.ff");

    private static string SanitizeFileName(string title)
    {
        string safe = string.Concat(title.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return string.IsNullOrWhiteSpace(safe) ? "Shnapp video" : safe;
    }

    private void Page_Loaded(object sender, RoutedEventArgs args)
    {
        _layoutReady = true;
        if (!_closed && _editor is not null)
            _ = RefreshPreviewAsync(_previewRevision);
    }
    private void Page_Unloaded(object sender, RoutedEventArgs args) { _layoutReady = false; }
    private void Export_Click(object sender, RoutedEventArgs args) => _ = ExportAsync();
    private void Undo_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { args.Handled = true; Undo(); }
    private void Redo_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { args.Handled = true; Redo(); }
    private void Export_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { args.Handled = true; _ = ExportAsync(); }
    private void Back_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { args.Handled = true; BackRequested?.Invoke(this, EventArgs.Empty); }
    private void Library_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { args.Handled = true; LibraryRequested?.Invoke(this, EventArgs.Empty); }
}
