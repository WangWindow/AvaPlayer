using System.Collections.ObjectModel;
using AvaPlayer.Models;
using AvaPlayer.Services.Playlist;

namespace AvaPlayer.Application.Tests.Fakes;

/// <summary>
/// Hand-written fake of <see cref="IPlaylistService"/> for unit testing.
/// Manages an in-memory queue and current-track pointer.
/// </summary>
public sealed class FakePlaylistService : IPlaylistService
{
    private readonly List<Track> _tracks = new();
    private readonly Random _random = new();
    private int _currentIndex = -1;

    public ObservableCollection<Track> Queue { get; } = new();
    public Track? CurrentTrack { get; private set; }
    public PlaybackMode PlaybackMode { get; set; }

    /// <summary>
    /// Returns the number of times <see cref="LoadAsync"/> has been called.
    /// </summary>
    public int LoadCallCount { get; private set; }

    /// <summary>
    /// Returns the number of times <see cref="GetNextTrack"/> has been called.
    /// </summary>
    public int NextCallCount { get; private set; }

    /// <summary>
    /// Returns the number of times <see cref="GetPreviousTrack"/> has been called.
    /// </summary>
    public int PreviousCallCount { get; private set; }

    /// <summary>
    /// Optional override for <see cref="GetNextTrack"/> return value.
    /// When null, the fake advances to the next track in the list.
    /// </summary>
    public Track? NextTrackOverride { get; set; }

    /// <summary>
    /// Optional override for <see cref="GetPreviousTrack"/> return value.
    /// When null, the fake moves to the previous track in the list.
    /// </summary>
    public Track? PreviousTrackOverride { get; set; }

    public FakePlaylistService()
    {
        PlaybackMode = PlaybackMode.Sequential;
    }

    public void AddTrack(Track track)
    {
        _tracks.Add(track);
        Queue.Add(track);
    }

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        LoadCallCount++;
        return Task.CompletedTask;
    }

    public Task AddFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task RemoveTracksAsync(IEnumerable<Track> tracks, CancellationToken cancellationToken = default)
    {
        foreach (var t in tracks.ToList())
        {
            _tracks.Remove(t);
            Queue.Remove(t);
        }
        return Task.CompletedTask;
    }

    public void SetCurrentTrack(Track track)
    {
        CurrentTrack = track;
        _currentIndex = _tracks.IndexOf(track);
    }

    public Track? GetNextTrack()
    {
        NextCallCount++;

        if (NextTrackOverride is not null)
            return NextTrackOverride;

        if (_tracks.Count == 0)
            return null;

        if (CurrentTrack is null)
            return _tracks[0];

        return PlaybackMode switch
        {
            PlaybackMode.RepeatOne => CurrentTrack,
            PlaybackMode.Shuffle => GetShuffleTrack(),
            PlaybackMode.RepeatAll => GetWrappedTrack(1),
            PlaybackMode.Sequential => GetSequentialTrack(1),
            _ => GetSequentialTrack(1)
        };
    }

    public Track? GetPreviousTrack()
    {
        PreviousCallCount++;

        if (PreviousTrackOverride is not null)
            return PreviousTrackOverride;

        if (_tracks.Count == 0)
            return null;

        if (CurrentTrack is null)
            return _tracks[0];

        return PlaybackMode switch
        {
            PlaybackMode.RepeatOne => CurrentTrack,
            PlaybackMode.Shuffle => GetShuffleTrack(),
            PlaybackMode.RepeatAll => GetWrappedTrack(-1),
            PlaybackMode.Sequential => GetSequentialTrack(-1),
            _ => GetSequentialTrack(-1)
        };
    }

    private Track? GetSequentialTrack(int delta)
    {
        var index = _tracks.IndexOf(CurrentTrack!);
        if (index < 0)
            return _tracks.Count > 0 ? _tracks[0] : null;

        var nextIndex = index + delta;
        return nextIndex >= 0 && nextIndex < _tracks.Count
            ? _tracks[nextIndex]
            : null;
    }

    private Track GetWrappedTrack(int delta)
    {
        var index = _tracks.IndexOf(CurrentTrack!);
        var nextIndex = (index + delta) % _tracks.Count;
        if (nextIndex < 0)
            nextIndex += _tracks.Count;
        return _tracks[nextIndex];
    }

    private Track GetShuffleTrack()
    {
        if (_tracks.Count == 1)
            return CurrentTrack!;

        Track candidate;
        do
        {
            candidate = _tracks[_random.Next(_tracks.Count)];
        } while (candidate.Id == CurrentTrack!.Id);

        return candidate;
    }
}
