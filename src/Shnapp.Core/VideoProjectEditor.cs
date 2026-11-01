using System.Collections.Immutable;

namespace Shnapp.Core;

/// <summary>Edits immutable video-project snapshots with bounded undo and redo history.</summary>
/// <param name="project">A valid project snapshot.</param>
/// <remarks>Use an editor on one thread, normally the application's UI thread.</remarks>
public sealed class VideoProjectEditor(VideoProject project)
{
    private const int HistoryLimit = 100;
    private readonly List<VideoProject> _undo = [];
    private readonly List<VideoProject> _redo = [];
    private VideoProject _current = ValidateInitial(project);

    /// <summary>The current immutable project snapshot.</summary>
    public VideoProject Current => _current;

    /// <summary>Whether an earlier edit can be restored.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Whether an undone edit can be reapplied.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised once after a real edit, undo, or redo.</summary>
    public event EventHandler? Changed;

    /// <summary>Changes the project title as one undoable edit.</summary>
    /// <param name="title">A nonempty title of at most 120 characters.</param>
    public void Rename(string title) => Commit(Current with { Title = ShnappTitles.Normalize(title) });

    /// <summary>Adds an imported media asset without modifying its file.</summary>
    /// <param name="asset">A unique asset with a safe project-relative path.</param>
    public void AddAsset(VideoAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Kind is not VideoAssetKind.ImportedVideo and not VideoAssetKind.ImportedAudio and not VideoAssetKind.Image)
        {
            throw new ArgumentException("Only imported video, audio, and image assets can be added after recording.", nameof(asset));
        }
        Commit(Current with { Assets = Current.Assets.Add(asset) });
    }

    /// <summary>Inserts a new video and its separate audio stems, rippling every existing track as one undoable edit.</summary>
    /// <param name="asset">A new imported video asset.</param>
    /// <param name="at">Insertion point on the output timeline.</param>
    /// <param name="audioStems">New audio assets and segments with start times relative to the inserted video.</param>
    /// <returns>The inserted clip identifier.</returns>
    public Guid InsertVideoAsset(VideoAsset asset, TimeSpan at,
        IReadOnlyList<(VideoAsset Asset, AudioSegment Segment)>? audioStems = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Kind != VideoAssetKind.ImportedVideo)
            throw new ArgumentException("The inserted asset must be an imported video.", nameof(asset));
        return InsertVideo(asset, at, addAsset: true, audioStems);
    }

    /// <summary>Inserts an existing imported video asset at the playhead.</summary>
    /// <param name="assetId">Identifier of an imported video already in this project.</param>
    /// <param name="at">Insertion point on the output timeline.</param>
    /// <returns>The inserted clip identifier.</returns>
    public Guid InsertVideoClip(Guid assetId, TimeSpan at)
    {
        VideoAsset asset = Current.Assets.FirstOrDefault(item => item.Id == assetId)
            ?? throw new ArgumentException("Video asset was not found.", nameof(assetId));
        if (asset.Kind != VideoAssetKind.ImportedVideo)
            throw new ArgumentException("The inserted asset must be an imported video.", nameof(assetId));
        return InsertVideo(asset, at, addAsset: false, audioStems: null);
    }

    private Guid InsertVideo(VideoAsset asset, TimeSpan at, bool addAsset,
        IReadOnlyList<(VideoAsset Asset, AudioSegment Segment)>? audioStems)
    {
        if (at < TimeSpan.Zero || at > Current.Duration)
            throw new ArgumentOutOfRangeException(nameof(at), "Insert time must be on the video timeline.");
        if (asset.Duration <= TimeSpan.Zero)
            throw new ArgumentException("The imported video must have a duration.", nameof(asset));
        if (audioStems is not null)
        {
            foreach ((VideoAsset audioAsset, AudioSegment segment) in audioStems)
            {
                if (audioAsset.Kind != VideoAssetKind.ImportedAudio ||
                    segment.AssetId != audioAsset.Id || segment.Start < TimeSpan.Zero ||
                    segment.End > asset.Duration)
                {
                    throw new ArgumentException(
                        "Audio stems must be imported audio within the inserted video's duration.",
                        nameof(audioStems));
                }
            }
        }

        var inserted = new VideoSegment(Guid.NewGuid(), asset.Id,
            at, TimeSpan.Zero, asset.Duration);
        var video = ImmutableArray.CreateBuilder<VideoSegment>();
        foreach (VideoSegment segment in Current.VideoSegments)
        {
            if (segment.End <= at) { video.Add(segment); continue; }
            if (segment.Start >= at)
            {
                video.Add(segment with { Start = segment.Start + asset.Duration });
                continue;
            }
            TimeSpan leftDuration = at - segment.Start;
            video.Add(segment with { SourceOut = segment.SourceIn + leftDuration });
            video.Add(segment with
            {
                Id = Guid.NewGuid(),
                Start = at + asset.Duration,
                SourceIn = segment.SourceIn + leftDuration,
            });
        }
        video.Add(inserted);

        var audio = ImmutableArray.CreateBuilder<AudioSegment>();
        foreach (AudioSegment segment in Current.AudioSegments)
        {
            if (segment.End <= at) { audio.Add(segment); continue; }
            if (segment.Start >= at)
            {
                audio.Add(segment with { Start = segment.Start + asset.Duration });
                continue;
            }
            TimeSpan leftDuration = at - segment.Start;
            audio.Add(segment with { SourceOut = segment.SourceIn + leftDuration });
            audio.Add(segment with
            {
                Id = Guid.NewGuid(),
                Start = at + asset.Duration,
                SourceIn = segment.SourceIn + leftDuration,
            });
        }
        if (audioStems is not null)
        {
            foreach (var stem in audioStems)
                audio.Add(stem.Segment with { Start = at + stem.Segment.Start });
        }

        var images = ImmutableArray.CreateBuilder<ImageOverlay>();
        foreach (ImageOverlay overlay in Current.ImageOverlays)
        {
            if (overlay.End <= at) { images.Add(overlay); continue; }
            if (overlay.Start >= at)
            {
                images.Add(overlay with { Start = overlay.Start + asset.Duration });
                continue;
            }
            images.Add(overlay with
            {
                Duration = at - overlay.Start,
                LayerId = LayerKey(overlay),
            });
            images.Add(overlay with
            {
                Id = Guid.NewGuid(),
                Start = at + asset.Duration,
                Duration = overlay.End - at,
                LayerId = LayerKey(overlay),
            });
        }

        Commit(Current with
        {
            Assets = addAsset
                ? Current.Assets.Add(asset).AddRange(audioStems?.Select(stem => stem.Asset) ?? [])
                : Current.Assets,
            VideoSegments = Sort(video.ToImmutable()),
            AudioSegments = Sort(audio.ToImmutable()),
            ImageOverlays = images.ToImmutable(),
        });
        return inserted.Id;
    }

    /// <summary>Adds a nonoverlapping part of the original MP4 to the video lane.</summary>
    /// <param name="segment">A valid source interval and timeline placement.</param>
    public void AddVideoSegment(VideoSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        Commit(Current with { VideoSegments = Sort(Current.VideoSegments.Add(segment)) });
    }

    /// <summary>Adds a computer, microphone, or imported audio interval.</summary>
    /// <param name="segment">A valid audio interval and timeline placement.</param>
    public void AddAudioSegment(AudioSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        Commit(Current with { AudioSegments = Sort(Current.AudioSegments.Add(segment)) });
    }

    /// <summary>Adds a timed still image to the overlay lanes.</summary>
    /// <param name="overlay">A valid image placement and duration.</param>
    public void AddImageOverlay(ImageOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        Commit(Current with
        {
            ImageOverlays = Current.ImageOverlays.Add(overlay with
            {
                LayerId = LayerKey(overlay),
            }),
        });
    }

    /// <summary>Replaces an existing video segment, preserving its identifier.</summary>
    /// <param name="segment">Complete replacement segment.</param>
    /// <returns>False when the segment is absent or unchanged.</returns>
    public bool UpdateVideoSegment(VideoSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        int index = Find(Current.VideoSegments, segment.Id);
        if (index < 0 || Current.VideoSegments[index] == segment) { return false; }
        Commit(Current with { VideoSegments = Sort(Current.VideoSegments.SetItem(index, segment)) });
        return true;
    }

    /// <summary>Changes a video's source range while keeping its timeline start fixed.</summary>
    /// <param name="id">Video segment identifier.</param>
    /// <param name="sourceIn">New first source time.</param>
    /// <param name="sourceOut">New exclusive source end time.</param>
    /// <returns>False when the segment is absent or unchanged.</returns>
    public bool TrimVideoSegment(Guid id, TimeSpan sourceIn, TimeSpan sourceOut)
    {
        int index = Find(Current.VideoSegments, id);
        return index >= 0 && UpdateVideoSegment(Current.VideoSegments[index] with
        {
            SourceIn = sourceIn,
            SourceOut = sourceOut,
        });
    }

    /// <summary>Moves a video segment without moving other timeline items.</summary>
    /// <param name="id">Video segment identifier.</param>
    /// <param name="start">New timeline start; overlaps are rejected.</param>
    /// <returns>False when the segment is absent or unchanged.</returns>
    public bool MoveVideoSegment(Guid id, TimeSpan start)
    {
        int index = Find(Current.VideoSegments, id);
        return index >= 0 && UpdateVideoSegment(Current.VideoSegments[index] with { Start = start });
    }

    /// <summary>Splits one video segment at a time strictly inside its timeline interval.</summary>
    /// <param name="id">Video segment identifier.</param>
    /// <param name="at">Split time on the output timeline.</param>
    /// <returns>The new right-hand segment identifier.</returns>
    public Guid SplitVideoSegment(Guid id, TimeSpan at)
    {
        int index = Find(Current.VideoSegments, id);
        if (index < 0) { throw new ArgumentException("Video segment was not found.", nameof(id)); }
        VideoSegment original = Current.VideoSegments[index];
        if (at <= original.Start || at >= original.End)
        {
            throw new ArgumentOutOfRangeException(nameof(at), "Split time must be inside the segment.");
        }
        TimeSpan leftDuration = at - original.Start;
        var left = original with { SourceOut = original.SourceIn + leftDuration };
        var right = original with
        {
            Id = Guid.NewGuid(),
            Start = at,
            SourceIn = left.SourceOut,
        };
        Commit(Current with
        {
            VideoSegments = Sort(Current.VideoSegments.SetItem(index, left).Add(right)),
        });
        return right.Id;
    }

    /// <summary>Removes a video segment, optionally closing its time across every lane.</summary>
    /// <param name="id">Video segment identifier.</param>
    /// <param name="ripple">When true, cuts the segment's interval from all lanes.</param>
    /// <returns>False when no matching segment exists.</returns>
    public bool DeleteVideoSegment(Guid id, bool ripple = false)
    {
        int index = Find(Current.VideoSegments, id);
        if (index < 0) { return false; }
        VideoSegment segment = Current.VideoSegments[index];
        if (ripple)
        {
            CutRange(segment.Start, segment.End);
            return true;
        }
        ImmutableArray<VideoSegment> video = Current.VideoSegments.RemoveAt(index);
        if (video.IsEmpty)
        {
            throw new InvalidOperationException("A video project must retain a primary video segment.");
        }
        TimeSpan end = video.Max(static item => item.End);
        Commit(Current with
        {
            VideoSegments = video,
            AudioSegments = TrimAudioToEnd(Current.AudioSegments, end),
            ImageOverlays = TrimImagesToEnd(Current.ImageOverlays, end),
        });
        return true;
    }

    /// <summary>Replaces an existing audio interval, including its gain and mute state.</summary>
    /// <param name="segment">Complete replacement audio segment.</param>
    /// <returns>False when the segment is absent or unchanged.</returns>
    public bool UpdateAudioSegment(AudioSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        int index = Find(Current.AudioSegments, segment.Id);
        if (index < 0 || Current.AudioSegments[index] == segment) { return false; }
        Commit(Current with { AudioSegments = Sort(Current.AudioSegments.SetItem(index, segment)) });
        return true;
    }

    /// <summary>Changes an audio source range while retaining its timeline start.</summary>
    /// <param name="id">Audio segment identifier.</param>
    /// <param name="sourceIn">New first source time.</param>
    /// <param name="sourceOut">New exclusive source end time.</param>
    /// <returns>False when the segment is absent or unchanged.</returns>
    public bool TrimAudioSegment(Guid id, TimeSpan sourceIn, TimeSpan sourceOut)
    {
        int index = Find(Current.AudioSegments, id);
        return index >= 0 && UpdateAudioSegment(Current.AudioSegments[index] with
        {
            SourceIn = sourceIn,
            SourceOut = sourceOut,
        });
    }

    /// <summary>Moves one audio interval without changing its source time.</summary>
    /// <param name="id">Audio segment identifier.</param>
    /// <param name="start">New timeline start.</param>
    /// <returns>False when the segment is absent or unchanged.</returns>
    public bool MoveAudioSegment(Guid id, TimeSpan start)
    {
        int index = Find(Current.AudioSegments, id);
        return index >= 0 && UpdateAudioSegment(Current.AudioSegments[index] with { Start = start });
    }

    /// <summary>Sets a linear volume multiplier from 0 to 4 for one audio interval.</summary>
    /// <param name="id">Audio segment identifier.</param>
    /// <param name="gain">Linear volume multiplier.</param>
    /// <returns>False when the segment is absent or unchanged.</returns>
    public bool SetAudioGain(Guid id, double gain)
    {
        int index = Find(Current.AudioSegments, id);
        return index >= 0 && UpdateAudioSegment(Current.AudioSegments[index] with { Gain = gain });
    }

    /// <summary>Mutes or unmutes one audio interval.</summary>
    /// <param name="id">Audio segment identifier.</param>
    /// <param name="muted">Whether the interval should be silent.</param>
    /// <returns>False when the segment is absent or unchanged.</returns>
    public bool SetAudioMuted(Guid id, bool muted)
    {
        int index = Find(Current.AudioSegments, id);
        return index >= 0 && UpdateAudioSegment(Current.AudioSegments[index] with { Muted = muted });
    }

    /// <summary>Splits one audio interval at a time strictly inside it.</summary>
    /// <param name="id">Audio segment identifier.</param>
    /// <param name="at">Split time on the output timeline.</param>
    /// <returns>The new right-hand segment identifier.</returns>
    public Guid SplitAudioSegment(Guid id, TimeSpan at)
    {
        int index = Find(Current.AudioSegments, id);
        if (index < 0) { throw new ArgumentException("Audio segment was not found.", nameof(id)); }
        AudioSegment original = Current.AudioSegments[index];
        if (at <= original.Start || at >= original.End)
        {
            throw new ArgumentOutOfRangeException(nameof(at), "Split time must be inside the segment.");
        }
        TimeSpan leftDuration = at - original.Start;
        var left = original with { SourceOut = original.SourceIn + leftDuration };
        var right = original with
        {
            Id = Guid.NewGuid(),
            Start = at,
            SourceIn = left.SourceOut,
        };
        Commit(Current with { AudioSegments = Sort(Current.AudioSegments.SetItem(index, left).Add(right)) });
        return right.Id;
    }

    /// <summary>Deletes one audio interval while retaining other timeline positions.</summary>
    /// <param name="id">Audio segment identifier.</param>
    /// <returns>False when no matching segment exists.</returns>
    public bool DeleteAudioSegment(Guid id)
    {
        int index = Find(Current.AudioSegments, id);
        if (index < 0) { return false; }
        Commit(Current with { AudioSegments = Current.AudioSegments.RemoveAt(index) });
        return true;
    }

    /// <summary>Cuts a marked interval from one audio clip without changing video time or other tracks.</summary>
    /// <param name="id">The selected audio clip.</param>
    /// <param name="start">First output-timeline time to silence.</param>
    /// <param name="end">Exclusive output-timeline time to silence.</param>
    public void CutAudioRange(Guid id, TimeSpan start, TimeSpan end)
    {
        int index = Find(Current.AudioSegments, id);
        if (index < 0) { throw new ArgumentException("Audio segment was not found.", nameof(id)); }
        AudioSegment selected = Current.AudioSegments[index];
        if (start < TimeSpan.Zero || end <= start || end > Current.Duration ||
            start >= selected.End || end <= selected.Start)
        {
            throw new ArgumentOutOfRangeException(nameof(start),
                "Cut range must overlap the selected audio clip inside the video timeline.");
        }

        TimeSpan cutStart = start > selected.Start ? start : selected.Start;
        TimeSpan cutEnd = end < selected.End ? end : selected.End;
        var audio = Current.AudioSegments.RemoveAt(index);
        if (cutStart > selected.Start)
        {
            audio = audio.Add(selected with
            {
                SourceOut = selected.SourceIn + (cutStart - selected.Start),
            });
        }
        if (cutEnd < selected.End)
        {
            audio = audio.Add(selected with
            {
                Id = cutStart > selected.Start ? Guid.NewGuid() : selected.Id,
                Start = cutEnd,
                SourceIn = selected.SourceIn + (cutEnd - selected.Start),
            });
        }
        Commit(Current with { AudioSegments = Sort(audio) });
    }

    /// <summary>Replaces an existing image's position, duration, and z-order.</summary>
    /// <param name="overlay">Complete replacement image overlay.</param>
    /// <returns>False when the image is absent or unchanged.</returns>
    public bool UpdateImageOverlay(ImageOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        int index = Find(Current.ImageOverlays, overlay.Id);
        if (index < 0 || Current.ImageOverlays[index] == overlay) { return false; }
        Commit(Current with { ImageOverlays = Current.ImageOverlays.SetItem(index, overlay) });
        return true;
    }

    /// <summary>Moves an image layer one position forward or backward in the visual stack.</summary>
    /// <param name="id">An image overlay in the layer to move.</param>
    /// <param name="direction">One for forward; minus one for backward.</param>
    /// <returns>False when the image does not exist or is already at that edge.</returns>
    /// <remarks>Split pieces of the same image layer move together as one undoable edit.</remarks>
    public bool MoveImageLayer(Guid id, int direction)
    {
        if (direction is not 1 and not -1)
            throw new ArgumentOutOfRangeException(nameof(direction), "Use one step forward or backward.");
        ImageOverlay? selected = Current.ImageOverlays.FirstOrDefault(item => item.Id == id);
        if (selected is null) return false;

        var layers = Current.ImageOverlays.Select((item, index) => (item, index))
            .GroupBy(entry => LayerKey(entry.item))
            .OrderBy(group => group.First().item.ZIndex)
            .ThenBy(group => group.Min(entry => entry.index))
            .ToArray();
        int selectedIndex = Array.FindIndex(layers, group =>
            group.Key == LayerKey(selected));
        int nextIndex = selectedIndex + direction;
        if (nextIndex < 0 || nextIndex >= layers.Length) return false;

        var order = layers.Select((group, index) => (group.Key, index))
            .ToDictionary(entry => entry.Key, entry => entry.index);
        order[layers[selectedIndex].Key] = nextIndex;
        order[layers[nextIndex].Key] = selectedIndex;
        Commit(Current with
        {
            ImageOverlays = [.. Current.ImageOverlays.Select(item => item with
            {
                ZIndex = order[LayerKey(item)],
            })],
        });
        return true;
    }

    /// <summary>Deletes one timed image without changing other timeline items.</summary>
    /// <param name="id">Image overlay identifier.</param>
    /// <returns>False when no matching overlay exists.</returns>
    public bool DeleteImageOverlay(Guid id)
    {
        int index = Find(Current.ImageOverlays, id);
        if (index < 0) { return false; }
        Commit(Current with { ImageOverlays = Current.ImageOverlays.RemoveAt(index) });
        return true;
    }

    /// <summary>Splits one image overlay at the playhead as a single undoable edit.</summary>
    /// <returns>The new right-hand overlay identifier.</returns>
    public Guid SplitImageOverlay(Guid id, TimeSpan at)
    {
        int index = Find(Current.ImageOverlays, id);
        if (index < 0) { throw new ArgumentException("Image overlay was not found.", nameof(id)); }
        ImageOverlay image = Current.ImageOverlays[index];
        if (at <= image.Start || at >= image.End)
        {
            throw new ArgumentOutOfRangeException(nameof(at),
                "Split time must be inside the selected image.");
        }
        var right = image with
        {
            Id = Guid.NewGuid(),
            Start = at,
            Duration = image.End - at,
            LayerId = LayerKey(image),
        };
        Commit(Current with
        {
            ImageOverlays = Current.ImageOverlays
                .SetItem(index, image with
                {
                    Duration = at - image.Start,
                    LayerId = LayerKey(image),
                })
                .Insert(index + 1, right),
        });
        return right.Id;
    }

    /// <summary>Removes a marked interval from one image overlay without changing video time.</summary>
    /// <returns>The surviving selected overlay, or null when the image was removed.</returns>
    public Guid? CutImageRange(Guid id, TimeSpan start, TimeSpan end)
    {
        int index = Find(Current.ImageOverlays, id);
        if (index < 0) { throw new ArgumentException("Image overlay was not found.", nameof(id)); }
        ImageOverlay image = Current.ImageOverlays[index];
        if (start < TimeSpan.Zero || end <= start || end > Current.Duration ||
            start >= image.End || end <= image.Start)
        {
            throw new ArgumentOutOfRangeException(nameof(start),
                "Cut range must overlap the selected image inside the video timeline.");
        }

        TimeSpan cutStart = start > image.Start ? start : image.Start;
        TimeSpan cutEnd = end < image.End ? end : image.End;
        ImmutableArray<ImageOverlay> images = Current.ImageOverlays;
        Guid? selected = id;
        if (cutStart == image.Start && cutEnd == image.End)
        {
            images = images.RemoveAt(index);
            selected = null;
        }
        else if (cutStart == image.Start)
        {
            images = images.SetItem(index, image with
            {
                Start = cutEnd,
                Duration = image.End - cutEnd,
                LayerId = LayerKey(image),
            });
        }
        else
        {
            images = images.SetItem(index, image with
            {
                Duration = cutStart - image.Start,
                LayerId = LayerKey(image),
            });
            if (cutEnd < image.End)
            {
                var right = image with
                {
                    Id = Guid.NewGuid(),
                    Start = cutEnd,
                    Duration = image.End - cutEnd,
                    LayerId = LayerKey(image),
                };
                images = images.Insert(index + 1, right);
                selected = right.Id;
            }
        }
        Commit(Current with { ImageOverlays = images });
        return selected;
    }

    /// <summary>Removes a time range from every lane and shifts later content left.</summary>
    /// <param name="start">Inclusive start on the output timeline.</param>
    /// <param name="end">Exclusive end on the output timeline.</param>
    /// <remarks>Items crossing either edge are trimmed or split without changing their source files.</remarks>
    public void CutRange(TimeSpan start, TimeSpan end)
    {
        if (start < TimeSpan.Zero || end <= start || end > Current.Duration)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Cut range must be inside the video timeline.");
        }
        ImmutableArray<VideoSegment> video = CutVideo(Current.VideoSegments, start, end);
        if (video.IsEmpty)
        {
            throw new InvalidOperationException("A cut cannot remove every primary video segment.");
        }
        Commit(Current with
        {
            VideoSegments = video,
            AudioSegments = CutAudio(Current.AudioSegments, start, end),
            ImageOverlays = CutImages(Current.ImageOverlays, start, end),
        });
    }

    /// <summary>Restores the previous project snapshot.</summary>
    /// <returns>False when there is no edit to undo.</returns>
    public bool Undo()
    {
        if (_undo.Count == 0) { return false; }
        _redo.Add(_current);
        _current = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Reapplies the most recently undone project snapshot.</summary>
    /// <returns>False when there is no edit to redo.</returns>
    public bool Redo()
    {
        if (_redo.Count == 0) { return false; }
        _undo.Add(_current);
        _current = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Commit(VideoProject next)
    {
        VideoProjectValidation.Validate(next);
        if (next.Title == Current.Title && next.Assets.SequenceEqual(Current.Assets) &&
            next.VideoSegments.SequenceEqual(Current.VideoSegments) &&
            next.AudioSegments.SequenceEqual(Current.AudioSegments) &&
            next.ImageOverlays.SequenceEqual(Current.ImageOverlays))
        {
            return;
        }
        _undo.Add(_current);
        if (_undo.Count > HistoryLimit) { _undo.RemoveAt(0); }
        _redo.Clear();
        _current = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static VideoProject ValidateInitial(VideoProject project)
    {
        VideoProjectValidation.Validate(project);
        return project;
    }

    private static int Find(ImmutableArray<VideoSegment> items, Guid id)
    {
        for (int i = 0; i < items.Length; i++) { if (items[i].Id == id) { return i; } }
        return -1;
    }

    private static int Find(ImmutableArray<AudioSegment> items, Guid id)
    {
        for (int i = 0; i < items.Length; i++) { if (items[i].Id == id) { return i; } }
        return -1;
    }

    private static int Find(ImmutableArray<ImageOverlay> items, Guid id)
    {
        for (int i = 0; i < items.Length; i++) { if (items[i].Id == id) { return i; } }
        return -1;
    }

    private static Guid LayerKey(ImageOverlay overlay) =>
        overlay.LayerId == Guid.Empty ? overlay.Id : overlay.LayerId;

    private static ImmutableArray<VideoSegment> Sort(ImmutableArray<VideoSegment> items) =>
        [.. items.OrderBy(static item => item.Start).ThenBy(static item => item.Id)];

    private static ImmutableArray<AudioSegment> Sort(ImmutableArray<AudioSegment> items) =>
        [.. items.OrderBy(static item => item.Start).ThenBy(static item => item.Id)];

    private static ImmutableArray<VideoSegment> CutVideo(ImmutableArray<VideoSegment> items,
        TimeSpan start, TimeSpan end)
    {
        var result = ImmutableArray.CreateBuilder<VideoSegment>();
        foreach (VideoSegment item in items)
        {
            if (item.End <= start) { result.Add(item); continue; }
            if (item.Start >= end) { result.Add(item with { Start = item.Start - (end - start) }); continue; }
            TimeSpan left = start > item.Start ? start - item.Start : TimeSpan.Zero;
            TimeSpan right = item.End > end ? item.End - end : TimeSpan.Zero;
            if (left > TimeSpan.Zero) { result.Add(item with { SourceOut = item.SourceIn + left }); }
            if (right > TimeSpan.Zero)
            {
                result.Add(item with
                {
                    Id = left > TimeSpan.Zero ? Guid.NewGuid() : item.Id,
                    Start = start,
                    SourceIn = item.SourceOut - right,
                });
            }
        }
        return Sort(result.ToImmutable());
    }

    private static ImmutableArray<AudioSegment> CutAudio(ImmutableArray<AudioSegment> items,
        TimeSpan start, TimeSpan end)
    {
        var result = ImmutableArray.CreateBuilder<AudioSegment>();
        foreach (AudioSegment item in items)
        {
            if (item.End <= start) { result.Add(item); continue; }
            if (item.Start >= end) { result.Add(item with { Start = item.Start - (end - start) }); continue; }
            TimeSpan left = start > item.Start ? start - item.Start : TimeSpan.Zero;
            TimeSpan right = item.End > end ? item.End - end : TimeSpan.Zero;
            if (left > TimeSpan.Zero) { result.Add(item with { SourceOut = item.SourceIn + left }); }
            if (right > TimeSpan.Zero)
            {
                result.Add(item with
                {
                    Id = left > TimeSpan.Zero ? Guid.NewGuid() : item.Id,
                    Start = start,
                    SourceIn = item.SourceOut - right,
                });
            }
        }
        return Sort(result.ToImmutable());
    }

    private static ImmutableArray<ImageOverlay> CutImages(ImmutableArray<ImageOverlay> items,
        TimeSpan start, TimeSpan end)
    {
        var result = ImmutableArray.CreateBuilder<ImageOverlay>();
        foreach (ImageOverlay item in items)
        {
            if (item.End <= start) { result.Add(item); continue; }
            if (item.Start >= end) { result.Add(item with { Start = item.Start - (end - start) }); continue; }
            TimeSpan left = start > item.Start ? start - item.Start : TimeSpan.Zero;
            TimeSpan right = item.End > end ? item.End - end : TimeSpan.Zero;
            if (left > TimeSpan.Zero)
            {
                result.Add(item with { Duration = left, LayerId = LayerKey(item) });
            }
            if (right > TimeSpan.Zero)
            {
                result.Add(item with
                {
                    Id = left > TimeSpan.Zero ? Guid.NewGuid() : item.Id,
                    Start = start,
                    Duration = right,
                    LayerId = left > TimeSpan.Zero ? LayerKey(item) : item.LayerId,
                });
            }
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<AudioSegment> TrimAudioToEnd(ImmutableArray<AudioSegment> items,
        TimeSpan end) =>
        [.. items.Where(item => item.Start < end).Select(item => item.End > end
            ? item with { SourceOut = item.SourceOut - (item.End - end) } : item)];

    private static ImmutableArray<ImageOverlay> TrimImagesToEnd(ImmutableArray<ImageOverlay> items,
        TimeSpan end) =>
        [.. items.Where(item => item.Start < end).Select(item => item.End > end
            ? item with { Duration = end - item.Start } : item)];
}
