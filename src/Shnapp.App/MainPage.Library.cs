using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    private int _librarySizeIndex = 1;
    private bool _libraryListMode;
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
            _libraryEntries[document.Id] = new LibraryEntry(document,
                _controller.Library.GetPreviewPath(document.Id), cardWidth, thumbnailHeight);
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
        ViewModel.Library.Clear();
        foreach (ShnappDocument document in _library)
        {
            if (MatchesLibraryQuery(document, query) && _libraryEntries.TryGetValue(document.Id, out LibraryEntry? entry))
            {
                ViewModel.Library.Add(entry);
            }
        }

        int count = ViewModel.Library.Count;
        bool hasSaved = _library.Count > 0;
        bool noMatches = hasSaved && count == 0;
        LibraryResultCount.Text = query.Length == 0
            ? $"{_library.Count} {ShnappWord(_library.Count)} saved on this PC"
            : $"{count} of {_library.Count} {ShnappWord(_library.Count)}";
        LibraryHeaderCapture.Visibility = hasSaved ? Visibility.Visible : Visibility.Collapsed;
        LibraryClearSearch.Visibility = query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        LibrarySizeChoice.IsEnabled = !_libraryListMode;
    }

    private void LibrarySearch_TextChanged(object sender, TextChangedEventArgs args) => FilterLibrary();

    private void LibrarySearch_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            LibrarySearch.Text = string.Empty;
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Enter && ViewModel.Library.FirstOrDefault() is { } entry)
        {
            _controller?.OpenDocument(entry.Id);
            args.Handled = true;
        }
    }

    private void LibraryClearSearch_Click(object sender, RoutedEventArgs args)
    {
        LibrarySearch.Text = string.Empty;
        LibrarySearch.Focus(FocusState.Programmatic);
    }

    private void LibrarySize_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_controller is null || LibraryGrid is null)
        {
            return;
        }

        int index = Math.Clamp(LibrarySizeChoice.SelectedIndex, 0, LibrarySizes.Length - 1);
        if (index != _librarySizeIndex)
        {
            _librarySizeIndex = index;
            RebuildLibraryEntries();
        }
    }

    private void LibraryGridMode_Click(object sender, RoutedEventArgs args)
    {
        _libraryListMode = false;
        UpdateLibraryView();
    }

    private void LibraryListMode_Click(object sender, RoutedEventArgs args)
    {
        _libraryListMode = true;
        UpdateLibraryView();
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
        LibrarySearch.SelectAll();
    }
}
