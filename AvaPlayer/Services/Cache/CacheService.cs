using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AvaPlayer.Services.Cache;

/// <summary>
/// Tiered local cache: in-process L1 for small payloads, atomic same-volume file writes on disk,
/// a private SQLite metadata index (TTL + LRU bookkeeping) and lazy batch eviction at <see cref="MaxBytes"/>.
/// </summary>
/// <remarks>
/// The index lives in its own small database (<c>cache-index.db</c> under the cache root) so this service
/// stays independent of <c>Services/Database</c>. All index mutations are serialized through an async gate
/// on a single long-lived connection; L1 mutations are serialized through a short in-memory lock.
/// </remarks>
public sealed class CacheService : ICacheService, IAsyncDisposable
{
    private const int KindValue = 0;
    private const int KindTombstone = 1;
    private const string IndexDatabaseFileName = "cache-index.db";
    private const string ValueFileExtension = ".bin";
    private const long NeverExpires = 0;

    /// <summary>Payloads up to this size are eligible for the in-process L1 tier (lyrics/metadata text).</summary>
    private const long MaxInMemoryEntryBytes = 64 * 1024;

    /// <summary>Total budget for the L1 tier; over-budget L1 entries are evicted least-recently-used first.</summary>
    private const long MaxInMemoryTotalBytes = 4 * 1024 * 1024;

    /// <summary>How recently an entry must have been touched to survive LRU trimming untouched.</summary>
    private const long TouchCoalesceIntervalMs = 5 * 60 * 1000;

    /// <summary>Number of index rows deleted per eviction batch (bounded, never a full rescan sort).</summary>
    private const int EvictionBatchSize = 128;

    private const long DefaultMaxBytes = 256L * 1024 * 1024;

    private readonly object _memoryGate = new();
    private readonly Dictionary<string, MemoryEntry> _memory = new();
    private readonly SemaphoreSlim _dbGate = new(1, 1);
    private readonly string _indexDatabasePath;
    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private long _memoryTotalBytes;
    private long _indexedTotalBytes;

    /// <summary>Creates the cache under <c>{LocalApplicationData}/AvaPlayer/cache</c> (fallback: app directory).</summary>
    public CacheService() : this(rootPath: null)
    {
    }

    /// <summary>
    /// Creates the cache rooted at an explicit directory. Used by tests for isolation; production code
    /// should use the parameterless constructor so DI activation stays unambiguous.
    /// </summary>
    public CacheService(string? rootPath)
    {
        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            RootPath = Path.GetFullPath(rootPath);
        }
        else
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var root = string.IsNullOrWhiteSpace(localData)
                ? AppContext.BaseDirectory
                : localData;
            RootPath = Path.Combine(root, "AvaPlayer", "cache");
        }

        Directory.CreateDirectory(RootPath);
        _indexDatabasePath = Path.Combine(RootPath, IndexDatabaseFileName);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _indexDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    /// <inheritdoc />
    public string RootPath { get; }

    /// <inheritdoc />
    public long MaxBytes { get; set; } = DefaultMaxBytes;

    /// <inheritdoc />
    public string GetCategoryPath(string category)
    {
        var path = Path.Combine(RootPath, category);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <inheritdoc />
    public string GetFilePath(string category, string fileName) =>
        Path.Combine(GetCategoryPath(category), fileName);

    // ---------------------------------------------------------------------------------
    // Tiered cache API
    // ---------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<string?> GetTextAsync(string category, string key, CancellationToken cancellationToken = default)
    {
        ValidateCategory(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var bytes = await GetCoreAsync(category, key, cancellationToken);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    /// <inheritdoc />
    public Task SetTextAsync(string category, string key, string value, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SetCoreAsync(category, key, Encoding.UTF8.GetBytes(value), ttl, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetBytesAsync(string category, string key, CancellationToken cancellationToken = default)
    {
        ValidateCategory(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return await GetCoreAsync(category, key, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetBytesAsync(string category, string key, byte[] value, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SetCoreAsync(category, key, value, ttl, cancellationToken);
    }

    /// <inheritdoc />
    public async Task MarkMissingAsync(string category, string key, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        ValidateCategory(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "Tombstone TTL must be positive.");
        }

        MemoryKey memoryKey = MemoryKey.For(category, key);
        string? staleValueFile = null;

        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            var connection = await EnsureConnectionAsync(cancellationToken);
            var existing = await TrySelectRowAsync(connection, memoryKey, cancellationToken);
            if (existing is not null && existing.Kind == KindValue)
            {
                staleValueFile = existing.RelativePath;
                _indexedTotalBytes -= existing.SizeBytes;
            }

            await UpsertEntryAsync(connection, memoryKey, relativePath: string.Empty, sizeBytes: 0,
                kind: KindTombstone, expiresAtMs: UtcNowMs() + (long)ttl.TotalMilliseconds, cancellationToken);
        }
        finally
        {
            _dbGate.Release();
        }

        if (staleValueFile is not null)
        {
            TryDeleteFile(Path.Combine(RootPath, staleValueFile));
        }

        lock (_memoryGate)
        {
            _memory.Remove(memoryKey.Value);
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsKnownMissingAsync(string category, string key, CancellationToken cancellationToken = default)
    {
        ValidateCategory(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        MemoryKey memoryKey = MemoryKey.For(category, key);
        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            var connection = await EnsureConnectionAsync(cancellationToken);
            var row = await TrySelectRowAsync(connection, memoryKey, cancellationToken);
            if (row is null || row.Kind != KindTombstone)
            {
                return false;
            }

            if (IsExpired(row.ExpiresAtMs))
            {
                await DeleteEntryAsync(connection, memoryKey, cancellationToken);
                return false;
            }

            return true;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CacheStats> GetStatsAsync(string? category = null, CancellationToken cancellationToken = default)
    {
        if (category is not null)
        {
            ValidateCategory(category);
        }

        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            var connection = await EnsureConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            if (category is null)
            {
                command.CommandText = "SELECT COALESCE(SUM(size_bytes), 0), COUNT(*) FROM cache_entries;";
            }
            else
            {
                command.CommandText = "SELECT COALESCE(SUM(size_bytes), 0), COUNT(*) FROM cache_entries WHERE category = $category;";
                command.Parameters.AddWithValue("$category", category);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new CacheStats(0, 0);
            }

            return new CacheStats(reader.GetInt64(0), reader.GetInt64(1));
        }
        finally
        {
            _dbGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task TrimAsync(CancellationToken cancellationToken = default)
    {
        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            var connection = await EnsureConnectionAsync(cancellationToken);
            await TrimToAsync(connection, (long)(MaxBytes * 0.9), cancellationToken);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    // ---------------------------------------------------------------------------------
    // Legacy clearing (also wipes L1 + index so no dangling metadata remains)
    // ---------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            var connection = await EnsureConnectionAsync(cancellationToken);
            await ExecuteAsync(connection, "DELETE FROM cache_entries;", cancellationToken);
            _indexedTotalBytes = 0;
        }
        finally
        {
            _dbGate.Release();
        }

        lock (_memoryGate)
        {
            _memory.Clear();
            _memoryTotalBytes = 0;
        }

        foreach (var directory in Directory.EnumerateDirectories(RootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(directory, recursive: true);
        }

        foreach (var file in Directory.EnumerateFiles(RootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Keep the private index database (and its WAL sidecars); its rows were just deleted.
            if (Path.GetFileName(file).StartsWith(IndexDatabaseFileName, StringComparison.Ordinal))
            {
                continue;
            }

            File.Delete(file);
        }

        Directory.CreateDirectory(RootPath);
    }

    /// <inheritdoc />
    public async Task ClearCategoryAsync(string category, CancellationToken cancellationToken = default)
    {
        ValidateCategory(category);

        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            var connection = await EnsureConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM cache_entries WHERE category = $category;";
            command.Parameters.AddWithValue("$category", category);
            await command.ExecuteNonQueryAsync(cancellationToken);

            // Re-read the authoritative total instead of tracking per-row sizes here.
            _indexedTotalBytes = await ExecuteScalarAsync(connection, "SELECT COALESCE(SUM(size_bytes), 0) FROM cache_entries;", cancellationToken);
        }
        finally
        {
            _dbGate.Release();
        }

        var prefix = category + "|";
        lock (_memoryGate)
        {
            foreach (var entry in _memory)
            {
                if (entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    _memoryTotalBytes -= entry.Value.Payload.Length;
                    _memory.Remove(entry.Key);
                }
            }
        }

        var path = Path.Combine(RootPath, category);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    /// <summary>Closes the private index connection. The singleton lives for the app lifetime; DI calls this on shutdown.</summary>
    public async ValueTask DisposeAsync()
    {
        await _dbGate.WaitAsync();
        try
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
        finally
        {
            _dbGate.Release();
        }

        _dbGate.Dispose();
    }

    // ---------------------------------------------------------------------------------
    // Core get/set
    // ---------------------------------------------------------------------------------

    private async Task<byte[]?> GetCoreAsync(string category, string key, CancellationToken cancellationToken)
    {
        MemoryKey memoryKey = MemoryKey.For(category, key);
        var now = UtcNowMs();

        MemoryEntry? memoryHit;
        lock (_memoryGate)
        {
            if (_memory.TryGetValue(memoryKey.Value, out var cached))
            {
                if (cached.ExpiresAtMs == NeverExpires || cached.ExpiresAtMs > now)
                {
                    cached.LastAccessMs = now;
                    memoryHit = cached;
                }
                else
                {
                    RemoveMemoryEntryLocked(memoryKey.Value, cached);
                    memoryHit = null;
                }
            }
            else
            {
                memoryHit = null;
            }
        }

        if (memoryHit is not null)
        {
            // Coalesce LRU bookkeeping: only rewrite the index when the stored timestamp went stale.
            if (now >= memoryHit.TouchDueMs)
            {
                memoryHit.TouchDueMs = now + TouchCoalesceIntervalMs;
                await TouchAsync(memoryKey, now, cancellationToken);
            }

            return (byte[])memoryHit.Payload.Clone();
        }

        IndexedRow? row;
        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            var connection = await EnsureConnectionAsync(cancellationToken);
            row = await TrySelectRowAsync(connection, memoryKey, cancellationToken);
            if (row is null)
            {
                return null;
            }

            if (row.Kind == KindTombstone || IsExpired(row.ExpiresAtMs))
            {
                // Miss (or expired): drop metadata and payload so the index never dangles.
                _indexedTotalBytes -= row.SizeBytes;
                await DeleteEntryAsync(connection, memoryKey, cancellationToken);
                row = null;
            }
        }
        finally
        {
            _dbGate.Release();
        }

        if (row is null)
        {
            TryDeleteFile(Path.Combine(RootPath, MemoryRelativePath(category, memoryKey.Hash)));
            return null;
        }

        byte[] payload;
        try
        {
            payload = await ReadFileAsync(Path.Combine(RootPath, row.RelativePath), cancellationToken);
        }
        catch (FileNotFoundException)
        {
            await _dbGate.WaitAsync(cancellationToken);
            try
            {
                var connection = await EnsureConnectionAsync(cancellationToken);
                _indexedTotalBytes -= row.SizeBytes;
                await DeleteEntryAsync(connection, memoryKey, cancellationToken);
            }
            finally
            {
                _dbGate.Release();
            }

            return null;
        }

        await TouchAsync(memoryKey, UtcNowMs(), cancellationToken);
        if (payload.Length <= MaxInMemoryEntryBytes)
        {
            AddMemoryEntry(memoryKey.Value, payload, row.ExpiresAtMs);
        }

        return (byte[])payload.Clone();
    }

    private async Task SetCoreAsync(string category, string key, byte[] payload, TimeSpan? ttl, CancellationToken cancellationToken)
    {
        ValidateCategory(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (ttl is { } ttlValue && ttlValue <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive when provided.");
        }

        MemoryKey memoryKey = MemoryKey.For(category, key);
        var relativePath = MemoryRelativePath(category, memoryKey.Hash);
        var finalPath = Path.Combine(RootPath, relativePath);
        var expiresAtMs = ttl is null ? NeverExpires : UtcNowMs() + (long)ttl.Value.TotalMilliseconds;

        await WriteFileAtomicAsync(finalPath, payload, cancellationToken);

        string? supersededFile = null;

        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            var connection = await EnsureConnectionAsync(cancellationToken);
            var existing = await TrySelectRowAsync(connection, memoryKey, cancellationToken);
            if (existing is not null && existing.Kind == KindValue && existing.RelativePath != relativePath)
            {
                supersededFile = existing.RelativePath;
            }

            _indexedTotalBytes += payload.Length - (existing is { Kind: KindValue } ? existing.SizeBytes : 0);
            await UpsertEntryAsync(connection, memoryKey, relativePath, payload.Length, KindValue, expiresAtMs, cancellationToken);

            // O(1) in-memory overflow check; the (batched) trim only runs when actually over budget.
            if (_indexedTotalBytes > MaxBytes)
            {
                await TrimToAsync(connection, (long)(MaxBytes * 0.9), cancellationToken);
            }
        }
        finally
        {
            _dbGate.Release();
        }

        if (supersededFile is not null)
        {
            TryDeleteFile(Path.Combine(RootPath, supersededFile));
        }

        if (payload.Length <= MaxInMemoryEntryBytes)
        {
            // Copy for the documented "caller may reuse the buffer" contract on the Set APIs.
            AddMemoryEntry(memoryKey.Value, (byte[])payload.Clone(), expiresAtMs);
        }
        else
        {
            lock (_memoryGate)
            {
                if (_memory.Remove(memoryKey.Value, out var removed))
                {
                    _memoryTotalBytes -= removed.Payload.Length;
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------
    // LRU trimming (batched; runs with _dbGate held)
    // ---------------------------------------------------------------------------------

    private async Task TrimToAsync(SqliteConnection connection, long targetBytes, CancellationToken cancellationToken)
    {
        if (targetBytes < 0)
        {
            targetBytes = 0;
        }

        var deletedFiles = new List<string>();
        while (_indexedTotalBytes > targetBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = new List<(string Category, string Hash, long Size)>();
            var select = connection.CreateCommand();
            select.CommandText = """
                SELECT category, key_hash, size_bytes
                FROM cache_entries
                ORDER BY last_access_ms ASC
                LIMIT $limit;
                """;
            select.Parameters.AddWithValue("$limit", EvictionBatchSize);
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    batch.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
                }
            }

            if (batch.Count == 0)
            {
                break;
            }

            foreach (var (category, hash, size) in batch)
            {
                var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM cache_entries WHERE category = $category AND key_hash = $hash;";
                delete.Parameters.AddWithValue("$category", category);
                delete.Parameters.AddWithValue("$hash", hash);
                await delete.ExecuteNonQueryAsync(cancellationToken);
                _indexedTotalBytes -= size;
                deletedFiles.Add(Path.Combine(RootPath, MemoryRelativePath(category, hash)));
                lock (_memoryGate)
                {
                    if (_memory.Remove(category + "|" + hash, out var evicted))
                    {
                        _memoryTotalBytes -= evicted.Payload.Length;
                    }
                }
            }
        }

        foreach (var file in deletedFiles)
        {
            TryDeleteFile(file);
        }
    }

    // ---------------------------------------------------------------------------------
    // Index helpers (all callers hold _dbGate and a valid connection)
    // ---------------------------------------------------------------------------------

    private async Task<SqliteConnection> EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return _connection;
        }

        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS cache_entries (
                key_hash TEXT NOT NULL,
                category TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                kind INTEGER NOT NULL,
                last_access_ms INTEGER NOT NULL,
                expires_at_ms INTEGER NOT NULL,
                PRIMARY KEY (category, key_hash)
            );
            """, cancellationToken);
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_cache_entries_lru ON cache_entries(last_access_ms);", cancellationToken);
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_cache_entries_category ON cache_entries(category);", cancellationToken);

        _indexedTotalBytes = await ExecuteScalarAsync(connection, "SELECT COALESCE(SUM(size_bytes), 0) FROM cache_entries;", cancellationToken);
        _connection = connection;
        return connection;
    }

    private async Task<IndexedRow?> TrySelectRowAsync(SqliteConnection connection, MemoryKey memoryKey, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT relative_path, size_bytes, kind, expires_at_ms
            FROM cache_entries
            WHERE category = $category AND key_hash = $hash;
            """;
        command.Parameters.AddWithValue("$category", memoryKey.Category);
        command.Parameters.AddWithValue("$hash", memoryKey.Hash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new IndexedRow(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3));
    }

    private async Task UpsertEntryAsync(SqliteConnection connection, MemoryKey memoryKey, string relativePath,
        long sizeBytes, int kind, long expiresAtMs, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cache_entries (key_hash, category, relative_path, size_bytes, kind, last_access_ms, expires_at_ms)
            VALUES ($hash, $category, $path, $size, $kind, $access, $expires)
            ON CONFLICT(category, key_hash) DO UPDATE SET
                relative_path = excluded.relative_path,
                size_bytes = excluded.size_bytes,
                kind = excluded.kind,
                last_access_ms = excluded.last_access_ms,
                expires_at_ms = excluded.expires_at_ms;
            """;
        command.Parameters.AddWithValue("$hash", memoryKey.Hash);
        command.Parameters.AddWithValue("$category", memoryKey.Category);
        command.Parameters.AddWithValue("$path", relativePath);
        command.Parameters.AddWithValue("$size", sizeBytes);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$access", UtcNowMs());
        command.Parameters.AddWithValue("$expires", expiresAtMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteEntryAsync(SqliteConnection connection, MemoryKey memoryKey, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM cache_entries WHERE category = $category AND key_hash = $hash;";
        command.Parameters.AddWithValue("$category", memoryKey.Category);
        command.Parameters.AddWithValue("$hash", memoryKey.Hash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task TouchAsync(MemoryKey memoryKey, long timestampMs, CancellationToken cancellationToken)
    {
        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            var connection = await EnsureConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE cache_entries SET last_access_ms = $access
                WHERE category = $category AND key_hash = $hash;
                """;
            command.Parameters.AddWithValue("$access", timestampMs);
            command.Parameters.AddWithValue("$category", memoryKey.Category);
            command.Parameters.AddWithValue("$hash", memoryKey.Hash);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    // ---------------------------------------------------------------------------------
    // L1 memory tier
    // ---------------------------------------------------------------------------------

    private void AddMemoryEntry(string memoryKeyString, byte[] payload, long expiresAtMs)
    {
        var now = UtcNowMs();
        lock (_memoryGate)
        {
            if (_memory.TryGetValue(memoryKeyString, out var previous))
            {
                _memoryTotalBytes -= previous.Payload.Length;
            }

            _memory[memoryKeyString] = new MemoryEntry
            {
                Payload = payload,
                ExpiresAtMs = expiresAtMs,
                LastAccessMs = now,
                TouchDueMs = now + TouchCoalesceIntervalMs,
            };
            _memoryTotalBytes += payload.Length;

            // LRU-trim L1 itself when over budget; small table, linear scan is fine.
            while (_memoryTotalBytes > MaxInMemoryTotalBytes && _memory.Count > 0)
            {
                string? oldestKey = null;
                var oldestAccess = long.MaxValue;
                foreach (var candidate in _memory)
                {
                    if (candidate.Value.LastAccessMs < oldestAccess)
                    {
                        oldestAccess = candidate.Value.LastAccessMs;
                        oldestKey = candidate.Key;
                    }
                }

                if (oldestKey is null)
                {
                    break;
                }

                RemoveMemoryEntryLocked(oldestKey, _memory[oldestKey]);
            }
        }
    }

    private void RemoveMemoryEntryLocked(string memoryKeyString, MemoryEntry entry)
    {
        _memory.Remove(memoryKeyString);
        _memoryTotalBytes -= entry.Payload.Length;
    }

    // ---------------------------------------------------------------------------------
    // File I/O (async handles + RandomAccess; atomic temp-write + rename)
    // ---------------------------------------------------------------------------------

    private static async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.Asynchronous);
        var length = RandomAccess.GetLength(handle);
        if (length <= 0)
        {
            return [];
        }

        if (length > Array.MaxLength)
        {
            throw new IOException($"Cached file '{path}' is too large to read into memory.");
        }

        var buffer = new byte[(int)length];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(total), total, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total == buffer.Length)
        {
            return buffer;
        }

        Array.Resize(ref buffer, total);
        return buffer;
    }

    private static async Task WriteFileAtomicAsync(string finalPath, byte[] payload, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException($"Cache path '{finalPath}' has no parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            await using (var stream = new FileStream(tempPath, options))
            {
                await stream.WriteAsync(payload, cancellationToken);

                // Cache data is regenerable: flush to the OS page cache only, never Flush(true).
                stream.Flush(flushToDisk: false);
            }

            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---------------------------------------------------------------------------------
    // Key safety & misc helpers
    // ---------------------------------------------------------------------------------

    private static void ValidateCategory(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        if (category.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || category.Contains('/', StringComparison.Ordinal)
            || category.Contains('\\', StringComparison.Ordinal)
            || category is "." or "..")
        {
            throw new ArgumentException($"Category '{category}' must be a single safe path segment.", nameof(category));
        }
    }

    private static string MemoryRelativePath(string category, string hash) =>
        category + "/" + hash + ValueFileExtension;

    private static bool IsExpired(long expiresAtMs) =>
        expiresAtMs != NeverExpires && UtcNowMs() >= expiresAtMs;

    private static long UtcNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ExecuteScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value ? value : 0;
    }

    /// <summary>Stable identity for a cache entry: category plus the SHA-256 hex hash of the caller-supplied key.</summary>
    private readonly struct MemoryKey
    {
        private MemoryKey(string category, string hash)
        {
            Category = category;
            Hash = hash;
            Value = category + "|" + hash;
        }

        public string Category { get; }

        public string Hash { get; }

        /// <summary>Key used inside the L1 dictionary (category namespaced, collision-free).</summary>
        public string Value { get; }

        public static MemoryKey For(string category, string key)
        {
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(category + "\n" + key), hash);
            return new MemoryKey(category, Convert.ToHexString(hash).ToLowerInvariant());
        }
    }

    private sealed class MemoryEntry
    {
        public required byte[] Payload { get; init; }

        public required long ExpiresAtMs { get; init; }

        public long LastAccessMs;

        public long TouchDueMs;
    }

    private sealed record IndexedRow(string RelativePath, long SizeBytes, int Kind, long ExpiresAtMs);
}
