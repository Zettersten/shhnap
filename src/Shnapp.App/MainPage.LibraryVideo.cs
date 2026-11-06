using Shnapp.Core;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private IReadOnlyList<VideoProjectSummary> _videoLibrary = [];
    private string _videoRootPath = string.Empty;

    /// <summary>Refreshes the videos shown beside saved images in the library.</summary>
    internal void SetVideoLibrary(IReadOnlyList<VideoProjectSummary> projects, string videoRootPath)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentException.ThrowIfNullOrWhiteSpace(videoRootPath);
        _videoLibrary = projects.ToArray();
        _videoRootPath = videoRootPath;
        if (_editor is null) RefreshVideoLibraryEntries();
    }

    private string GetVideoSourcePath(VideoProjectSummary project) =>
        Path.Combine(_videoRootPath, project.Id.ToString("N"),
            project.RecordingRelativePath.Replace('/', Path.DirectorySeparatorChar));
}
