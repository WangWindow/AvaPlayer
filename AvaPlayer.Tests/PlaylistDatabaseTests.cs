using AvaPlayer.Models;
using AvaPlayer.Services.Database;
using Microsoft.Data.Sqlite;

namespace AvaPlayer.Application.Tests;

public sealed class PlaylistDatabaseTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public PlaylistDatabaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"avaplayer-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
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
    public async Task Initialize_WithLegacyDatabase_CreatesPlaylistPerLegacyFolder()
    {
        CreateLegacyDatabase(_dbPath);
        var db = new SqliteDatabaseService(_dbPath);

        await db.InitializeAsync();

        var playlists = await db.GetPlaylistsAsync();
        var playlist = Assert.Single(playlists);
        Assert.Equal("LegacyMusic", playlist.Name);
        Assert.Equal("C:/LegacyMusic", playlist.FolderPath);
    }

    [Fact]
    public async Task Initialize_WithLegacyTracks_AssignsTracksToMigratedPlaylist()
    {
        CreateLegacyDatabase(_dbPath);
        var db = new SqliteDatabaseService(_dbPath);

        await db.InitializeAsync();

        var playlists = await db.GetPlaylistsAsync();
        var playlist = playlists.Single(p => p.FolderPath == "C:/LegacyMusic");
        Assert.Equal(2, playlist.TrackCount);

        var tracks = await db.GetTracksAsync(playlist.Id);
        Assert.Equal(2, tracks.Count);
        Assert.All(tracks, track => Assert.Equal(playlist.Id, track.PlaylistId));
    }

    [Fact]
    public async Task Initialize_WhenAlreadyMigrated_DoesNotDuplicatePlaylists()
    {
        CreateLegacyDatabase(_dbPath);
        var db = new SqliteDatabaseService(_dbPath);

        await db.InitializeAsync();
        await db.InitializeAsync();

        var playlists = await db.GetPlaylistsAsync();
        Assert.Single(playlists);
    }

    [Fact]
    public async Task SavePlaylist_UpsertsById()
    {
        var db = CreateInitializedDb();
        var playlist = new PlaylistInfo { Id = "p1", Name = "A", FolderPath = @"C:\A" };

        await db.SavePlaylistAsync(playlist);
        await db.SavePlaylistAsync(new PlaylistInfo { Id = playlist.Id, Name = "B", FolderPath = playlist.FolderPath });
        var playlists = await db.GetPlaylistsAsync();
        Assert.Equal("B", Assert.Single(playlists).Name);
    }

    [Fact]
    public async Task RenamePlaylist_UpdatesNameOnly()
    {
        var db = CreateInitializedDb();
        await db.SavePlaylistAsync(new PlaylistInfo { Id = "p1", Name = "A", FolderPath = @"C:\A" });

        await db.RenamePlaylistAsync("p1", "Renamed");

        var playlist = Assert.Single(await db.GetPlaylistsAsync());
        Assert.Equal("Renamed", playlist.Name);
        Assert.Equal(@"C:\A", playlist.FolderPath);
    }

    [Fact]
    public async Task DeletePlaylist_RemovesPlaylistAndItsTracksOnly()
    {
        var db = CreateInitializedDb();
        await db.SavePlaylistAsync(new PlaylistInfo { Id = "p1", Name = "A", FolderPath = @"C:\A" });
        await db.SavePlaylistAsync(new PlaylistInfo { Id = "p2", Name = "B", FolderPath = @"C:\B" });
        await db.SaveTracksAsync(new[]
        {
            new Track { Id = "t1", FilePath = @"C:\A\1.mp3", PlaylistId = "p1" },
            new Track { Id = "t2", FilePath = @"C:\B\2.mp3", PlaylistId = "p2" }
        });

        await db.DeletePlaylistAsync("p1");

        var playlists = await db.GetPlaylistsAsync();
        Assert.Equal("p2", Assert.Single(playlists).Id);
        var remainingTracks = await db.GetTracksAsync();
        Assert.Equal("t2", Assert.Single(remainingTracks).Id);
    }

    [Fact]
    public async Task GetTracks_WithoutPlaylistId_ReturnsAllTracks()
    {
        var db = CreateInitializedDb();
        await db.SavePlaylistAsync(new PlaylistInfo { Id = "p1", Name = "A", FolderPath = @"C:\A" });
        await db.SavePlaylistAsync(new PlaylistInfo { Id = "p2", Name = "B", FolderPath = @"C:\B" });
        await db.SaveTracksAsync(new[]
        {
            new Track { Id = "t1", FilePath = @"C:\A\1.mp3", PlaylistId = "p1" },
            new Track { Id = "t2", FilePath = @"C:\B\2.mp3", PlaylistId = "p2" }
        });

        var all = await db.GetTracksAsync();
        var onlyP1 = await db.GetTracksAsync("p1");

        Assert.Equal(2, all.Count);
        Assert.Equal("t1", Assert.Single(onlyP1).Id);
    }

    private SqliteDatabaseService CreateInitializedDb()
    {
        var db = new SqliteDatabaseService(_dbPath);
        db.InitializeAsync().GetAwaiter().GetResult();
        return db;
    }

    private static void CreateLegacyDatabase(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        Execute(connection, """
            CREATE TABLE library_folders (
                path TEXT PRIMARY KEY,
                added_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """);
        Execute(connection, """
            CREATE TABLE tracks (
                id TEXT PRIMARY KEY,
                file_path TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL,
                artist TEXT NOT NULL,
                album TEXT NOT NULL,
                duration_seconds REAL NOT NULL
            );
            """);
        Execute(connection, "CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
        Execute(connection, "INSERT INTO library_folders (path) VALUES ('C:/LegacyMusic');");
        Execute(connection, "INSERT INTO tracks (id, file_path, title, artist, album, duration_seconds) VALUES ('t1', 'C:/LegacyMusic/1.mp3', 'One', '', '', 10);");
        Execute(connection, "INSERT INTO tracks (id, file_path, title, artist, album, duration_seconds) VALUES ('t2', 'C:/LegacyMusic/2.mp3', 'Two', '', '', 20);");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
