using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Shnapp.Core;
using Windows.System;

namespace Shnapp.App;

/// <summary>Display details for one video in the library list.</summary>
public sealed class VideoLibraryEntry(Guid id, string title, string metadataText,
    string durationText, string automationLabel)
{
    public Guid Id { get; set; } = id;
    public string Title { get; set; } = title;
    public string MetadataText { get; set; } = metadataText;
    public string DurationText { get; set; } = durationText;
    public string AutomationLabel { get; set; } = automationLabel;
}

public sealed partial class MainPage
{
    private IReadOnlyList<VideoProjectSummary> _videoLibrary = [];
    private bool _showVideoLibrary;

    /// <summary>Video rows consumed by the native library list.</summary>
    public ObservableCollection<VideoLibraryEntry> VideoLibraryEntries { get; } = [];

    /// <summary>Refreshes saved video projects independently of the photo library.</summary>
    internal void SetVideoLibrary(IReadOnlyList<VideoProjectSummary> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        _videoLibrary = projects.ToArray();
        if (_showVideoLibrary) { FilterVideoLibrary(); }
    }

    private void LibraryPhotosTab_Click(object sender, RoutedEventArgs args)
    {
        _showVideoLibrary = false;
        FilterLibrary();
    }

    private void LibraryVideosTab_Click(object sender, RoutedEventArgs args)
    {
        _showVideoLibrary = true;
        FilterVideoLibrary();
    }

    private void UpdateLibraryMediaChrome()
    {
        LibraryPhotosTab.IsChecked = !_showVideoLibrary;
        LibraryVideosTab.IsChecked = _showVideoLibrary;
        LibraryPhotoControls.Visibility = _showVideoLibrary ? Visibility.Collapsed : Visibility.Visible;
        LibrarySearch.PlaceholderText = _showVideoLibrary ? "Search videos" : "Search shnapps";
    }

    private void FilterVideoLibrary()
    {
        UpdateLibraryMediaChrome();
        string query = LibrarySearch.Text.Trim();
        VideoLibraryEntries.Clear();
        foreach (VideoProjectSummary project in _videoLibrary
            .Where(project => MatchesVideoQuery(project, query))
            .OrderByDescending(static project => project.CreatedAt)
            .ThenBy(static project => project.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            DateTimeOffset localDate = project.CreatedAt.ToLocalTime();
            string duration = FormatVideoDuration(project.Duration);
            string metadata = $"{localDate:MMM d, yyyy}  ·  {project.Width} × {project.Height}";
            string label = $"{project.Title}, video, {duration}, recorded " +
                $"{localDate:MMMM d, yyyy}, {project.Width} by {project.Height} pixels";
            VideoLibraryEntries.Add(new VideoLibraryEntry(project.Id, project.Title,
                metadata, duration, label));
        }

        int count = VideoLibraryEntries.Count;
        LibraryResultCount.Text = query.Length == 0
            ? $"{_videoLibrary.Count} {VideoWord(_videoLibrary.Count)}"
            : $"{count} of {_videoLibrary.Count} {VideoWord(_videoLibrary.Count)}";
        LibraryGrid.Visibility = Visibility.Collapsed;
        LibraryList.Visibility = Visibility.Collapsed;
        LibraryEmpty.Visibility = Visibility.Collapsed;
        VideoLibraryList.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        VideoLibraryEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        VideoLibraryEmptyTitle.Text = query.Length > 0 && _videoLibrary.Count > 0
            ? "No matching videos" : "Your videos will appear here";
        VideoLibraryEmptyDescription.Text = query.Length > 0 && _videoLibrary.Count > 0
            ? $"No videos match “{query}”. Try another title or date."
            : "Record a window, region, or full screen to get started.";
        VideoLibraryClearSearch.Visibility = query.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateLibrarySelectionStatus();
    }

    private static bool MatchesVideoQuery(VideoProjectSummary project, string query)
    {
        if (query.Length == 0) { return true; }
        DateTimeOffset localDate = project.CreatedAt.ToLocalTime();
        string terms = $"{project.Title} video {localDate:MMM d yyyy HH:mm} " +
            $"{localDate:yyyy-MM-dd} {project.Width}x{project.Height} " +
            FormatVideoDuration(project.Duration);
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => terms.Contains(term, StringComparison.CurrentCultureIgnoreCase));
    }

    private static string FormatVideoDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}"
        : $"{(int)duration.TotalMinutes:D2}:{duration.Seconds:D2}";

    private static string VideoWord(int count) => count == 1 ? "video" : "videos";

    private void VideoLibrary_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is VideoLibraryEntry entry)
        {
            _controller?.OpenVideoProject(entry.Id);
        }
    }

    private void VideoLibrary_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not VirtualKey.Enter and not VirtualKey.Space || XamlRoot is null)
        {
            return;
        }
        for (DependencyObject? focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            focused is not null; focused = VisualTreeHelper.GetParent(focused))
        {
            if (focused is ListViewItem { Content: VideoLibraryEntry entry })
            {
                args.Handled = true;
                _controller?.OpenVideoProject(entry.Id);
                return;
            }
            if (focused == VideoLibraryList) { break; }
        }
    }
}
