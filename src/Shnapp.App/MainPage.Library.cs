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
    private readonly HashSet<Guid> _selectedLibraryIds = [];
    private bool _focusSearchWhenLibraryOpens;

    private void RebuildLibraryEntries()
    {
        if (_controller is null)
        {
            return;
        }

        (double cardWidth, double thumbnailHeight) = LibrarySizes[_librarySizeIndex];
        _libraryEntries.Clear();
        foreach (ShnappDocument document in _library)
        {
            string compact = _controller.Library.GetCompactPreviewPath(document.Id);
            string preview = (_libraryListMode || _librarySizeIndex == 0) && File.Exists(compact)
                ? compact
                : _controller.Library.GetPreviewPath(document.Id);
            _libraryEntries[document.Id] = new LibraryEntry(document,
                preview, cardWidth, thumbnailHeight, preview == compact ? 192 : 384)
            {
                CanDrag = !_librarySelectionMode,
            };
        }

        FilterLibrary();
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
            foreach (ShnappDocument document in _library)
            {
                if (MatchesLibraryQuery(document, query) && _libraryEntries.TryGetValue(document.Id, out LibraryEntry? entry))
                {
                    ViewModel.Library.Add(entry);
                }
            }

            if (_librarySelectionMode)
            {
                foreach (LibraryEntry entry in ViewModel.Library.Where(entry => _selectedLibraryIds.Contains(entry.Id)))
                {
                    LibraryGrid.SelectedItems.Add(entry);
                    LibraryList.SelectedItems.Add(entry);
                }
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
        LibraryEmptyLogo.Visibility = noMatches ? Visibility.Collapsed : Visibility.Visible;
        LibraryNoResultsIcon.Visibility = noMatches ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmptyTitle.Text = noMatches ? "No matching shnapps" : "Your first shnapp starts here";
        LibraryEmptyDescription.Text = noMatches
            ? $"No results for “{query}”. Try a title, date, or capture type."
            : "Capture a window or region. Your shnapps will be saved here, ready to revisit.";
        LibraryEmptyCapture.Visibility = noMatches ? Visibility.Collapsed : Visibility.Visible;
        LibraryEmptyClear.Visibility = noMatches ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmptyShortcuts.Visibility = noMatches ? Visibility.Collapsed : Visibility.Visible;
        UpdateLibraryView();
    }

    private static string ShnappWord(int count) => count == 1 ? "shnapp" : "shnapps";

    private static bool MatchesLibraryQuery(ShnappDocument document, string query)
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
        string terms = $"{document.Title} {type} {document.CreatedAt.ToLocalTime():MMM d yyyy HH:mm} " +
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
        LibrarySelectionBar.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
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
            LibrarySearch.Text = string.Empty;
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

        if (_librarySelectionMode)
        {
            // Native GridView/ListView selection handles the tap and its keyboard equivalent.
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
        AddAction("Clone", () => _controller.CloneDocument(entry.Id));
        AddAction("Rename", () => _controller.RequestRenameDocument(entry.Id));
        AddAction("Copy full path", () => _controller.CopyDocumentPath(entry.Id));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddAction("Delete", () => _controller.RequestDeleteDocuments([entry.Id]));
        menu.ShowAt(card, new FlyoutShowOptions { Position = args.GetPosition(card) });
        args.Handled = true;

        void AddAction(string label, Action action)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private void LibrarySelectionToggle_Changed(object sender, RoutedEventArgs args)
    {
        if (!IsLoaded)
        {
            return;
        }

        _librarySelectionMode = LibrarySelectionToggle.IsChecked == true;
        _restoringLibrarySelection = true;
        try
        {
            if (!_librarySelectionMode)
            {
                _selectedLibraryIds.Clear();
                LibraryGrid.SelectedItems.Clear();
                LibraryList.SelectedItems.Clear();
            }

            LibraryGrid.SelectionMode = _librarySelectionMode ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
            LibraryList.SelectionMode = _librarySelectionMode ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
            LibraryGrid.IsItemClickEnabled = !_librarySelectionMode;
            LibraryList.IsItemClickEnabled = !_librarySelectionMode;
            foreach (LibraryEntry entry in _libraryEntries.Values)
            {
                entry.CanDrag = !_librarySelectionMode;
            }
        }
        finally
        {
            _restoringLibrarySelection = false;
        }

        UpdateLibrarySelectionStatus();
    }

    private void Library_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_restoringLibrarySelection || !_librarySelectionMode)
        {
            return;
        }

        ListViewBase active = _libraryListMode ? LibraryList : LibraryGrid;
        if (!ReferenceEquals(sender, active))
        {
            return;
        }

        foreach (LibraryEntry entry in args.RemovedItems.OfType<LibraryEntry>())
        {
            _selectedLibraryIds.Remove(entry.Id);
        }
        foreach (LibraryEntry entry in args.AddedItems.OfType<LibraryEntry>())
        {
            _selectedLibraryIds.Add(entry.Id);
        }
        UpdateLibrarySelectionStatus();
    }

    private void UpdateLibrarySelectionStatus()
    {
        int count = _selectedLibraryIds.Count;
        LibrarySelectionCount.Text = $"{count} selected";
        LibrarySelectionCount.Visibility = _librarySelectionMode ? Visibility.Visible : Visibility.Collapsed;
        LibraryDeleteSelected.Visibility = _librarySelectionMode ? Visibility.Visible : Visibility.Collapsed;
        LibraryDeleteSelected.IsEnabled = count > 0;
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
        _selectedLibraryIds.ExceptWith(ids);
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
