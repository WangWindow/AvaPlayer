using AvaPlayer.Models;
using Microsoft.Data.Sqlite;

namespace AvaPlayer.Services.Database;

public sealed class SqliteDatabaseService : IDatabaseService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;

    public SqliteDatabaseService() : this(databasePath: null)
    {
    }

    public SqliteDatabaseService(string? databasePath)
    {
        string root;
        if (!string.IsNullOrWhiteSpace(databasePath))
        {
            root = Path.GetDirectoryName(databasePath) ?? AppContext.BaseDirectory;
            DatabasePath = Path.Combine(root, Path.GetFileName(databasePath));
        }
        else
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            root = string.IsNullOrWhiteSpace(localData)
                ? AppContext.BaseDirectory
                : Path.Combine(localData, "AvaPlayer");
            DatabasePath = Path.Combine(root, "avaplayer.db");
        }

        Directory.CreateDirectory(root);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS library_folders (
                    path TEXT PRIMARY KEY,
                    added_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
                """, cancellationToken);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS tracks (
                    id TEXT PRIMARY KEY,
                    file_path TEXT NOT NULL UNIQUE,
                    title TEXT NOT NULL,
                    artist TEXT NOT NULL,
                    album TEXT NOT NULL,
                    duration_seconds REAL NOT NULL
                );
                """, cancellationToken);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """, cancellationToken);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS playlists (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    folder_path TEXT NOT NULL UNIQUE,
                    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
                """, cancellationToken);
            await MigrateAsync(connection, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var hasTracksPlaylistId = await ExecuteScalarAsync(connection, """
            SELECT COUNT(*)
            FROM pragma_table_info('tracks')
            WHERE name = 'playlist_id';
            """, cancellationToken);
        if (hasTracksPlaylistId == 0)
        {
            await ExecuteAsync(connection, "ALTER TABLE tracks ADD COLUMN playlist_id TEXT NOT NULL DEFAULT '';",
                cancellationToken);
        }

        var playlistCount = await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM playlists;", cancellationToken);
        if (playlistCount > 0)
        {
            return;
        }

        var legacyFolders = new List<string>();
        var foldersCommand = connection.CreateCommand();
        foldersCommand.CommandText = "SELECT path FROM library_folders ORDER BY added_at;";
        await using var foldersReader = await foldersCommand.ExecuteReaderAsync(cancellationToken);
        while (await foldersReader.ReadAsync(cancellationToken))
        {
            legacyFolders.Add(foldersReader.GetString(0));
        }

        if (legacyFolders.Count == 0)
        {
            return;
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var folder in legacyFolders)
        {
            var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name))
            {
                name = folder;
            }

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO playlists (id, name, folder_path)
                VALUES ($id, $name, $folder_path)
                ON CONFLICT(folder_path) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$id", PlaylistIdForFolder(folder));
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$folder_path", folder);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var assignCommand = connection.CreateCommand();
        assignCommand.Transaction = transaction;
        assignCommand.CommandText = """
            UPDATE tracks
            SET playlist_id = COALESCE((
                SELECT p.id
                FROM playlists p
                WHERE instr(lower($sep) || lower(tracks.file_path) || $sep, lower($sep) || lower(p.folder_path) || $sep) = 1
                ORDER BY length(p.folder_path) DESC
                LIMIT 1
            ), '')
            WHERE playlist_id = '';
            """;
        assignCommand.Parameters.AddWithValue("$sep", Path.DirectorySeparatorChar);
        await assignCommand.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    internal static string PlaylistIdForFolder(string folderPath)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(folderPath.ToLowerInvariant()));
        return Convert.ToHexString(bytes);
    }

    private static async Task<long> ExecuteScalarAsync(SqliteConnection connection, string sql,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value ? value : 0;
    }

    public async Task<IReadOnlyList<string>> GetLibraryFoldersAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText = "SELECT path FROM library_folders ORDER BY added_at;";

            var folders = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                folders.Add(reader.GetString(0));
            }

            return folders;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveLibraryFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO library_folders (path)
                VALUES ($path)
                ON CONFLICT(path) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$path", folderPath);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Track>> GetTracksAsync(string? playlistId = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, file_path, title, artist, album, duration_seconds, playlist_id
                FROM tracks
                WHERE ($playlist_id = '' OR playlist_id = $playlist_id)
                ORDER BY artist, album, title;
                """;
            command.Parameters.AddWithValue("$playlist_id", playlistId ?? string.Empty);

            var tracks = new List<Track>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tracks.Add(new Track
                {
                    Id = reader.GetString(0),
                    FilePath = reader.GetString(1),
                    Title = reader.GetString(2),
                    Artist = reader.GetString(3),
                    Album = reader.GetString(4),
                    DurationSeconds = reader.GetDouble(5),
                    PlaylistId = reader.GetString(6)
                });
            }

            return tracks;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PlaylistInfo>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT p.id, p.name, p.folder_path, COUNT(t.id) AS track_count
                FROM playlists p
                LEFT JOIN tracks t ON t.playlist_id = p.id
                GROUP BY p.id, p.name, p.folder_path
                ORDER BY p.created_at, p.rowid;
                """;

            var playlists = new List<PlaylistInfo>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                playlists.Add(new PlaylistInfo
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    FolderPath = reader.GetString(2),
                    TrackCount = reader.GetInt64(3) is long count ? checked((int)count) : 0
                });
            }

            return playlists;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SavePlaylistAsync(PlaylistInfo playlist, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlist.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(playlist.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(playlist.FolderPath);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO playlists (id, name, folder_path)
                VALUES ($id, $name, $folder_path)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    folder_path = excluded.folder_path;
                """;
            command.Parameters.AddWithValue("$id", playlist.Id);
            command.Parameters.AddWithValue("$name", playlist.Name);
            command.Parameters.AddWithValue("$folder_path", playlist.FolderPath);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RenamePlaylistAsync(string playlistId, string newName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText = "UPDATE playlists SET name = $name WHERE id = $id;";
            command.Parameters.AddWithValue("$id", playlistId);
            command.Parameters.AddWithValue("$name", newName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            var deleteTracks = connection.CreateCommand();
            deleteTracks.Transaction = transaction;
            deleteTracks.CommandText = "DELETE FROM tracks WHERE playlist_id = $id;";
            deleteTracks.Parameters.AddWithValue("$id", playlistId);
            await deleteTracks.ExecuteNonQueryAsync(cancellationToken);

            var deletePlaylist = connection.CreateCommand();
            deletePlaylist.Transaction = transaction;
            deletePlaylist.CommandText = "DELETE FROM playlists WHERE id = $id;";
            deletePlaylist.Parameters.AddWithValue("$id", playlistId);
            await deletePlaylist.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveTracksAsync(IEnumerable<Track> tracks, CancellationToken cancellationToken = default)
    {
        var trackList = tracks.ToList();
        if (trackList.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            foreach (var track in trackList)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO tracks (id, file_path, title, artist, album, duration_seconds, playlist_id)
                    VALUES ($id, $file_path, $title, $artist, $album, $duration, $playlist_id)
                    ON CONFLICT(file_path) DO UPDATE SET
                        id = excluded.id,
                        title = excluded.title,
                        artist = excluded.artist,
                        album = excluded.album,
                        duration_seconds = excluded.duration_seconds,
                        playlist_id = excluded.playlist_id;
                    """;
                command.Parameters.AddWithValue("$id", track.Id);
                command.Parameters.AddWithValue("$file_path", track.FilePath);
                command.Parameters.AddWithValue("$title", track.Title);
                command.Parameters.AddWithValue("$artist", track.Artist);
                command.Parameters.AddWithValue("$album", track.Album);
                command.Parameters.AddWithValue("$duration", track.DurationSeconds);
                command.Parameters.AddWithValue("$playlist_id", track.PlaylistId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteTracksAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        var paths = filePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            foreach (var path in paths)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM tracks WHERE file_path = $filePath;";
                command.Parameters.AddWithValue("$filePath", path);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings (key, value)
                VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSettingsBatchAsync(IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default)
    {
        if (settings is null || settings.Count == 0)
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            foreach (var (key, value) in settings)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO settings (key, value)
                    VALUES ($key, $value)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                    """;
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$value", value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = $key LIMIT 1;";
            command.Parameters.AddWithValue("$key", key);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result as string;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
