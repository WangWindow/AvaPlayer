using AvaPlayer.Models;
using Microsoft.Data.Sqlite;

namespace AvaPlayer.Services.Database;

public sealed class SqliteDatabaseService : IDatabaseService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;

    public SqliteDatabaseService()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localData)
            ? AppContext.BaseDirectory
            : Path.Combine(localData, "AvaPlayer");

        Directory.CreateDirectory(root);
        DatabasePath = Path.Combine(root, "avaplayer.db");
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
        }
        finally
        {
            _gate.Release();
        }
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

    public async Task<IReadOnlyList<Track>> GetTracksAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, file_path, title, artist, album, duration_seconds
                FROM tracks
                ORDER BY artist, album, title;
                """;

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
                    DurationSeconds = reader.GetDouble(5)
                });
            }

            return tracks;
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
                    INSERT INTO tracks (id, file_path, title, artist, album, duration_seconds)
                    VALUES ($id, $file_path, $title, $artist, $album, $duration)
                    ON CONFLICT(file_path) DO UPDATE SET
                        id = excluded.id,
                        title = excluded.title,
                        artist = excluded.artist,
                        album = excluded.album,
                        duration_seconds = excluded.duration_seconds;
                    """;
                command.Parameters.AddWithValue("$id", track.Id);
                command.Parameters.AddWithValue("$file_path", track.FilePath);
                command.Parameters.AddWithValue("$title", track.Title);
                command.Parameters.AddWithValue("$artist", track.Artist);
                command.Parameters.AddWithValue("$album", track.Album);
                command.Parameters.AddWithValue("$duration", track.DurationSeconds);
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
