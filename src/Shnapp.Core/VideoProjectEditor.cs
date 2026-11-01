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

    /// <summary>Adds imported video as one independent visual layer without rippling other clips.</summary>
    /// <remarks>Copied audio stems start relative to the video and are added in the same undo step.</remarks>
    public Guid AddVideoOverlayAsset(VideoAsset asset, TimeSpan at, VideoRect bounds,
        IReadOnlyList<(VideoAsset Asset, AudioSegment Segment)>? audioStems = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Kind != VideoAssetKind.ImportedVideo)
            throw new ArgumentException("The overlay asset must be an imported video.", nameof(asset));
        var overlay = new VideoOverlay(Guid.NewGuid(), asset.Id, at, TimeSpan.Zero,
            asset.Duration, bounds);
        overlay = overlay with { LayerId = overlay.Id };
        var assets = Current.Assets.Add(asset);
        var audio = Current.AudioSegments;
        if (audioStems is not null)
        {
            foreach ((VideoAsset audioAsset, AudioSegment segment) in audioStems)
            {
                if (audioAsset.Kind != VideoAssetKind.ImportedAudio ||
                    segment.AssetId != audioAsset.Id || segment.Start < TimeSpan.Zero ||
                    segment.End > asset.Duration)
                    throw new ArgumentException("Audio stems must fit the imported video.",
                        nameof(audioStems));
                assets = assets.Add(audioAsset);
                audio = audio.Add(segment with { Start = at + segment.Start });
            }
        }
        Commit(Current with
        {
            Assets = assets,
            VideoOverlays = Current.VideoOverlays.Add(overlay),
            AudioSegments = Sort(audio),
        });
        return overlay.Id;
    }

    /// <summary>Adds a clip from an existing video asset on a separate visual layer.</summary>
    public Guid AddVideoOverlay(Guid assetId, TimeSpan start, TimeSpan sourceIn,
        TimeSpan sourceOut, VideoRect bounds, int zIndex = 0)
    {
        var overlay = new VideoOverlay(Guid.NewGuid(), assetId, start, sourceIn,
            sourceOut, bounds, zIndex);
        overlay = overlay with { LayerId = overlay.Id };
        Commit(Current with { VideoOverlays = Current.VideoOverlays.Add(overlay) });
        return overlay.Id;
    }

    /// <summary>Replaces an overlay video's timing, geometry, crop, or layer.</summary>
    public bool UpdateVideoOverlay(VideoOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        int index = Find(Current.VideoOverlays, overlay.Id);
        if (index < 0) return false;
        overlay = overlay with { LayerId = LayerKey(overlay) };
        if (Current.VideoOverlays[index] == overlay) return false;
        Commit(Current with { VideoOverlays = Sort(Current.VideoOverlays.SetItem(index, overlay)) });
        return true;
    }

    public bool MoveVideoOverlay(Guid id, TimeSpan start)
    {
        int index = Find(Current.VideoOverlays, id);
        return index >= 0 &&
            UpdateVideoOverlay(Current.VideoOverlays[index] with { Start = start });
    }

    public bool TrimVideoOverlay(Guid id, TimeSpan sourceIn, TimeSpan sourceOut)
    {
        int index = Find(Current.VideoOverlays, id);
        return index >= 0 && UpdateVideoOverlay(Current.VideoOverlays[index] with
        {
            SourceIn = sourceIn,
            SourceOut = sourceOut,
        });
    }

    public Guid SplitVideoOverlay(Guid id, TimeSpan at)
    {
        int index = Find(Current.VideoOverlays, id);
        if (index < 0) throw new ArgumentException("Video overlay was not found.", nameof(id));
        VideoOverlay original = Current.VideoOverlays[index];
        if (at <= original.Start || at >= original.End)
            throw new ArgumentOutOfRangeException(nameof(at), "Split time must be inside the clip.");
        var left = original with
        {
            SourceOut = original.SourceIn + (at - original.Start),
            LayerId = LayerKey(original),
        };
        var right = original with
        {
            Id = Guid.NewGuid(),
            Start = at,
            SourceIn = left.SourceOut,
            LayerId = LayerKey(original),
        };
        Commit(Current with
        {
            VideoOverlays = Sort(Current.VideoOverlays.SetItem(index, left).Add(right)),
        });
        return right.Id;
    }

    public bool DeleteVideoOverlay(Guid id)
    {
        int index = Find(Current.VideoOverlays, id);
        if (index < 0) return false;
        Commit(Current with { VideoOverlays = Current.VideoOverlays.RemoveAt(index) });
        return true;
    }

    /// <summary>Moves an entire video overlay row forward or backward in the visual stack.</summary>
    public bool MoveVideoOverlayLayer(Guid id, int direction)
    {
        if (direction is not 1 and not -1)
            throw new ArgumentOutOfRangeException(nameof(direction));
        VideoOverlay? selected = Current.VideoOverlays.FirstOrDefault(item => item.Id == id);
        if (selected is null) return false;
        var layers = Current.VideoOverlays.Select((item, index) => (item, index))
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
            VideoOverlays = Sort([.. Current.VideoOverlays.Select(item => item with
            {
                ZIndex = order[LayerKey(item)],
            })]),
        });
        return true;
    }

    /// <summary>Removes a marked interval from one video overlay without rippling other lanes.</summary>
    public Guid? CutVideoOverlayRange(Guid id, TimeSpan start, TimeSpan end)
    {
        int index = Find(Current.VideoOverlays, id);
        if (index < 0)
            throw new ArgumentException("Video overlay was not found.", nameof(id));
        VideoOverlay item = Current.VideoOverlays[index];
        if (start < TimeSpan.Zero || end <= start || end > Current.Duration ||
            start >= item.End || end <= item.Start)
            throw new ArgumentOutOfRangeException(nameof(start),
                "Cut range must overlap the clip inside the project timeline.");
        TimeSpan cutStart = start > item.Start ? start : item.Start;
        TimeSpan cutEnd = end < item.End ? end : item.End;
        ImmutableArray<VideoOverlay> overlays = Current.VideoOverlays;
        Guid? selected = id;
        if (cutStart == item.Start && cutEnd == item.End)
        {
            overlays = overlays.RemoveAt(index);
            selected = null;
        }
        else if (cutStart == item.Start)
            overlays = overlays.SetItem(index, item with
            {
                Start = cutEnd,
                SourceIn = item.SourceIn + (cutEnd - item.Start),
                LayerId = LayerKey(item),
            });
        else
        {
            overlays = overlays.SetItem(index, item with
            {
                SourceOut = item.SourceIn + (cutStart - item.Start),
                LayerId = LayerKey(item),
            });
            if (cutEnd < item.End)
            {
                var right = item with
                {
                    Id = Guid.NewGuid(),
                    Start = cutEnd,
                    SourceIn = item.SourceIn + (cutEnd - item.Start),
                    LayerId = LayerKey(item),
                };
                overlays = overlays.Insert(index + 1, right);
                selected = right.Id;
            }
        }
        Commit(Current with { VideoOverlays = Sort(overlays) });
        return selected;
    }

    /// <summary>Shows or hides a visual track; Guid.Empty is the original recording lane.</summary>
    public bool SetLayerVisible(Guid layerId, bool visible)
    {
        if (layerId != Guid.Empty &&
            !Current.VideoOverlays.Any(item => LayerKey(item) == layerId) &&
            !Current.ImageOverlays.Any(item => LayerKey(item) == layerId) &&
            !Current.TextOverlays.Any(item => LayerKey(item) == layerId))
            return false;
        bool hidden = Current.HiddenLayerIds.Contains(layerId);
        if (hidden == !visible) return false;
        Commit(Current with
        {
            HiddenLayerIds = visible
                ? Current.HiddenLayerIds.Remove(layerId)
                : Current.HiddenLayerIds.Add(layerId),
        });
        return true;
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
            video.Add(segment with
            {
                SourceOut = segment.SourceIn + leftDuration,
                FadeIn = Min(segment.FadeIn, leftDuration),
                FadeOut = TimeSpan.Zero,
            });
            video.Add(segment with
            {
                Id = Guid.NewGuid(),
                Start = at + asset.Duration,
                SourceIn = segment.SourceIn + leftDuration,
                FadeIn = TimeSpan.Zero,
                FadeOut = Min(segment.FadeOut, segment.End - at),
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

        var texts = ImmutableArray.CreateBuilder<TextOverlay>();
        foreach (TextOverlay overlay in Current.TextOverlays)
        {
            if (overlay.End <= at) { texts.Add(overlay); continue; }
            if (overlay.Start >= at)
            {
                texts.Add(overlay with { Start = overlay.Start + asset.Duration });
                continue;
            }
            texts.Add(overlay with
            {
                Duration = at - overlay.Start,
                LayerId = LayerKey(overlay),
            });
            texts.Add(overlay with
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
            TextOverlays = texts.ToImmutable(),
            VideoOverlays = RippleInsertVideoOverlays(Current.VideoOverlays, at,
                asset.Duration),
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

    /// <summary>Adds an editable, timed text layer above the video.</summary>
    public void AddTextOverlay(TextOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        Commit(Current with
        {
            TextOverlays = Current.TextOverlays.Add(overlay with
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
        if (index < 0) return false;
        VideoSegment fitted = FitFades(segment);
        if (Current.VideoSegments[index] == fitted) return false;
        Commit(Current with { VideoSegments = Sort(Current.VideoSegments.SetItem(index, fitted)) });
        return true;
    }

    /// <summary>Sets clip-edge fades, in seconds of the visible source interval.</summary>
    public bool SetVideoFades(Guid id, TimeSpan fadeIn, TimeSpan fadeOut)
    {
        int index = Find(Current.VideoSegments, id);
        if (index < 0) return false;
        VideoSegment segment = Current.VideoSegments[index];
        if (segment.FadeIn == fadeIn && segment.FadeOut == fadeOut) return false;
        Commit(Current with
        {
            VideoSegments = Current.VideoSegments.SetItem(index, segment with
            {
                FadeIn = fadeIn,
                FadeOut = fadeOut,
            }),
        });
        return true;
    }

    /// <summary>Applies a dip to black at a touching cut in one undoable edit.</summary>
    /// <remarks>The outgoing fade-out and incoming fade-in are replaced; other fades remain.</remarks>
    public bool ApplyDipToBlackAtCut(Guid outgoingSegmentId, TimeSpan durationPerSide)
    {
        if (durationPerSide <= TimeSpan.Zero || durationPerSide > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(durationPerSide));
        int outgoingIndex = Find(Current.VideoSegments, outgoingSegmentId);
        if (outgoingIndex < 0) return false;
        VideoSegment outgoing = Current.VideoSegments[outgoingIndex];
        int incomingIndex = Find(Current.VideoSegments, outgoing.End);
        if (incomingIndex < 0 || incomingIndex == outgoingIndex) return false;
        VideoSegment incoming = Current.VideoSegments[incomingIndex];
        TimeSpan half = Min(durationPerSide,
            Min(outgoing.Duration - outgoing.FadeIn,
                incoming.Duration - incoming.FadeOut));
        if (half <= TimeSpan.Zero || Current.Transitions.Any(item =>
                item.Lane == VideoTransitionLane.Video &&
                item.OutgoingId == outgoing.Id && item.IncomingId == incoming.Id)) return false;
        AddTransition(VideoTransitionLane.Video, outgoing.Id, incoming.Id,
            VideoTransitionEffect.DipToBlack, half * 2);
        return true;
    }

    /// <summary>Adds one transition centered on a touching cut as an undoable edit.</summary>
    public Guid AddTransition(VideoTransitionLane lane, Guid outgoingId, Guid incomingId,
        VideoTransitionEffect effect, TimeSpan duration)
    {
        var transition = new VideoTransition(Guid.NewGuid(), lane, outgoingId,
            incomingId, effect, duration);
        VideoProject next = Current with { Transitions = Current.Transitions.Add(transition) };
        if (lane == VideoTransitionLane.Video)
        {
            int outgoingIndex = Find(next.VideoSegments, outgoingId);
            int incomingIndex = Find(next.VideoSegments, incomingId);
            if (outgoingIndex >= 0 && incomingIndex >= 0)
                next = next with
                {
                    VideoSegments = next.VideoSegments
                        .SetItem(outgoingIndex,
                            next.VideoSegments[outgoingIndex] with { FadeOut = TimeSpan.Zero })
                        .SetItem(incomingIndex,
                            next.VideoSegments[incomingIndex] with { FadeIn = TimeSpan.Zero }),
                };
        }
        VideoProjectValidation.Validate(next);
        Commit(next);
        return transition.Id;
    }

    /// <summary>Changes a transition preset or duration without changing its identity.</summary>
    public bool UpdateTransition(VideoTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        int index = Find(Current.Transitions, transition.Id);
        if (index < 0 || Current.Transitions[index] == transition) return false;
        VideoProject next = Current with
        {
            Transitions = Current.Transitions.SetItem(index, transition),
        };
        VideoProjectValidation.Validate(next);
        Commit(next);
        return true;
    }

    /// <summary>Removes a transition at a cut without moving either clip.</summary>
    public bool DeleteTransition(Guid id)
    {
        int index = Find(Current.Transitions, id);
        if (index < 0) return false;
        Commit(Current with { Transitions = Current.Transitions.RemoveAt(index) });
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
        var left = original with
        {
            SourceOut = original.SourceIn + leftDuration,
            FadeIn = Min(original.FadeIn, leftDuration),
            FadeOut = TimeSpan.Zero,
        };
        var right = original with
        {
            Id = Guid.NewGuid(),
            Start = at,
            SourceIn = left.SourceOut,
            FadeIn = TimeSpan.Zero,
            FadeOut = Min(original.FadeOut, original.End - at),
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
        Commit(Current with { VideoSegments = video });
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

    /// <summary>Mutes every split piece of one audio asset in a single undo step.</summary>
    public bool SetAudioTrackMuted(Guid assetId, bool muted)
    {
        if (!Current.AudioSegments.Any(item => item.AssetId == assetId &&
            item.Muted != muted)) return false;
        Commit(Current with
        {
            AudioSegments = [.. Current.AudioSegments.Select(item =>
                item.AssetId == assetId ? item with { Muted = muted } : item)],
        });
        return true;
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

    /// <summary>Replaces an existing text layer's content, timing, bounds, or style.</summary>
    public bool UpdateTextOverlay(TextOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        int index = Find(Current.TextOverlays, overlay.Id);
        if (index < 0 || Current.TextOverlays[index] == overlay) return false;
        Commit(Current with { TextOverlays = Current.TextOverlays.SetItem(index, overlay) });
        return true;
    }

    /// <summary>Deletes one timed text layer without changing other timeline items.</summary>
    public bool DeleteTextOverlay(Guid id)
    {
        int index = Find(Current.TextOverlays, id);
        if (index < 0) return false;
        Commit(Current with { TextOverlays = Current.TextOverlays.RemoveAt(index) });
        return true;
    }

    /// <summary>Splits one text layer at the playhead as one undoable edit.</summary>
    public Guid SplitTextOverlay(Guid id, TimeSpan at)
    {
        int index = Find(Current.TextOverlays, id);
        if (index < 0) throw new ArgumentException("Text overlay was not found.", nameof(id));
        TextOverlay text = Current.TextOverlays[index];
        if (at <= text.Start || at >= text.End)
            throw new ArgumentOutOfRangeException(nameof(at), "Split time must be inside the selected text.");
        var right = text with
        {
            Id = Guid.NewGuid(),
            Start = at,
            Duration = text.End - at,
            LayerId = LayerKey(text),
        };
        Commit(Current with
        {
            TextOverlays = Current.TextOverlays
                .SetItem(index, text with { Duration = at - text.Start, LayerId = LayerKey(text) })
                .Insert(index + 1, right),
        });
        return right.Id;
    }

    /// <summary>Removes a marked interval from one text layer without changing video time.</summary>
    public Guid? CutTextRange(Guid id, TimeSpan start, TimeSpan end)
    {
        int index = Find(Current.TextOverlays, id);
        if (index < 0) throw new ArgumentException("Text overlay was not found.", nameof(id));
        TextOverlay text = Current.TextOverlays[index];
        if (start < TimeSpan.Zero || end <= start || end > Current.Duration ||
            start >= text.End || end <= text.Start)
            throw new ArgumentOutOfRangeException(nameof(start),
                "Cut range must overlap the selected text inside the video timeline.");

        TimeSpan cutStart = start > text.Start ? start : text.Start;
        TimeSpan cutEnd = end < text.End ? end : text.End;
        ImmutableArray<TextOverlay> texts = Current.TextOverlays;
        Guid? selected = id;
        if (cutStart == text.Start && cutEnd == text.End)
        {
            texts = texts.RemoveAt(index);
            selected = null;
        }
        else if (cutStart == text.Start)
        {
            texts = texts.SetItem(index, text with
            {
                Start = cutEnd,
                Duration = text.End - cutEnd,
                LayerId = LayerKey(text),
            });
        }
        else
        {
            texts = texts.SetItem(index, text with
            {
                Duration = cutStart - text.Start,
                LayerId = LayerKey(text),
            });
            if (cutEnd < text.End)
            {
                var right = text with
                {
                    Id = Guid.NewGuid(),
                    Start = cutEnd,
                    Duration = text.End - cutEnd,
                    LayerId = LayerKey(text),
                };
                texts = texts.Insert(index + 1, right);
                selected = right.Id;
            }
        }
        Commit(Current with { TextOverlays = texts });
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
            TextOverlays = CutTexts(Current.TextOverlays, start, end),
            VideoOverlays = CutVideoOverlays(Current.VideoOverlays, start, end),
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
        next = PruneHiddenLayers(next);
        next = PruneTransitions(next);
        VideoProjectValidation.Validate(next);
        if (next.Title == Current.Title && next.Assets.SequenceEqual(Current.Assets) &&
            next.VideoSegments.SequenceEqual(Current.VideoSegments) &&
            next.VideoOverlays.SequenceEqual(Current.VideoOverlays) &&
            next.AudioSegments.SequenceEqual(Current.AudioSegments) &&
            next.ImageOverlays.SequenceEqual(Current.ImageOverlays) &&
            next.TextOverlays.SequenceEqual(Current.TextOverlays) &&
            next.Transitions.SequenceEqual(Current.Transitions) &&
            next.HiddenLayerIds.SequenceEqual(Current.HiddenLayerIds))
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

    private static VideoProject PruneHiddenLayers(VideoProject project)
    {
        if (project.HiddenLayerIds.IsDefaultOrEmpty) return project;
        var present = new HashSet<Guid> { Guid.Empty };
        foreach (VideoOverlay item in project.VideoOverlays) present.Add(LayerKey(item));
        foreach (ImageOverlay item in project.ImageOverlays) present.Add(LayerKey(item));
        foreach (TextOverlay item in project.TextOverlays) present.Add(LayerKey(item));
        ImmutableArray<Guid> hidden = [.. project.HiddenLayerIds.Where(present.Contains).Distinct()];
        return hidden.SequenceEqual(project.HiddenLayerIds)
            ? project : project with { HiddenLayerIds = hidden };
    }

    private static int Find(ImmutableArray<VideoSegment> items, Guid id)
    {
        for (int i = 0; i < items.Length; i++) { if (items[i].Id == id) { return i; } }
        return -1;
    }

    private static int Find(ImmutableArray<VideoOverlay> items, Guid id)
    {
        for (int i = 0; i < items.Length; i++) { if (items[i].Id == id) return i; }
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

    private static int Find(ImmutableArray<TextOverlay> items, Guid id)
    {
        for (int i = 0; i < items.Length; i++) { if (items[i].Id == id) return i; }
        return -1;
    }

    private static int Find(ImmutableArray<VideoTransition> items, Guid id)
    {
        for (int i = 0; i < items.Length; i++) { if (items[i].Id == id) return i; }
        return -1;
    }

    private static VideoProject PruneTransitions(VideoProject next)
    {
        if (next.Transitions.IsDefaultOrEmpty) return next;
        var kept = ImmutableArray.CreateBuilder<VideoTransition>();
        var outgoing = new HashSet<(VideoTransitionLane Lane, Guid Id)>();
        var incoming = new HashSet<(VideoTransitionLane Lane, Guid Id)>();
        var occupied = new Dictionary<Guid, TimeSpan>();
        foreach (VideoTransition item in next.Transitions)
        {
            if (item is null ||
                !VideoTransitionGeometry.TryGetSpan(next, item, out VideoTransitionSpan span) ||
                outgoing.Contains((item.Lane, item.OutgoingId)) ||
                incoming.Contains((item.Lane, item.IncomingId))) continue;
            TimeSpan outgoingHalf = span.Cut - span.Start;
            TimeSpan incomingHalf = span.End - span.Cut;
            TimeSpan outgoingDuration = ItemDuration(next, item.Lane, item.OutgoingId);
            TimeSpan incomingDuration = ItemDuration(next, item.Lane, item.IncomingId);
            if (occupied.GetValueOrDefault(item.OutgoingId) + outgoingHalf > outgoingDuration ||
                occupied.GetValueOrDefault(item.IncomingId) + incomingHalf > incomingDuration)
                continue;
            occupied[item.OutgoingId] = occupied.GetValueOrDefault(item.OutgoingId) + outgoingHalf;
            occupied[item.IncomingId] = occupied.GetValueOrDefault(item.IncomingId) + incomingHalf;
            outgoing.Add((item.Lane, item.OutgoingId));
            incoming.Add((item.Lane, item.IncomingId));
            kept.Add(item);
        }
        return kept.Count == next.Transitions.Length ? next : next with
        {
            Transitions = kept.ToImmutable(),
        };
    }

    private static TimeSpan ItemDuration(VideoProject project, VideoTransitionLane lane, Guid id) =>
        lane switch
        {
            VideoTransitionLane.Video => project.VideoSegments.First(item => item.Id == id).Duration,
            VideoTransitionLane.VideoOverlay => project.VideoOverlays.First(item => item.Id == id).Duration,
            VideoTransitionLane.Image => project.ImageOverlays.First(item => item.Id == id).Duration,
            VideoTransitionLane.Text => project.TextOverlays.First(item => item.Id == id).Duration,
            _ => TimeSpan.Zero,
        };

    private static int Find(ImmutableArray<VideoSegment> items, TimeSpan start)
    {
        for (int i = 0; i < items.Length; i++) { if (items[i].Start == start) return i; }
        return -1;
    }

    private static Guid LayerKey(ImageOverlay overlay) =>
        overlay.LayerId == Guid.Empty ? overlay.Id : overlay.LayerId;

    private static Guid LayerKey(VideoOverlay overlay) =>
        overlay.LayerId == Guid.Empty ? overlay.Id : overlay.LayerId;

    private static Guid LayerKey(TextOverlay overlay) =>
        overlay.LayerId == Guid.Empty ? overlay.Id : overlay.LayerId;

    private static VideoSegment FitFades(VideoSegment segment)
    {
        TimeSpan fadeIn = Min(segment.FadeIn, segment.Duration);
        return segment with
        {
            FadeIn = fadeIn,
            FadeOut = Min(segment.FadeOut, segment.Duration - fadeIn),
        };
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static ImmutableArray<VideoSegment> Sort(ImmutableArray<VideoSegment> items) =>
        [.. items.OrderBy(static item => item.Start).ThenBy(static item => item.Id)];

    private static ImmutableArray<VideoOverlay> Sort(ImmutableArray<VideoOverlay> items) =>
        [.. items.OrderBy(static item => item.ZIndex).ThenBy(static item => item.Start)
            .ThenBy(static item => item.Id)];

    private static ImmutableArray<AudioSegment> Sort(ImmutableArray<AudioSegment> items) =>
        [.. items.OrderBy(static item => item.Start).ThenBy(static item => item.Id)];

    private static ImmutableArray<VideoOverlay> RippleInsertVideoOverlays(
        ImmutableArray<VideoOverlay> items, TimeSpan at, TimeSpan duration)
    {
        var result = ImmutableArray.CreateBuilder<VideoOverlay>();
        foreach (VideoOverlay item in items)
        {
            if (item.End <= at) { result.Add(item); continue; }
            if (item.Start >= at)
            {
                result.Add(item with { Start = item.Start + duration });
                continue;
            }
            TimeSpan left = at - item.Start;
            result.Add(item with
            {
                SourceOut = item.SourceIn + left,
                LayerId = LayerKey(item),
            });
            result.Add(item with
            {
                Id = Guid.NewGuid(),
                Start = at + duration,
                SourceIn = item.SourceIn + left,
                LayerId = LayerKey(item),
            });
        }
        return Sort(result.ToImmutable());
    }

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
            if (left > TimeSpan.Zero)
            {
                result.Add(item with
                {
                    SourceOut = item.SourceIn + left,
                    FadeIn = Min(item.FadeIn, left),
                    FadeOut = TimeSpan.Zero,
                });
            }
            if (right > TimeSpan.Zero)
            {
                result.Add(item with
                {
                    Id = left > TimeSpan.Zero ? Guid.NewGuid() : item.Id,
                    Start = start,
                    SourceIn = item.SourceOut - right,
                    FadeIn = TimeSpan.Zero,
                    FadeOut = Min(item.FadeOut, right),
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

    private static ImmutableArray<VideoOverlay> CutVideoOverlays(
        ImmutableArray<VideoOverlay> items, TimeSpan start, TimeSpan end)
    {
        var result = ImmutableArray.CreateBuilder<VideoOverlay>();
        foreach (VideoOverlay item in items)
        {
            if (item.End <= start) { result.Add(item); continue; }
            if (item.Start >= end)
            {
                result.Add(item with { Start = item.Start - (end - start) });
                continue;
            }
            TimeSpan left = start > item.Start ? start - item.Start : TimeSpan.Zero;
            TimeSpan right = item.End > end ? item.End - end : TimeSpan.Zero;
            if (left > TimeSpan.Zero)
                result.Add(item with
                {
                    SourceOut = item.SourceIn + left,
                    LayerId = LayerKey(item),
                });
            if (right > TimeSpan.Zero)
                result.Add(item with
                {
                    Id = left > TimeSpan.Zero ? Guid.NewGuid() : item.Id,
                    Start = start,
                    SourceIn = item.SourceOut - right,
                    LayerId = LayerKey(item),
                });
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

    private static ImmutableArray<TextOverlay> CutTexts(ImmutableArray<TextOverlay> items,
        TimeSpan start, TimeSpan end)
    {
        var result = ImmutableArray.CreateBuilder<TextOverlay>();
        foreach (TextOverlay item in items)
        {
            if (item.End <= start) { result.Add(item); continue; }
            if (item.Start >= end) { result.Add(item with { Start = item.Start - (end - start) }); continue; }
            TimeSpan left = start > item.Start ? start - item.Start : TimeSpan.Zero;
            TimeSpan right = item.End > end ? item.End - end : TimeSpan.Zero;
            if (left > TimeSpan.Zero)
                result.Add(item with { Duration = left, LayerId = LayerKey(item) });
            if (right > TimeSpan.Zero)
            {
                result.Add(item with
                {
                    Id = left > TimeSpan.Zero ? Guid.NewGuid() : item.Id,
                    Start = start,
                    Duration = right,
                    LayerId = LayerKey(item),
                });
            }
        }
        return result.ToImmutable();
    }

}
