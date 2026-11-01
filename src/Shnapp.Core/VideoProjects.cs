using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Shnapp.Core;

/// <summary>The role of a media file in an editable video project.</summary>
public enum VideoAssetKind
{
    Recording,
    ComputerAudio,
    MicrophoneAudio,
    ImportedAudio,
    Image,
    ImportedVideo,
}

/// <summary>An immutable media file owned by a video project directory.</summary>
/// <param name="Id">Stable identifier referenced by timeline items.</param>
/// <param name="Kind">The media's role.</param>
/// <param name="RelativePath">Path beneath the project directory, using forward slashes.</param>
/// <param name="Duration">Source duration; zero for still images.</param>
public sealed record VideoAsset(Guid Id, VideoAssetKind Kind, string RelativePath, TimeSpan Duration)
{
    /// <summary>Optional original file name shown for imported media; old projects may omit it.</summary>
    public string? DisplayName { get; init; }
}

/// <summary>A source-video interval placed on the primary video lane.</summary>
/// <param name="Id">Stable timeline identifier.</param>
/// <param name="AssetId">Identifier of the original MP4 asset.</param>
/// <param name="Start">Start on the output timeline.</param>
/// <param name="SourceIn">First source time retained.</param>
/// <param name="SourceOut">Exclusive source end time.</param>
public sealed record VideoSegment(Guid Id, Guid AssetId, TimeSpan Start, TimeSpan SourceIn, TimeSpan SourceOut)
{
    /// <summary>Length of the source interval.</summary>
    [JsonIgnore]
    public TimeSpan Duration => SourceOut - SourceIn;

    /// <summary>Exclusive end on the output timeline.</summary>
    [JsonIgnore]
    public TimeSpan End => Start + Duration;
}

/// <summary>An independently editable computer, microphone, or imported audio interval.</summary>
/// <param name="Id">Stable timeline identifier.</param>
/// <param name="AssetId">Identifier of an audio asset.</param>
/// <param name="Start">Start on the output timeline.</param>
/// <param name="SourceIn">First source time retained.</param>
/// <param name="SourceOut">Exclusive source end time.</param>
/// <param name="Gain">Linear volume multiplier from 0 to 4.</param>
/// <param name="Muted">Whether this interval is silent in preview and export.</param>
public sealed record AudioSegment(Guid Id, Guid AssetId, TimeSpan Start, TimeSpan SourceIn,
    TimeSpan SourceOut, double Gain = 1, bool Muted = false)
{
    /// <summary>Length of the source interval.</summary>
    [JsonIgnore]
    public TimeSpan Duration => SourceOut - SourceIn;

    /// <summary>Exclusive end on the output timeline.</summary>
    [JsonIgnore]
    public TimeSpan End => Start + Duration;
}

/// <summary>Position and size in output-video pixels.</summary>
/// <param name="X">Horizontal position.</param>
/// <param name="Y">Vertical position.</param>
/// <param name="Width">Visible width.</param>
/// <param name="Height">Visible height.</param>
public readonly record struct VideoRect(double X, double Y, double Width, double Height);

/// <summary>A still image placed over the video for a bounded time.</summary>
/// <param name="Id">Stable timeline identifier.</param>
/// <param name="AssetId">Identifier of an image asset.</param>
/// <param name="Start">Start on the output timeline.</param>
/// <param name="Duration">How long the image is visible.</param>
/// <param name="Bounds">Image position and size in video pixels.</param>
/// <param name="ZIndex">Stack order; larger values appear above smaller values.</param>
public sealed record ImageOverlay(Guid Id, Guid AssetId, TimeSpan Start, TimeSpan Duration,
    VideoRect Bounds, int ZIndex = 0)
{
    /// <summary>Stable layer identity across splits and cuts; empty in older projects.</summary>
    public Guid LayerId { get; init; }

    /// <summary>Exclusive end on the output timeline.</summary>
    [JsonIgnore]
    public TimeSpan End => Start + Duration;
}

/// <summary>A versioned, nondestructive video edit that references immutable media assets.</summary>
/// <param name="SchemaVersion">Version of the serialized project format.</param>
/// <param name="Id">Stable project identifier and directory name.</param>
/// <param name="Title">User-facing title.</param>
/// <param name="CreatedAt">UTC creation time.</param>
/// <param name="Width">Output width in pixels.</param>
/// <param name="Height">Output height in pixels.</param>
/// <param name="Assets">Original and imported media files.</param>
/// <param name="VideoSegments">The ordered primary video lane.</param>
/// <param name="AudioSegments">Independent audio intervals.</param>
/// <param name="ImageOverlays">Timed still-image overlays.</param>
public sealed record VideoProject(int SchemaVersion, Guid Id, string Title, DateTimeOffset CreatedAt,
    int Width, int Height, ImmutableArray<VideoAsset> Assets,
    ImmutableArray<VideoSegment> VideoSegments, ImmutableArray<AudioSegment> AudioSegments,
    ImmutableArray<ImageOverlay> ImageOverlays)
{
    /// <summary>The current project schema.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Requested capture rate, retained for preview and MP4 export.</summary>
    /// <remarks>Older project JSON omits this property and opens at 30 fps.</remarks>
    public int FrameRate { get; init; } = 30;

    /// <summary>End of the last visible or audible timeline item.</summary>
    [JsonIgnore]
    public TimeSpan Duration
    {
        get
        {
            TimeSpan end = TimeSpan.Zero;
            foreach (VideoSegment item in VideoSegments)
                if (item.End > end) end = item.End;
            foreach (AudioSegment item in AudioSegments)
                if (item.End > end) end = item.End;
            foreach (ImageOverlay item in ImageOverlays)
                if (item.End > end) end = item.End;
            return end;
        }
    }

    /// <summary>Creates a project from a finished MP4 and optional separate WAV stems.</summary>
    /// <param name="title">User-facing title.</param>
    /// <param name="duration">Recorded video duration.</param>
    /// <param name="width">Recorded video width in pixels.</param>
    /// <param name="height">Recorded video height in pixels.</param>
    /// <param name="mp4RelativePath">Original MP4 path beneath this project's directory.</param>
    /// <param name="computerAudioRelativePath">Optional separate computer-audio WAV path.</param>
    /// <param name="microphoneAudioRelativePath">Optional separate microphone WAV path.</param>
    /// <param name="id">Optional predetermined project identifier.</param>
    /// <param name="frameRate">Requested capture rate in frames per second.</param>
    /// <returns>A validated project with aligned initial video and audio segments.</returns>
    public static VideoProject CreateRecording(string title, TimeSpan duration, int width, int height,
        string mp4RelativePath, string? computerAudioRelativePath = null,
        string? microphoneAudioRelativePath = null, Guid? id = null, int frameRate = 30)
    {
        Guid recordingId = Guid.NewGuid();
        var assets = ImmutableArray.CreateBuilder<VideoAsset>();
        var audio = ImmutableArray.CreateBuilder<AudioSegment>();
        assets.Add(new VideoAsset(recordingId, VideoAssetKind.Recording, mp4RelativePath, duration));
        AddStem(computerAudioRelativePath, VideoAssetKind.ComputerAudio);
        AddStem(microphoneAudioRelativePath, VideoAssetKind.MicrophoneAudio);

        var project = new VideoProject(CurrentSchemaVersion, id ?? Guid.NewGuid(),
            ShnappTitles.Normalize(title), DateTimeOffset.UtcNow, width, height,
            assets.ToImmutable(), [new VideoSegment(Guid.NewGuid(), recordingId,
                TimeSpan.Zero, TimeSpan.Zero, duration)], audio.ToImmutable(), [])
        {
            FrameRate = frameRate,
        };
        VideoProjectValidation.Validate(project);
        return project;

        void AddStem(string? relativePath, VideoAssetKind kind)
        {
            if (relativePath is null) { return; }
            Guid assetId = Guid.NewGuid();
            assets.Add(new VideoAsset(assetId, kind, relativePath, duration));
            audio.Add(new AudioSegment(Guid.NewGuid(), assetId, TimeSpan.Zero,
                TimeSpan.Zero, duration));
        }
    }
}

/// <summary>Small, file-independent details used to populate a video library.</summary>
/// <param name="Id">Project identifier.</param>
/// <param name="Title">User-facing title.</param>
/// <param name="CreatedAt">UTC creation time.</param>
/// <param name="Duration">Current edited video duration.</param>
/// <param name="Width">Output width in pixels.</param>
/// <param name="Height">Output height in pixels.</param>
public sealed record VideoProjectSummary(Guid Id, string Title, DateTimeOffset CreatedAt,
    TimeSpan Duration, int Width, int Height);
