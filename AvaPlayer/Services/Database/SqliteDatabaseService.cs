using AvaPlayer.Models;
using Microsoft.Data.Sqlite;

namespace AvaPlayer.Services.Database;

public sealed class SqliteDatabaseService : IDatabaseService
{
    // WAL 允许「读并发、写独占」;用异步读写锁替代原先的全局 SemaphoreSlim(1,1),
    // 避免把读操作也串行化。见 AsyncReadWriteLock 的说明(不使用线程亲和的 ReaderWriterLockSlim)。
    private readonly AsyncReadWriteLock _lock = new();
    private readonly string _connectionString;

    // 幂等守卫:0 = 未初始化,1 = 已完成建表与迁移。InitializeAsync 会被多处重复调用(App、PlaylistService 等),
    // DDL 与迁移只执行一次。
    private int _initialized;

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
        // Microsoft.Data.Sqlite 自 6.0 起默认启用本机连接池,开/关连接本身很便宜;
        // 每次操作仍「开连 → 执行 → Dispose 归还池」,绝不跨线程长期持有 SqliteConnection(SQLite 对象非线程安全)。
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 10,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _initialized) != 0)
        {
            return;
        }

        await _lock.WaitWriteAsync(cancellationToken);
        try
        {
            if (_initialized != 0)
            {
                return;
            }

            await using var connection = await OpenConnectionAsync(cancellationToken);

            // journal_mode=WAL 持久保存在库文件中,只需设置一次,故放在初始化流程而非每次开连。
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

            // 索引在迁移之后创建:playlist_id 列由迁移补齐,IF NOT EXISTS 保证老库升级同样补上。
            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_tracks_playlist_id ON tracks(playlist_id);",
                cancellationToken);
            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_tracks_library_order ON tracks(artist, album, title);",
                cancellationToken);

            // 首次初始化后跑一次优化(analyze + 索引选择性统计),为查询规划器补全统计信息。
            await ExecuteAsync(connection, "PRAGMA optimize=0x10002;", cancellationToken);

            Volatile.Write(ref _initialized, 1);
        }
        finally
        {
            _lock.ReleaseWrite();
        }
    }

    /// <summary>
    /// 统一的开连辅助方法:从池中取出连接并施加所有 per-connection PRAGMA。
    /// 池化连接会被复用,这些设置不随 Dispose 消失但也不保证对新读者生效,故每次开连都幂等地施加一遍。
    /// journal_mode=WAL 是持久设置,由 InitializeAsync 负责,不在此列;
    /// WAL 模式下官方明确不鼓励与 Cache=Shared 组合,因此连接串不启用共享缓存。
    /// </summary>
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            // synchronous=NORMAL 是 WAL 下的正确生产默认值(FULL 会每次提交 fsync);
            // busy_timeout 覆盖「锁等待 → 默认超时」;cache/mmap 为每连接的读性能调优。
            // 这些 PRAGMA 都只在连接生命期内有效,而连接池会复用物理连接,所以每次开连都要施加。
            // 合并为单条多语句命令:拆成 5 条独立命令时,每次开连要付 5 轮
            // command 创建/预编译/执行的开销,实测让单行读取(如 GetSettingAsync)慢约 4 倍。
            await ExecuteAsync(connection, """
                PRAGMA synchronous=NORMAL;
                PRAGMA busy_timeout=5000;
                PRAGMA temp_store=MEMORY;
                PRAGMA cache_size=-65536;
                PRAGMA mmap_size=134217728;
                """, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
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
        // 循环外建一条命令,循环内只改参数值,避免每条记录重复创建命令对象。
        var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = """
            INSERT INTO playlists (id, name, folder_path)
            VALUES ($id, $name, $folder_path)
            ON CONFLICT(folder_path) DO NOTHING;
            """;
        var idParam = insertCommand.Parameters.Add("$id", SqliteType.Text);
        var nameParam = insertCommand.Parameters.Add("$name", SqliteType.Text);
        var folderParam = insertCommand.Parameters.Add("$folder_path", SqliteType.Text);
        foreach (var folder in legacyFolders)
        {
            var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name))
            {
                name = folder;
            }

            idParam.Value = PlaylistIdForFolder(folder);
            nameParam.Value = name;
            folderParam.Value = folder;
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
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
        await _lock.WaitReadAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            _lock.ReleaseRead();
        }
    }

    public async Task SaveLibraryFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        await _lock.WaitWriteAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            _lock.ReleaseWrite();
        }
    }

    public async Task<IReadOnlyList<Track>> GetTracksAsync(string? playlistId = null, CancellationToken cancellationToken = default)
    {
        await _lock.WaitReadAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

            // 拆成两条 SQL:原先的 ($playlist_id = '' OR playlist_id = $playlist_id) 里 OR 会让索引失效。
            // 不带歌单时无 WHERE,带歌单时走 playlist_id 等值谓词,均可命中 idx_tracks_playlist_id / idx_tracks_library_order。
            var command = connection.CreateCommand();
            if (string.IsNullOrEmpty(playlistId))
            {
                command.CommandText = """
                    SELECT id, file_path, title, artist, album, duration_seconds, playlist_id
                    FROM tracks
                    ORDER BY artist, album, title;
                    """;
            }
            else
            {
                command.CommandText = """
                    SELECT id, file_path, title, artist, album, duration_seconds, playlist_id
                    FROM tracks
                    WHERE playlist_id = $playlist_id
                    ORDER BY artist, album, title;
                    """;
                command.Parameters.AddWithValue("$playlist_id", playlistId);
            }

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
            _lock.ReleaseRead();
        }
    }

    public async Task<IReadOnlyList<PlaylistInfo>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitReadAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            _lock.ReleaseRead();
        }
    }

    public async Task SavePlaylistAsync(PlaylistInfo playlist, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlist.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(playlist.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(playlist.FolderPath);

        await _lock.WaitWriteAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            _lock.ReleaseWrite();
        }
    }

    public async Task RenamePlaylistAsync(string playlistId, string newName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        await _lock.WaitWriteAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText = "UPDATE playlists SET name = $name WHERE id = $id;";
            command.Parameters.AddWithValue("$id", playlistId);
            command.Parameters.AddWithValue("$name", newName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.ReleaseWrite();
        }
    }

    public async Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);

        await _lock.WaitWriteAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
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
            _lock.ReleaseWrite();
        }
    }

    public async Task SaveTracksAsync(IEnumerable<Track> tracks, CancellationToken cancellationToken = default)
    {
        var trackList = tracks.ToList();
        if (trackList.Count == 0)
        {
            return;
        }

        await _lock.WaitWriteAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            // 循环外建一条命令并持有参数引用,循环内只改参数值;
            // Microsoft.Data.Sqlite 会自动复用语句编译结果,无需显式 Prepare()。
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
            var idParam = command.Parameters.Add("$id", SqliteType.Text);
            var filePathParam = command.Parameters.Add("$file_path", SqliteType.Text);
            var titleParam = command.Parameters.Add("$title", SqliteType.Text);
            var artistParam = command.Parameters.Add("$artist", SqliteType.Text);
            var albumParam = command.Parameters.Add("$album", SqliteType.Text);
            var durationParam = command.Parameters.Add("$duration", SqliteType.Real);
            var playlistIdParam = command.Parameters.Add("$playlist_id", SqliteType.Text);

            foreach (var track in trackList)
            {
                idParam.Value = track.Id;
                filePathParam.Value = track.FilePath;
                titleParam.Value = track.Title;
                artistParam.Value = track.Artist;
                albumParam.Value = track.Album;
                durationParam.Value = track.DurationSeconds;
                playlistIdParam.Value = track.PlaylistId;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _lock.ReleaseWrite();
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

        await _lock.WaitWriteAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            // 循环外建一条命令,循环内只改参数值。
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM tracks WHERE file_path = $filePath;";
            var filePathParam = command.Parameters.Add("$filePath", SqliteType.Text);
            foreach (var path in paths)
            {
                filePathParam.Value = path;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _lock.ReleaseWrite();
        }
    }

    public async Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await _lock.WaitWriteAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            _lock.ReleaseWrite();
        }
    }

    public async Task SaveSettingsBatchAsync(IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default)
    {
        if (settings is null || settings.Count == 0)
            return;

        await _lock.WaitWriteAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            // 循环外建一条命令,循环内只改参数值。
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO settings (key, value)
                VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            var keyParam = command.Parameters.Add("$key", SqliteType.Text);
            var valueParam = command.Parameters.Add("$value", SqliteType.Text);
            foreach (var (key, value) in settings)
            {
                keyParam.Value = key;
                valueParam.Value = value;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _lock.ReleaseWrite();
        }
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await _lock.WaitReadAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = $key LIMIT 1;";
            command.Parameters.AddWithValue("$key", key);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result as string;
        }
        finally
        {
            _lock.ReleaseRead();
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 异步读写锁:读并发(至多 MaxConcurrentReaders 个)、写独占、写者优先。
    /// 之所以不直接用 ReaderWriterLockSlim:它要求 Enter/Exit 在同一线程,而本服务全部方法是 async,
    /// await 续行可能切换到其他线程池线程,释放锁时会抛 SynchronizationLockException。
    /// 这里用 SemaphoreSlim 组合出等效语义,原生支持异步等待与 CancellationToken,且对 AOT 友好。
    /// 获取顺序:读者 = 闸门 → 读槽;写者 = 闸门(持有直到占满全部读槽)→ 写门 → 全部读槽,
    /// 写者持有闸门期间新读者无法进入,从而避免写者被源源不断的读者饿死。
    /// </summary>
    private sealed class AsyncReadWriteLock
    {
        private const int MaxConcurrentReaders = 16;

        private readonly SemaphoreSlim _turnstile = new(1, 1);
        private readonly SemaphoreSlim _writerGate = new(1, 1);
        private readonly SemaphoreSlim _readerSlots = new(MaxConcurrentReaders, MaxConcurrentReaders);

        public async Task WaitReadAsync(CancellationToken cancellationToken)
        {
            await _turnstile.WaitAsync(cancellationToken);
            try
            {
                await _readerSlots.WaitAsync(cancellationToken);
            }
            catch
            {
                _turnstile.Release();
                throw;
            }

            _turnstile.Release();
        }

        public void ReleaseRead() => _readerSlots.Release();

        public async Task WaitWriteAsync(CancellationToken cancellationToken)
        {
            await _turnstile.WaitAsync(cancellationToken);
            var writerGateHeld = false;
            var acquiredReaderSlots = 0;
            try
            {
                await _writerGate.WaitAsync(cancellationToken);
                writerGateHeld = true;
                for (var i = 0; i < MaxConcurrentReaders; i++)
                {
                    await _readerSlots.WaitAsync(cancellationToken);
                    acquiredReaderSlots++;
                }
            }
            catch
            {
                for (var i = 0; i < acquiredReaderSlots; i++)
                {
                    _readerSlots.Release();
                }

                if (writerGateHeld)
                {
                    _writerGate.Release();
                }

                _turnstile.Release();
                throw;
            }

            // 全部读槽已占用且无新读者能通过闸门,此刻写者独占;闸门可以放行后续读者排队。
            _turnstile.Release();
        }

        public void ReleaseWrite()
        {
            for (var i = 0; i < MaxConcurrentReaders; i++)
            {
                _readerSlots.Release();
            }

            _writerGate.Release();
        }
    }
}
