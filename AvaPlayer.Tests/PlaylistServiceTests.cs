using AvaPlayer.Helpers;
using AvaPlayer.Models;
using AvaPlayer.Services.Database;
using AvaPlayer.Services.Playlist;
using AvaPlayer.Application.Tests.Fakes;

namespace AvaPlayer.Application.Tests;

public sealed class PlaylistServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteDatabaseService _database;
    private readonly FakeTrackScannerService _scanner;
    private readonly PlaylistService _service;

    public PlaylistServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"avaplayer-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _database = new SqliteDatabaseService(Path.Combine(_tempDir, "test.db"));
        _scanner = new FakeTrackScannerService();
        _service = new PlaylistService(_database, _scanner);
        _database.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task AddPlaylist_UsesFolderNameWhenNameEmpty()
    {
        var folder = CreateMusicFolder("MySongs", "a.mp3");

        await _service.AddPlaylistAsync(name: string.Empty, folder);

        var playlist = Assert.Single(_service.Playlists);
        Assert.Equal("MySongs", playlist.Name);
        Assert.Equal(folder, playlist.FolderPath);
    }

    [Fact]
    public async Task AddPlaylist_ScansTagsTracksAndSelectsIt()
    {
        var folder = CreateMusicFolder("Songs", "a.mp3", "b.mp3");
        _scanner.ScanHandler = _ => new[]
        {
            MakeTrack(Path.Combine(folder, "a.mp3")),
            MakeTrack(Path.Combine(folder, "b.mp3"))
        };

        await _service.AddPlaylistAsync(name: string.Empty, folder);

        Assert.Equal([folder], _scanner.ScannedFolders);
        var playlist = _service.SelectedPlaylist;
        Assert.NotNull(playlist);
        Assert.Equal(2, playlist.TrackCount);
        Assert.Equal(2, _service.Queue.Count);
        Assert.All(_service.Queue, track => Assert.Equal(playlist.Id, track.PlaylistId));
        Assert.Equal(_service.Queue[0], _service.CurrentTrack);
    }

    [Fact]
    public async Task SelectPlaylist_SwitchesQueueToThatPlaylist()
    {
        var first = await AddPlaylistWithTracks("One", "a.mp3");
        var second = await AddPlaylistWithTracks("Two", "c.mp3");

        await _service.SelectPlaylistAsync(first.Id);
        var firstPaths = _service.Queue.Select(t => t.FilePath).ToArray();

        await _service.SelectPlaylistAsync(second.Id);

        Assert.Equal([Path.Combine(_tempDir, "One", "a.mp3")], firstPaths);
        Assert.Equal([Path.Combine(_tempDir, "Two", "c.mp3")], _service.Queue.Select(t => t.FilePath));
        Assert.Equal(second.Id, _service.SelectedPlaylist!.Id);
    }

    [Fact]
    public async Task RenamePlaylist_UpdatesNameInCollection()
    {
        var playlist = await AddPlaylistWithTracks("Old", "a.mp3");

        await _service.RenamePlaylistAsync(playlist.Id, "New");

        Assert.Equal("New", Assert.Single(_service.Playlists).Name);
    }

    [Fact]
    public async Task RemovePlaylist_DeletesTracksAndSelectsRemaining()
    {
        var first = await AddPlaylistWithTracks("One", "a.mp3");
        var second = await AddPlaylistWithTracks("Two", "c.mp3");
        await _service.SelectPlaylistAsync(first.Id);

        await _service.RemovePlaylistAsync(first.Id);

        Assert.Equal("Two", Assert.Single(_service.Playlists).Name);
        Assert.Equal(second.Id, _service.SelectedPlaylist!.Id);
        Assert.Equal([Path.Combine(_tempDir, "Two", "c.mp3")], _service.Queue.Select(t => t.FilePath));
        Assert.Empty(await _database.GetTracksAsync(first.Id));
    }

    [Fact]
    public async Task RemoveTracks_RemovesFromQueueAndPlaylistCount()
    {
        var playlist = await AddPlaylistWithTracks("One", "a.mp3", "b.mp3");
        var removed = _service.Queue.First();

        await _service.RemoveTracksAsync([removed]);

        Assert.Single(_service.Queue);
        Assert.Equal(1, Assert.Single(_service.Playlists).TrackCount);
        Assert.Equal(playlist.Id, _service.SelectedPlaylist!.Id);
    }

    [Fact]
    public async Task LoadAsync_RestoresSelectedPlaylist()
    {
        var playlist = await AddPlaylistWithTracks("One", "a.mp3");

        var freshService = new PlaylistService(_database, _scanner);
        await freshService.LoadAsync();

        Assert.Equal(playlist.Id, freshService.SelectedPlaylist!.Id);
        Assert.Equal([Path.Combine(_tempDir, "One", "a.mp3")], freshService.Queue.Select(t => t.FilePath));
    }

    [Fact]
    public async Task LoadAsync_WithoutPlaylists_LeavesQueueEmpty()
    {
        await _service.LoadAsync();

        Assert.Empty(_service.Playlists);
        Assert.Null(_service.SelectedPlaylist);
        Assert.Empty(_service.Queue);
    }

    private string CreateMusicFolder(string name, params string[] fileNames)
    {
        var folder = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(folder);
        foreach (var fileName in fileNames)
        {
            File.WriteAllText(Path.Combine(folder, fileName), string.Empty);
        }
        return folder;
    }

    private async Task<PlaylistInfo> AddPlaylistWithTracks(string name, params string[] fileNames)
    {
        var folder = CreateMusicFolder(name, fileNames);
        _scanner.ScanHandler = _ => fileNames
            .Select(fileName => MakeTrack(Path.Combine(folder, fileName)))
            .ToArray();
        await _service.AddPlaylistAsync(name: string.Empty, folder);
        return _service.Playlists.First(p => p.FolderPath == folder);
    }

    private static Track MakeTrack(string filePath) => new()
    {
        Id = StableId.ForPath(filePath),
        FilePath = filePath,
        Title = Path.GetFileNameWithoutExtension(filePath)
    };
}
