using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Shnapp.App.Editor;
using Shnapp.Core;
using Windows.System;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private static readonly (double CardWidth, double ThumbnailHeight)[] LibrarySizes =
    [
        (152, 92),
        (224, 144),
        (304, 194),
        (384, 246),
    ];

    private readonly Dictionary<Guid, LibraryEntry> _libraryEntries = [];
    private int _librarySizeIndex = 2;
    private bool _libraryListMode;
    private bool _librarySelectionMode;
    private bool _restoringLibrarySelection;
    private bool _libraryCompactList;
    private readonly HashSet<Guid> _selectedLibraryIds = [];
    private LibraryEntry? _focusedGridEntry;
    private bool _focusSearchWhenLibraryOpens;
    private LibrarySortField _librarySortField = LibrarySortField.Date;
    private bool _librarySortDescending = true;

    private enum LibrarySortField
    {
        Date,
        Name,
        FileSize,
        Dimensions,
    }

    internal void UpdateReadyLibrary(IReadOnlyList<ShnappSummary> documents)
    {
        _library = documents;
        if (_editor is null) { RebuildLibraryEntries(); }
    }

    internal void SetLibraryRecoveryState(bool restoring)
    {
        LibraryEmptyDescription.Text = restoring
            ? "Restoring saved shnapps. They will appear here as they are ready."
            : "The fast, beautiful way to capture your screen and turn ideas into reality.";
    }

    private void RebuildLibraryEntries()
    {
        if (_controller is null)
        {
            return;
        }

        (double cardWidth, double thumbnailHeight) = LibrarySizes[_librarySizeIndex];
        if (_focusedGridEntry is not null)
        {
            _focusedGridEntry.IsGridKeyboardFocused = false;
            _focusedGridEntry = null;
        }
        _libraryEntries.Clear();
        foreach (ShnappSummary document in _library)
        {
            string fitted = _controller.Library.GetFittedPreviewPath(document.Id);
            string export = _controller.Library.GetExportPath(document.Id);
            string preview = File.Exists(fitted) ? fitted : export;
            if (!File.Exists(preview))
            {
                preview = _controller.Library.GetPreviewPath(document.Id);
            }
            long? fileSizeBytes = GetSavedPngSize(document.Id);
            _libraryEntries[document.Id] = new LibraryEntry(document,
                preview, cardWidth, thumbnailHeight,
                _libraryListMode || _librarySizeIndex == 0 ? 192 : 384, fileSizeBytes)
            {
                CanDrag = !_librarySelectionMode,
                CompactList = _libraryCompactList,
                IsSelected = _selectedLibraryIds.Contains(document.Id),
            };
        }

        FilterLibrary();
    }

    private void LibraryRoot_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateLibraryGridGeometry(args.NewSize.Width);
        bool compact = args.NewSize.Width < 680;
        if (_libraryCompactList == compact)
        {
            return;
        }

        _libraryCompactList = compact;
        foreach (LibraryEntry entry in _libraryEntries.Values)
        {
            entry.CompactList = compact;
        }
    }

    private long? GetSavedPngSize(Guid id)
    {
        try
        {
            var file = new FileInfo(_controller!.Library.GetExportPath(id));
            return file.Exists ? file.Length : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void FilterLibrary()
    {
        if (_controller is null)
        {
            return;
        }

        string query = LibrarySearch.Text.Trim();
        _restoringLibrarySelection = true;
        try
        {
            ViewModel.Library.Clear();
            IEnumerable<LibraryEntry> filtered = _library
                .Where(document => MatchesLibraryQuery(document, query))
                .Select(document => _libraryEntries.GetValueOrDefault(document.Id))
                .OfType<LibraryEntry>();
            LibraryEntry[] visible = SortLibraryEntries(filtered).ToArray();
            for (int index = 0; index < visible.Length; index++)
            {
                LibraryEntry entry = visible[index];
                entry.IsLastVisible = index == visible.Length - 1;
                ViewModel.Library.Add(entry);
            }

        }
        finally
        {
            _restoringLibrarySelection = false;
        }

        int count = ViewModel.Library.Count;
        bool hasSaved = _library.Count > 0;
        bool noMatches = hasSaved && count == 0;
        LibraryResultCount.Text = query.Length == 0
            ? $"{_library.Count} {ShnappWord(_library.Count)}"
            : $"{count} of {_library.Count} {ShnappWord(_library.Count)}";
        LibraryEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmptyArtwork.Visibility = noMatches ? Visibility.Collapsed : Visibility.Visible;
        LibraryNoResults.Visibility = noMatches ? Visibility.Visible : Visibility.Collapsed;
        LibraryNoResultsDescription.Text = noMatches
            ? $"No results for “{query}”. Try a title, date, or capture type."
            : string.Empty;
        LayoutLibraryEmpty();
        UpdateLibraryGridGeometry(LibraryRoot.ActualWidth);
        UpdateLibraryView();
    }

    private void UpdateLibraryGridGeometry(double rootWidth)
    {
        double availableWidth = rootWidth - LibraryRoot.Padding.Left - LibraryRoot.Padding.Right;
        if (availableWidth <= 0 || ViewModel.Library.Count == 0)
        {
            return;
        }

        const double gutter = 16;
        (double preferredWidth, double preferredHeight) = LibrarySizes[_librarySizeIndex];
        int columns = Math.Max(1, (int)Math.Floor((availableWidth + gutter) / (preferredWidth + gutter)));
        int entryCount = ViewModel.Library.Count;
        if (entryCount >= 3 && entryCount < columns &&
            availableWidth / entryCount <= preferredWidth * 1.5)
        {
            // Fill a short first row when the extra width still honors the chosen thumbnail size.
            columns = entryCount;
        }
        // ItemsWrapGrid can round fractional item widths upward and wrap the last column.
        double tileWidth = Math.Floor(availableWidth / columns);
        double trailingPixelRemainder = availableWidth - tileWidth * columns;
        double cardWidth = tileWidth - (columns > 1 ? gutter : 0);
        double thumbnailHeight = preferredHeight * cardWidth / preferredWidth;

        for (int index = 0; index < ViewModel.Library.Count; index++)
        {
            LibraryEntry entry = ViewModel.Library[index];
            entry.CardTileWidth = tileWidth;
            entry.CardWidth = index % columns == columns - 1
                ? tileWidth + trailingPixelRemainder
                : cardWidth;
            entry.ThumbnailHeight = thumbnailHeight;
        }
    }

    private IEnumerable<LibraryEntry> SortLibraryEntries(IEnumerable<LibraryEntry> entries)
    {
        IOrderedEnumerable<LibraryEntry> sorted = _librarySortField switch
        {
            LibrarySortField.Name when _librarySortDescending =>
                entries.OrderByDescending(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase),
            LibrarySortField.Name =>
                entries.OrderBy(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase),
            LibrarySortField.FileSize when _librarySortDescending =>
                entries.OrderBy(entry => entry.FileSizeBytes is null)
                    .ThenByDescending(entry => entry.FileSizeBytes.GetValueOrDefault()),
            LibrarySortField.FileSize =>
                entries.OrderBy(entry => entry.FileSizeBytes is null)
                    .ThenBy(entry => entry.FileSizeBytes.GetValueOrDefault()),
            LibrarySortField.Dimensions when _librarySortDescending =>
                entries.OrderByDescending(entry => entry.PixelArea),
            LibrarySortField.Dimensions =>
                entries.OrderBy(entry => entry.PixelArea),
            _ when _librarySortDescending =>
                entries.OrderByDescending(entry => entry.ModifiedAt),
            _ =>
                entries.OrderBy(entry => entry.ModifiedAt),
        };

        return sorted.ThenBy(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Id);
    }

    private void LibrarySortField_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: string field } ||
            !Enum.TryParse(field, out LibrarySortField parsed))
        {
            return;
        }

        _librarySortField = parsed;
        _librarySortDescending = parsed != LibrarySortField.Name;
        LibrarySortDescending.IsChecked = _librarySortDescending;
        UpdateLibrarySortLabel();
        FilterLibrary();
    }

    private void LibrarySortDescending_Click(object sender, RoutedEventArgs args)
    {
        _librarySortDescending = LibrarySortDescending.IsChecked;
        UpdateLibrarySortLabel();
        FilterLibrary();
    }

    private void UpdateLibrarySortLabel()
    {
        LibrarySortLabel.Text = (_librarySortField, _librarySortDescending) switch
        {
            (LibrarySortField.Date, true) => "Newest first",
            (LibrarySortField.Date, false) => "Oldest first",
            (LibrarySortField.Name, true) => "Name Z–A",
            (LibrarySortField.Name, false) => "Name A–Z",
            (LibrarySortField.FileSize, true) => "Largest files",
            (LibrarySortField.FileSize, false) => "Smallest files",
            (LibrarySortField.Dimensions, true) => "Largest images",
            _ => "Smallest images",
        };
    }

    private static string ShnappWord(int count) => count == 1 ? "shnapp" : "shnapps";

    private static bool MatchesLibraryQuery(ShnappSummary document, string query)
    {
        if (query.Length == 0)
        {
            return true;
        }

        string type = document.CaptureKind switch
        {
            CaptureKind.Window => "window",
            CaptureKind.FullScreen => "full screen display",
            _ => "region",
        };
        string terms = $"{document.Title} {type} {document.ModifiedAt.ToLocalTime():MMM d yyyy HH:mm} " +
            $"{document.Viewport.Width:0}x{document.Viewport.Height:0} " +
            $"{document.Viewport.Width:0}×{document.Viewport.Height:0}";
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => terms.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateLibraryView()
    {
        bool hasResults = ViewModel.Library.Count > 0;
        LibraryGrid.Visibility = hasResults && !_libraryListMode ? Visibility.Visible : Visibility.Collapsed;
        LibraryList.Visibility = hasResults && _libraryListMode ? Visibility.Visible : Visibility.Collapsed;
        LibraryGridMode.IsChecked = !_libraryListMode;
        LibraryListMode.IsChecked = _libraryListMode;
        LibrarySizeButton.IsEnabled = !_libraryListMode;
        UpdateLibrarySelectionStatus();
    }

    private void LibrarySearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => FilterLibrary();

    private void LibrarySearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (ViewModel.Library.FirstOrDefault() is { } entry)
        {
            _controller?.OpenDocument(entry.Id);
        }
    }

    private void LibrarySearch_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            _controller?.Hide();
            args.Handled = true;
        }
    }

    private void LibraryClearSearch_Click(object sender, RoutedEventArgs args)
    {
        LibrarySearch.Text = string.Empty;
        LibrarySearch.Focus(FocusState.Programmatic);
    }

    private void LibrarySize_Click(object sender, RoutedEventArgs args)
    {
        if (_controller is null || sender is not RadioMenuFlyoutItem item ||
            !int.TryParse(item.Tag?.ToString(), out int index))
        {
            return;
        }

        index = Math.Clamp(index, 0, LibrarySizes.Length - 1);
        if (index != _librarySizeIndex)
        {
            _librarySizeIndex = index;
            RebuildLibraryEntries();
        }
    }

    private void LibraryGridMode_Click(object sender, RoutedEventArgs args)
    {
        _libraryListMode = false;
        RebuildLibraryEntries();
    }

    private void LibraryListMode_Click(object sender, RoutedEventArgs args)
    {
        _libraryListMode = true;
        RebuildLibraryEntries();
    }

    private void LibraryPreview_Tapped(object sender, TappedRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: LibraryEntry entry })
        {
            return;
        }

        _controller?.OpenDocument(entry.Id);

        args.Handled = true;
    }

    private void LibraryCard_RightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: LibraryEntry entry } card || _controller is null)
        {
            return;
        }

        var menu = new MenuFlyout();
        AddAction("Clone", Symbol.Copy, () => _controller.CloneDocument(entry.Id));
        AddAction("Rename", Symbol.Edit, () => _controller.RequestRenameDocument(entry.Id));
        AddAction("Copy snapshot path", Symbol.Copy, () => _controller.CopyDocumentPath(entry.Id));
        AddAction("Share", Symbol.Share, () => _controller.ShareDocument(entry.Id));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddAction("Delete", Symbol.Delete, () => _controller.RequestDeleteDocuments([entry.Id]));
        menu.ShowAt(card, new FlyoutShowOptions { Position = args.GetPosition(card) });
        args.Handled = true;

        void AddAction(string label, Symbol icon, Action action)
        {
            var item = new MenuFlyoutItem { Text = label, Icon = new SymbolIcon(icon) };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private void LibraryCard_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is LibraryEntry entry)
        {
            _controller?.OpenDocument(entry.Id);
        }
    }

    private void LibraryThumbnail_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: LibraryEntry entry })
        {
            entry.IsGridHovered = true;
        }
    }

    private void LibraryThumbnail_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: LibraryEntry entry })
        {
            entry.IsGridHovered = false;
        }
    }

    private void LibraryGrid_GotFocus(object sender, RoutedEventArgs args)
    {
        SetFocusedGridEntry(FindFocusedGridEntry(args.OriginalSource));
    }

    private void LibraryGrid_LostFocus(object sender, RoutedEventArgs args)
    {
        // Focus can move from the card to its newly revealed checkbox. Recheck after
        // that transition instead of hiding the checkbox before it receives focus.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (XamlRoot is not null)
            {
                SetFocusedGridEntry(FindFocusedGridEntry(FocusManager.GetFocusedElement(XamlRoot)));
            }
        });
    }

    private void SetFocusedGridEntry(LibraryEntry? entry)
    {
        if (ReferenceEquals(entry, _focusedGridEntry))
        {
            return;
        }

        if (_focusedGridEntry is not null)
        {
            _focusedGridEntry.IsGridKeyboardFocused = false;
        }
        _focusedGridEntry = entry;
        if (entry is not null)
        {
            entry.IsGridKeyboardFocused = true;
        }
    }

    private static LibraryEntry? FindFocusedGridEntry(object? source)
    {
        for (DependencyObject? current = source as DependencyObject; current is not null;
            current = VisualTreeHelper.GetParent(current))
        {
            if (current is GridViewItem { Content: LibraryEntry entry })
            {
                return entry;
            }
        }

        return null;
    }

    private void LibrarySelectionCheck_Tapped(object sender, TappedRoutedEventArgs args) => args.Handled = true;

    private void LibrarySelectionCheck_Changed(object sender, RoutedEventArgs args)
    {
        if (!IsLoaded || _restoringLibrarySelection ||
            sender is not CheckBox { Tag: LibraryEntry entry } checkbox)
        {
            return;
        }

        bool selected = checkbox.IsChecked == true;
        _restoringLibrarySelection = true;
        try
        {
            entry.IsSelected = selected;
            if (selected)
            {
                _selectedLibraryIds.Add(entry.Id);
            }
            else
            {
                _selectedLibraryIds.Remove(entry.Id);
            }
        }
        finally
        {
            _restoringLibrarySelection = false;
        }
        UpdateLibrarySelectionStatus();
    }

    private void UpdateLibrarySelectionStatus()
    {
        int count = _selectedLibraryIds.Count;
        bool selectionMode = count > 0;
        if (_librarySelectionMode != selectionMode)
        {
            _librarySelectionMode = selectionMode;
            foreach (LibraryEntry entry in _libraryEntries.Values)
            {
                entry.CanDrag = !selectionMode;
            }
        }

        LibrarySelectionCount.Text = $"Delete {count}";
        LibraryDeleteSelected.Visibility = selectionMode ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LibraryDeleteSelected,
            count == 1 ? "Delete 1 selected shnapp" : $"Delete {count} selected shnapps");
    }

    private void LibraryDeleteSelected_Click(object sender, RoutedEventArgs args)
    {
        if (_selectedLibraryIds.Count > 0)
        {
            _controller?.RequestDeleteDocuments(_selectedLibraryIds.ToArray());
        }
    }

    internal void RemoveLibrarySelection(IEnumerable<Guid> ids)
    {
        _restoringLibrarySelection = true;
        try
        {
            foreach (Guid id in ids)
            {
                _selectedLibraryIds.Remove(id);
                if (_libraryEntries.TryGetValue(id, out LibraryEntry? entry))
                {
                    entry.IsSelected = false;
                }
            }
        }
        finally
        {
            _restoringLibrarySelection = false;
        }
        UpdateLibrarySelectionStatus();
    }

    private void FocusLibrarySearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_editor is not null)
        {
            _focusSearchWhenLibraryOpens = true;
            _controller?.OpenLibrary();
        }
        else
        {
            FocusLibrarySearch();
        }
        args.Handled = true;
    }

    private void FocusLibrarySearchIfRequested()
    {
        if (_focusSearchWhenLibraryOpens)
        {
            _focusSearchWhenLibraryOpens = false;
            DispatcherQueue.TryEnqueue(FocusLibrarySearch);
        }
    }

    private void FocusLibrarySearch()
    {
        LibrarySearch.Focus(FocusState.Programmatic);
        FindLibrarySearchInput(LibrarySearch)?.SelectAll();
    }

    private static TextBox? FindLibrarySearchInput(DependencyObject root)
    {
        if (root is TextBox input)
        {
            return input;
        }

        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindLibrarySearchInput(VisualTreeHelper.GetChild(root, index)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
