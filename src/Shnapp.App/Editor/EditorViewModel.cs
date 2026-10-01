using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Shnapp.Core;

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

    /// <summary>Gets the virtualized collection of visible library entries.</summary>
    public ObservableCollection<LibraryEntry> Library { get; } = [];
}

/// <summary>A display-only entry backed by an editable local document.</summary>
public sealed partial class LibraryEntry : ObservableObject
{
    /// <summary>Enables dragging a saved image into the editor outside selection mode.</summary>
    [ObservableProperty]
    public partial bool CanDrag { get; set; } = true;

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

    public double ListRowHeight => CompactList ? 94 : 74;
    public GridLength ListThumbnailWidth => new(CompactList ? 84 : 104);
    public GridLength ListDateWidth => new(CompactList ? 0 : 168);
    public GridLength ListDimensionsWidth => new(CompactList ? 0 : 114);
    public GridLength ListSizeWidth => new(CompactList ? 0 : 78);
    public Visibility WideListVisibility => CompactList ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CompactListVisibility => CompactList ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Gets the stable document identifier.</summary>
    public Guid Id { get; }

    /// <summary>Gets the title shown beneath the thumbnail.</summary>
    public string Title { get; }

    /// <summary>Gets the local capture date.</summary>
    public string DateText { get; }

    /// <summary>Gets the concise capture date shown on thumbnail cards.</summary>
    public string GridDateText { get; }

    /// <summary>Gets the visible canvas dimensions in pixels.</summary>
    public string DimensionsText { get; }

    /// <summary>Gets the size of the saved PNG, or a placeholder when it is missing.</summary>
    public string FileSizeText { get; }

    /// <summary>Gets the exact saved PNG size for sorting.</summary>
    public long? FileSizeBytes { get; }

    /// <summary>Gets the visible pixel count used to sort dimensions.</summary>
    public double PixelArea { get; }

    /// <summary>Gets the source date used to sort entries.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Gets all metadata for the card tooltip and accessibility tree.</summary>
    public string MetadataDescription { get; }

    /// <summary>Gets date and size together for the compact list layout.</summary>
    public string CompactMetadataSummary { get; }

    /// <summary>Gets the decoded-size-limited local preview.</summary>
    public BitmapImage Preview { get; }

    /// <summary>Gets the width of this entry in the selected grid layout.</summary>
    public double CardWidth { get; }

    /// <summary>Gets the thumbnail height in the selected grid layout.</summary>
    public double ThumbnailHeight { get; }

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
            _ => "Region",
        };
        CreatedAt = document.CreatedAt;
        DateTimeOffset localCapture = document.CreatedAt.ToLocalTime();
        DateText = localCapture.ToString("MMM d, yyyy · h:mm tt");
        GridDateText = localCapture.ToString("MMM d, yyyy");
        DimensionsText = $"{document.Viewport.Width:0} × {document.Viewport.Height:0} px";
        PixelArea = document.Viewport.Width * document.Viewport.Height;
        FileSizeBytes = fileSizeBytes;
        FileSizeText = fileSizeBytes is { } bytes ? ShnappMetadata.FormatFileSize(bytes) : "—";
        CompactMetadataSummary = $"{DateText}  ·  {FileSizeText}";
        MetadataDescription = $"{Title}, {DateText}, {DimensionsText}, " +
            (fileSizeBytes is null ? "file size unavailable" : FileSizeText);
        CardWidth = cardWidth;
        ThumbnailHeight = thumbnailHeight;
        Preview = new BitmapImage { DecodePixelWidth = previewPixelWidth };
        if (File.Exists(previewPath))
        {
            Preview.UriSource = new Uri(previewPath, UriKind.Absolute);
        }
    }
}

internal enum EditorTool
{
    Select,
    Text,
    Step,
    Arrow,
    Line,
    Rectangle,
    Square,
    Ellipse,
    Circle,
    Redaction,
    Crop,
}
