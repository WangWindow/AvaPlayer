using AvaPlayer.Helpers;
using AvaPlayer.Models;
using AvaPlayer.Services.Database;
using AvaPlayer.Services.Playlist;
using AvaPlayer.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvaPlayer.Tests;

public sealed class LibraryRescanTests : IDisposable
{
    private readonly TestDirectory _directory = new();
    private readonly SqliteDatabaseService _database;
    private readonly PlaylistService _library;

    public LibraryRescanTests()
    {
        _database = new SqliteDatabaseService(_directory.FilePath("library.db"));
        _database.InitializeAsync().GetAwaiter().GetResult();
        _library = new PlaylistService(_database, new TrackScannerService(NullLogger<TrackScannerService>.Instance));
    }

    [Fact]
    public async Task Parent_and_child_playlists_keep_independent_membership()
    {
        _directory.CreateWave("child/song.wav");
        await _library.AddPlaylistAsync("Parent", _directory.Root);
        var parentId = _library.SelectedPlaylist!.Id;
        await _library.AddPlaylistAsync("Child", _directory.FilePath("child"));
        var childId = _library.SelectedPlaylist!.Id;
        Assert.Single(await _database.GetTracksAsync(parentId));
        Assert.Single(await _database.GetTracksAsync(childId));

        await _library.RemoveTracksAsync(_library.Queue.ToArray());
        Assert.Empty(await _database.GetTracksAsync(childId));
        Assert.Single(await _database.GetTracksAsync(parentId));
    }

    [Fact]
    public async Task Rescan_reconciles_additions_deletions_and_keeps_current_track()
    {
        var currentPath = _directory.CreateWave("a.wav");
        var removed = _directory.CreateWave("removed.wav");
        await _library.AddPlaylistAsync("My playlist", _directory.Root);
        var playlist = _library.SelectedPlaylist!;
        _library.SetCurrentTrack(_library.Queue.Single(t => t.FilePath == currentPath));
        File.Delete(removed);
        var added = _directory.CreateWave("b.wav");

        await _library.RescanPlaylistAsync(playlist.Id, true);

        Assert.Equal("My playlist", _library.SelectedPlaylist!.Name);
        Assert.Equal(2, _library.Queue.Count);
        Assert.Contains(_library.Queue, t => t.FilePath == added);
        Assert.Equal(currentPath, _library.CurrentTrack!.FilePath);
        Assert.NotNull(_library.GetNextTrack());
    }

    [Fact]
    public async Task Recursive_option_is_persisted_and_repeat_import_keeps_name()
    {
        _directory.CreateWave("top.wav");
        _directory.CreateWave("child/song.wav");
        await _library.AddPlaylistAsync("Custom name", _directory.Root);
        var playlist = _library.SelectedPlaylist!;
        await _library.RescanPlaylistAsync(playlist.Id, false);
        Assert.Single(_library.Queue);
        Assert.False(_library.SelectedPlaylist!.IncludeSubfolders);

        await _library.AddPlaylistAsync("", Path.Combine(_directory.Root, ".") + Path.DirectorySeparatorChar);
        Assert.Single(_library.Playlists);
        Assert.Equal("Custom name", _library.SelectedPlaylist!.Name);
        Assert.Single(_library.Queue);
        var reloaded = new SqliteDatabaseService(_database.DatabasePath);
        await reloaded.InitializeAsync();
        Assert.False(Assert.Single(await reloaded.GetPlaylistsAsync()).IncludeSubfolders);
    }

    [Fact]
    public async Task Missing_folder_and_cancellation_preserve_existing_index()
    {
        _directory.CreateWave();
        await _library.AddPlaylistAsync("Music", _directory.Root);
        var playlist = _library.SelectedPlaylist!;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _library.RescanPlaylistAsync(playlist.Id, false, cancelled.Token));
        Assert.Single(await _database.GetTracksAsync(playlist.Id));
        Assert.True(Assert.Single(await _database.GetPlaylistsAsync()).IncludeSubfolders);
    }

    [Fact]
    public async Task Reconciliation_failure_rolls_back_deleted_rows_and_options()
    {
        var path = _directory.CreateWave();
        await _library.AddPlaylistAsync("Music", _directory.Root);
        var playlist = _library.SelectedPlaylist!;
        // Duplicate identities violate the compound primary key during insertion.
        var invalid = new[]
        {
            new Track { Id = "same", FilePath = path, PlaylistId = playlist.Id },
            new Track { Id = "same", FilePath = path + "other", PlaylistId = playlist.Id }
        };
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => _database.ReplacePlaylistTracksAsync(playlist.Id, invalid, false));
        Assert.Single(await _database.GetTracksAsync(playlist.Id));
        Assert.True(Assert.Single(await _database.GetPlaylistsAsync()).IncludeSubfolders);
    }

    [Fact]
    public async Task Case_distinct_paths_remain_distinct_on_linux()
    {
        if (!OperatingSystem.IsLinux()) return;
        _directory.CreateWave("a.wav");
        _directory.CreateWave("A.wav");
        await _library.AddPlaylistAsync("Music", _directory.Root);
        Assert.Equal(2, _library.Queue.Count);
        Assert.NotEqual(StableId.ForPath(_directory.FilePath("a.wav")), StableId.ForPath(_directory.FilePath("A.wav")));
    }

    [Fact]
    public async Task Browsing_another_playlist_does_not_replace_the_playback_queue()
    {
        var firstPath = _directory.CreateWave("one/a.wav");
        var secondPath = _directory.CreateWave("one/b.wav");
        _directory.CreateWave("two/c.wav");
        _directory.CreateWave("two/d.wav");
        await _library.AddPlaylistAsync("One", _directory.FilePath("one"));
        var playing = _library.Queue.Single(t => t.FilePath == firstPath);
        _library.SetCurrentTrack(playing);
        await _library.AddPlaylistAsync("Two", _directory.FilePath("two"));
        Assert.Equal("Two", _library.SelectedPlaylist!.Name);
        Assert.Equal(firstPath, _library.CurrentTrack!.FilePath);
        Assert.Equal(secondPath, _library.GetNextTrack()!.FilePath);

        _library.SetCurrentTrack(_library.Queue[0]);
        Assert.Equal(_library.Queue[1].FilePath, _library.GetNextTrack()!.FilePath);
    }

    [Fact]
    public async Task Playback_restore_uses_the_playing_playlist_after_browsing_another()
    {
        var firstPath = _directory.CreateWave("one/a.wav");
        var secondPath = _directory.CreateWave("one/b.wav");
        _directory.CreateWave("two/c.wav");
        await _library.AddPlaylistAsync("One", _directory.FilePath("one"));
        var settings = new AvaPlayer.Services.Settings.AppSettingsService(_database);
        using (var session = new AvaPlayer.Services.PlaybackSession.PlaybackSession(new Fakes.FakePlayerService(),
            _library, settings, new Fakes.FakePlaybackPositionMemoryService(),
            NullLogger<AvaPlayer.Services.PlaybackSession.PlaybackSession>.Instance))
        {
            await session.PlayTrackAsync(_library.Queue.Single(t => t.FilePath == firstPath));
            await _library.AddPlaylistAsync("Two", _directory.FilePath("two"));
            await session.PersistAsync();
        }
        var restored = new PlaylistService(_database, new TrackScannerService(NullLogger<TrackScannerService>.Instance));
        await restored.LoadAsync();
        Assert.Equal("Two", restored.SelectedPlaylist!.Name);
        using var restoredSession = new AvaPlayer.Services.PlaybackSession.PlaybackSession(new Fakes.FakePlayerService(),
            restored, settings, new Fakes.FakePlaybackPositionMemoryService(),
            NullLogger<AvaPlayer.Services.PlaybackSession.PlaybackSession>.Instance);
        await restoredSession.RestoreTrackAsync();
        Assert.Equal(firstPath, restored.CurrentTrack!.FilePath);
        Assert.Equal(secondPath, restored.GetNextTrack()!.FilePath);
    }

    [Fact]
    public async Task Initial_import_honors_the_subfolder_option()
    {
        _directory.CreateWave("top.wav");
        _directory.CreateWave("child/song.wav");
        await _library.AddPlaylistAsync("Music", _directory.Root, includeSubfolders: false);
        Assert.Single(_library.Queue);
        Assert.False(_library.SelectedPlaylist!.IncludeSubfolders);
    }

    [Fact]
    public async Task Shuffle_after_removing_the_current_file_uses_the_remaining_member()
    {
        var first = _directory.CreateWave("a.wav");
        var remaining = _directory.CreateWave("b.wav");
        await _library.AddPlaylistAsync("Music", _directory.Root);
        _library.SetCurrentTrack(_library.Queue.Single(t => t.FilePath == first));
        _library.PlaybackMode = PlaybackMode.Shuffle;
        File.Delete(first);
        await _library.RescanPlaylistAsync(_library.SelectedPlaylist!.Id, true);
        Assert.Equal(remaining, _library.GetNextTrack()!.FilePath);
    }

    [Fact]
    public async Task Clearing_browse_selection_does_not_show_duplicate_cross_playlist_members()
    {
        _directory.CreateWave("child/song.wav");
        await _library.AddPlaylistAsync("Parent", _directory.Root);
        await _library.AddPlaylistAsync("Child", _directory.FilePath("child"));
        await _library.SelectPlaylistAsync(null);
        Assert.Empty(_library.Queue);
    }

    public void Dispose() => _directory.Dispose();
}
