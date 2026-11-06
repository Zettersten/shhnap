using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Shnapp.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Shnapp.App.Editor;

/// <summary>Observable presentation state for Shnapp's document-first window.</summary>
public sealed partial class EditorViewModel : ObservableObject
{
    /// <summary>Gets or sets the title of the current surface.</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = "Capture first. Think less.";

    /// <summary>Gets or sets the quiet status message.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "Ready to shnapp";

    /// <summary>Gets or sets the physical-pixel dimensions.</summary>
    [ObservableProperty]
    public partial string Dimensions { get; set; } = string.Empty;

    /// <summary>Gets or sets whether an editable shnapp is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorVisibility))]
    [NotifyPropertyChangedFor(nameof(LibraryVisibility))]
    [NotifyPropertyChangedFor(nameof(IsLibrarySelected))]
    public partial bool HasDocument { get; set; }

    /// <summary>Gets or sets whether undo is available.</summary>
    [ObservableProperty]
    public partial bool CanUndo { get; set; }

    /// <summary>Gets or sets whether redo is available.</summary>
    [ObservableProperty]
    public partial bool CanRedo { get; set; }

    /// <summary>Gets the visibility of the annotation surface.</summary>
    public Visibility EditorVisibility => HasDocument ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Gets the visibility of the library surface.</summary>
    public Visibility LibraryVisibility => HasDocument ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Highlights Library while the library surface is visible.</summary>
    public bool? IsLibrarySelected => !HasDocument;

    /// <summary>Gets the virtualized collection of visible library entries.</summary>
    public ObservableCollection<LibraryEntry> Library { get; } = [];
}

/// <summary>A gallery entry backed by a saved screenshot or recording.</summary>
public sealed partial class LibraryEntry : ObservableObject
{
    private static readonly SemaphoreSlim PreviewDecodeGate = new(4, 4);
    private readonly string? _photoPreviewPath;
    private readonly int _previewPixelWidth;
    private BitmapImage _preview = new();
    private CancellationTokenSource? _previewCancellation;
    private bool _previewLoaded;
    private bool _previewFailed;

    /// <summary>Enables dragging a saved image into the editor outside selection mode.</summary>
    [ObservableProperty]
    public partial bool CanDrag { get; set; } = true;

    /// <summary>Tracks selection across the grid, list, filtering, and layout changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridSelectionVisibility))]
    public partial bool IsSelected { get; set; }

    /// <summary>Reveals the grid checkbox while the pointer is over its thumbnail.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridSelectionVisibility))]
    public partial bool IsGridHovered { get; set; }

    /// <summary>Exposes selection to keyboard users when a grid card has focus.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridSelectionVisibility))]
    public partial bool IsGridKeyboardFocused { get; set; }

    public Visibility GridSelectionVisibility => !IsVideo &&
        (IsGridHovered || IsGridKeyboardFocused || IsSelected)
        ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Identifies recordings in the shared photo and video gallery.</summary>
    public bool IsVideo { get; }

    /// <summary>Shows a play marker and a thumbnail fallback for recordings.</summary>
    public Visibility VideoBadgeVisibility => IsVideo ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Selection and photo-only actions remain limited to editable images.</summary>
    public Visibility PhotoSelectionVisibility => IsVideo ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The recording duration shown on a video card.</summary>
    public string VideoDurationText { get; } = string.Empty;

    /// <summary>Searchable text shared by photos and videos.</summary>
    public string SearchTerms { get; }

    /// <summary>Absolute recording path used to request a bounded shell thumbnail.</summary>
    public string? VideoSourcePath { get; }

    /// <summary>Hides the trailing divider of the current filtered and sorted list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeparatorVisibility))]
    public partial bool IsLastVisible { get; set; }

    public Visibility SeparatorVisibility => IsLastVisible ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Stacks list metadata when aligned columns would hide the title.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListRowHeight))]
    [NotifyPropertyChangedFor(nameof(ListThumbnailWidth))]
    [NotifyPropertyChangedFor(nameof(ListDateWidth))]
    [NotifyPropertyChangedFor(nameof(ListDimensionsWidth))]
    [NotifyPropertyChangedFor(nameof(ListSizeWidth))]
    [NotifyPropertyChangedFor(nameof(WideListVisibility))]
    [NotifyPropertyChangedFor(nameof(CompactListVisibility))]
    public partial bool CompactList { get; set; }

    public double ListRowHeight => CompactList ? 68 : 64;
    public GridLength ListThumbnailWidth => new(48);
    public GridLength ListDateWidth => new(CompactList ? 0 : 152);
    public GridLength ListDimensionsWidth => new(CompactList ? 0 : 124);
    public GridLength ListSizeWidth => new(CompactList ? 0 : 80);
    public Visibility WideListVisibility => CompactList ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CompactListVisibility => CompactList ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Gets the stable document identifier.</summary>
    public Guid Id { get; }

    /// <summary>Gets the title shown beneath the thumbnail.</summary>
    public string Title { get; }

    /// <summary>Names each selection checkbox for assistive technology.</summary>
    public string SelectionLabel => $"Select {Title}";

    /// <summary>Gets the local date of the last save.</summary>
    public string DateText { get; }

    /// <summary>Gets compact details for the right edge of a thumbnail card.</summary>
    public string GridMetadataText => IsVideo
        ? CardWidth < 260 ? VideoDurationText : $"{GridDateText} · {VideoDurationText}"
        : CardWidth switch
        {
            < 180 => FileSizeText,
            < 260 => $"{GridDateText} · {FileSizeText}",
            _ => $"{GridDateText} · {GridDimensionsText} · {FileSizeText}",
        };

    private string GridDateText { get; }
    private string GridDimensionsText { get; }

    /// <summary>Gets the visible canvas dimensions in pixels.</summary>
    public string DimensionsText { get; }

    /// <summary>Gets the size of the saved PNG, or a placeholder when it is missing.</summary>
    public string FileSizeText { get; }

    /// <summary>Gets the exact saved PNG size for sorting.</summary>
    public long? FileSizeBytes { get; }

    /// <summary>Gets the visible pixel count used to sort dimensions.</summary>
    public double PixelArea { get; }

    /// <summary>Gets the last save date used to sort entries.</summary>
    public DateTimeOffset ModifiedAt { get; }

    /// <summary>Gets all metadata for the card tooltip and accessibility tree.</summary>
    public string MetadataDescription { get; }

    /// <summary>Gets date and size together for the compact list layout.</summary>
    public string CompactMetadataSummary { get; }

    /// <summary>Gets the decoded-size-limited local preview.</summary>
    public BitmapImage Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    /// <summary>Gets the width of this entry in the selected grid layout.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridMetadataText))]
    public partial double CardWidth { get; set; }

    /// <summary>Gets the width reserved for this card in the wrapping grid.</summary>
    [ObservableProperty]
    public partial double CardTileWidth { get; set; }

    /// <summary>Gets the thumbnail height in the selected grid layout.</summary>
    [ObservableProperty]
    public partial double ThumbnailHeight { get; set; }

    /// <summary>Gets the capture type shown in list view.</summary>
    public string CaptureType { get; }

    /// <summary>Creates a library presentation entry without loading the original image.</summary>
    /// <param name="document">The validated local display metadata.</param>
    /// <param name="previewPath">The absolute PNG preview path.</param>
    public LibraryEntry(ShnappSummary document, string previewPath, double cardWidth = 224,
        double thumbnailHeight = 144, int previewPixelWidth = 384, long? fileSizeBytes = null)
    {
        Id = document.Id;
        Title = document.Title;
        CaptureType = document.CaptureKind switch
        {
            CaptureKind.Window => "Window",
            CaptureKind.FullScreen => "Full screen",
            CaptureKind.Camera => "Camera photo",
            _ => "Region",
        };
        ModifiedAt = document.ModifiedAt;
        DateTimeOffset localSave = document.ModifiedAt.ToLocalTime();
        string searchType = document.CaptureKind switch
        {
            CaptureKind.Window => "window",
            CaptureKind.FullScreen => "full screen display",
            CaptureKind.Camera => "camera photo webcam",
            _ => "region",
        };
        SearchTerms = $"{Title} image photo screenshot {searchType} " +
            $"{localSave:MMM d yyyy HH:mm} {localSave:yyyy-MM-dd} " +
            $"{document.Viewport.Width:0}x{document.Viewport.Height:0} " +
            $"{document.Viewport.Width:0}×{document.Viewport.Height:0}";
        DateText = localSave.ToString("MMM d, yyyy · h:mm tt");
        GridDateText = localSave.ToString("MMM d");
        DimensionsText = $"{document.Viewport.Width:0} × {document.Viewport.Height:0} px";
        GridDimensionsText = $"{document.Viewport.Width:0}×{document.Viewport.Height:0}";
        PixelArea = document.Viewport.Width * document.Viewport.Height;
        FileSizeBytes = fileSizeBytes;
        FileSizeText = fileSizeBytes is { } bytes ? ShnappMetadata.FormatFileSize(bytes) : "—";
        CompactMetadataSummary = $"{DateText}  ·  {DimensionsText}  ·  {FileSizeText}";
        MetadataDescription = $"{Title}, {DateText}, {DimensionsText}, " +
            (fileSizeBytes is null ? "file size unavailable" : FileSizeText);
        CardWidth = cardWidth;
        CardTileWidth = cardWidth;
        ThumbnailHeight = thumbnailHeight;
        _previewPixelWidth = previewPixelWidth;
        _photoPreviewPath = previewPath;
        Preview = NewPreview();
    }

    /// <summary>Creates a video card in the same virtualized gallery as photos.</summary>
    public LibraryEntry(VideoProjectSummary project, string sourceMp4Path,
        double cardWidth = 224, double thumbnailHeight = 144,
        int previewPixelWidth = 384, long? fileSizeBytes = null)
    {
        IsVideo = true;
        CanDrag = false;
        Id = project.Id;
        Title = project.Title;
        CaptureType = "Video";
        ModifiedAt = project.CreatedAt;
        VideoSourcePath = sourceMp4Path;
        VideoDurationText = project.Duration.TotalHours >= 1
            ? $"{(int)project.Duration.TotalHours:D2}:{project.Duration.Minutes:D2}:{project.Duration.Seconds:D2}"
            : $"{(int)project.Duration.TotalMinutes:D2}:{project.Duration.Seconds:D2}";
        DateTimeOffset localSave = project.CreatedAt.ToLocalTime();
        DateText = localSave.ToString("MMM d, yyyy · h:mm tt");
        GridDateText = localSave.ToString("MMM d");
        DimensionsText = $"{project.Width} × {project.Height} px";
        GridDimensionsText = $"{project.Width}×{project.Height}";
        PixelArea = (double)project.Width * project.Height;
        FileSizeBytes = fileSizeBytes;
        FileSizeText = fileSizeBytes is { } bytes ? ShnappMetadata.FormatFileSize(bytes) : "—";
        CompactMetadataSummary = $"{DateText}  ·  {DimensionsText}  ·  {VideoDurationText}";
        MetadataDescription = $"{Title}, video, {VideoDurationText}, recorded " +
            $"{localSave:MMMM d, yyyy}, {project.Width} by {project.Height} pixels";
        SearchTerms = $"{Title} video recording {localSave:MMM d yyyy HH:mm} " +
            $"{localSave:yyyy-MM-dd} {project.Width}x{project.Height} " +
            $"{project.Width}×{project.Height} {VideoDurationText}";
        CardWidth = cardWidth;
        CardTileWidth = cardWidth;
        ThumbnailHeight = thumbnailHeight;
        _previewPixelWidth = Math.Clamp(previewPixelWidth, 64, 512);
        Preview = NewPreview();
    }

    /// <summary>Loads only realized thumbnails, with bounded decode concurrency.</summary>
    public void EnsurePreview(CancellationToken cancellationToken = default)
    {
        if (_previewLoaded || _previewFailed || _previewCancellation is not null ||
            (IsVideo ? string.IsNullOrWhiteSpace(VideoSourcePath) :
                string.IsNullOrWhiteSpace(_photoPreviewPath)))
            return;
        CancellationTokenSource cancellation = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        _previewCancellation = cancellation;
        _ = LoadPreviewAsync(cancellation);
    }

    /// <summary>Drops decoded pixels when a gallery card leaves the viewport.</summary>
    public void ReleasePreview()
    {
        bool hadPreview = _previewLoaded || _previewCancellation is not null;
        _previewCancellation?.Cancel();
        _previewCancellation = null;
        if (!hadPreview) return;
        _previewLoaded = false;
        Preview = NewPreview();
    }

    private BitmapImage NewPreview() => new() { DecodePixelWidth = _previewPixelWidth };

    private async Task LoadPreviewAsync(CancellationTokenSource cancellation)
    {
        bool entered = false;
        CancellationToken token = cancellation.Token;
        try
        {
            await PreviewDecodeGate.WaitAsync(token);
            entered = true;
            token.ThrowIfCancellationRequested();
            BitmapImage preview = Preview;
            if (IsVideo)
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(VideoSourcePath!);
                token.ThrowIfCancellationRequested();
                using StorageItemThumbnail thumbnail = await file.GetThumbnailAsync(
                    ThumbnailMode.VideosView, (uint)_previewPixelWidth,
                    ThumbnailOptions.UseCurrentScale);
                token.ThrowIfCancellationRequested();
                if (thumbnail.Size == 0) { _previewFailed = true; return; }
                await preview.SetSourceAsync(thumbnail);
            }
            else
            {
                // Stable preview paths may change after an edit. Read current bytes.
                await using FileStream file = new(_photoPreviewPath!, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using IRandomAccessStream stream = file.AsRandomAccessStream();
                token.ThrowIfCancellationRequested();
                await preview.SetSourceAsync(stream);
            }
            token.ThrowIfCancellationRequested();
            _previewLoaded = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { _previewFailed = !token.IsCancellationRequested; }
        catch (UnauthorizedAccessException) { _previewFailed = !token.IsCancellationRequested; }
        catch (COMException) { _previewFailed = !token.IsCancellationRequested; }
        finally
        {
            if (entered) PreviewDecodeGate.Release();
            if (ReferenceEquals(_previewCancellation, cancellation))
                _previewCancellation = null;
            cancellation.Dispose();
        }
    }
}

internal enum EditorTool
{
    Select,
    Text,
    Step,
    Callout,
    Magnify,
    Arrow,
    Line,
    Rectangle,
    Square,
    Ellipse,
    Circle,
    Redaction,
    Crop,
    Shape,
}
