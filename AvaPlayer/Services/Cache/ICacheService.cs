namespace AvaPlayer.Services.Cache;

/// <summary>
/// A point-in-time snapshot of cache occupancy, returned by <see cref="ICacheService.GetStatsAsync"/>.
/// </summary>
/// <param name="TotalBytes">Sum of the sizes of all live (non-tombstone) value payloads tracked by the index.</param>
/// <param name="EntryCount">Number of tracked entries, including tombstones.</param>
public readonly record struct CacheStats(long TotalBytes, long EntryCount);

/// <summary>
/// Local disk-backed cache with an in-process memory tier (L1), a SQLite metadata index,
/// TTL expiry, tombstones (negative caching), and bounded-capacity LRU eviction.
/// </summary>
/// <remarks>
/// <para>
/// Value payloads are stored as opaque files under <see cref="RootPath"/>&lt;category&gt;/ named by a
/// SHA-256 hex hash of <c>(category, key)</c>. Keys are never interpolated into file paths directly,
/// so arbitrary or hostile key strings cannot cause directory traversal or illegal-character failures.
/// </para>
/// <para>
/// All writes are atomic: data is written to a temporary file in the destination directory, flushed to
/// the OS, then moved onto the final name with <c>File.Move(..., overwrite: true)</c> (a same-volume
/// rename, which is atomic on both Linux and Windows). Readers therefore never observe a partially
/// written payload. Cached data is regenerable, so durability against power loss is intentionally not
/// guaranteed (no <c>flushToDisk</c>).
/// </para>
/// <para>
/// Implementations must be safe for concurrent use; the DI registration is a singleton.
/// </para>
/// </remarks>
public interface ICacheService
{
    // ---------------------------------------------------------------------------------
    // Legacy path-oriented surface (kept for backward compatibility with existing callers
    // such as LyricsService / AlbumArtService that place their own files by path).
    // Files created through these members are NOT tracked by the index, TTL, or eviction.
    // ---------------------------------------------------------------------------------

    /// <summary>Absolute path of the cache root directory (created on construction).</summary>
    string RootPath { get; }

    /// <summary>
    /// Returns (and creates, if missing) the directory for <paramref name="category"/> under <see cref="RootPath"/>.
    /// </summary>
    string GetCategoryPath(string category);

    /// <summary>
    /// Returns the full path of <paramref name="fileName"/> inside the category directory.
    /// Does not create the file. Legacy helper for callers that manage files by path themselves.
    /// </summary>
    string GetFilePath(string category, string fileName);

    /// <summary>
    /// Deletes every cached payload in every category, clears the in-process L1 tier, and removes all
    /// rows from the SQLite metadata index so no dangling entries remain. The index database file
    /// itself is recreated empty.
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every cached payload in one category plus its L1 items and index rows.
    /// </summary>
    Task ClearCategoryAsync(string category, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------------------------
    // Tiered cache API (indexed, TTL-aware, tombstone-aware, LRU-evicted).
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Upper bound for the total size of cached value payloads across all categories.
    /// When a write pushes the total above this limit, the service lazily evicts the
    /// least-recently-used entries in batches until the total drops to 90% of the limit.
    /// Setting a smaller value triggers trimming on the next write. Defaults to 256 MiB.
    /// </summary>
    long MaxBytes { get; set; }

    /// <summary>
    /// Reads a UTF-8 text entry, serving from the in-process L1 tier when the entry is small enough
    /// to be memory-cached (see <see cref="SetBytesAsync"/> remarks for the size policy).
    /// </summary>
    /// <param name="category">Category (sub-directory) name; must be a single safe path segment.</param>
    /// <param name="key">Cache key; arbitrary strings are allowed (they are hashed, never used as file names).</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>
    /// The stored text on a hit; <see langword="null"/> on a miss. A miss covers: no entry, an entry
    /// whose TTL has elapsed (expired entries are deleted as a side effect), and keys that only have
    /// a live tombstone — check <see cref="IsKnownMissingAsync"/> to distinguish "never queried"
    /// from "queried, known to have no result".
    /// </returns>
    Task<string?> GetTextAsync(string category, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically writes a UTF-8 text entry. Text entries below the L1 size threshold are also placed
    /// in the in-process memory tier, so subsequent reads skip disk entirely. Replaces any existing
    /// value or tombstone for the same key.
    /// </summary>
    /// <param name="category">Category (sub-directory) name; must be a single safe path segment.</param>
    /// <param name="key">Cache key; arbitrary strings are allowed (hashed to a file name).</param>
    /// <param name="value">Text payload to store.</param>
    /// <param name="ttl">
    /// Time-to-live. <see langword="null"/> (default) means the entry never expires by time and can only
    /// leave the cache through replacement, eviction, or clearing. A positive <see cref="TimeSpan"/> makes
    /// reads after the window treat the entry as a miss (and delete it).
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task SetTextAsync(string category, string key, string value, TimeSpan? ttl = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a binary entry (e.g. album-art image bytes) from disk via async I/O.
    /// Entries whose size is at or below the L1 threshold (currently 64 KiB) are transparently
    /// promoted into the in-process memory tier after the first disk read.
    /// </summary>
    /// <param name="category">Category (sub-directory) name; must be a single safe path segment.</param>
    /// <param name="key">Cache key; arbitrary strings are allowed (hashed to a file name).</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>
    /// A fresh, caller-owned byte array on a hit (safe to mutate); <see langword="null"/> on a miss,
    /// including expired entries (deleted as a side effect) and live tombstones.
    /// </returns>
    Task<byte[]?> GetBytesAsync(string category, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically writes a binary entry. The content is copied; the caller may reuse the buffer.
    /// Large payloads (above the 64 KiB L1 threshold) intentionally bypass the memory tier and live
    /// only on disk, indexed for LRU eviction. Replaces any existing value or tombstone for the key.
    /// </summary>
    /// <param name="category">Category (sub-directory) name; must be a single safe path segment.</param>
    /// <param name="key">Cache key; arbitrary strings are allowed (hashed to a file name).</param>
    /// <param name="value">Payload bytes to store.</param>
    /// <param name="ttl">
    /// Time-to-live; <see langword="null"/> means never expires, a positive window makes later reads miss.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task SetBytesAsync(string category, string key, byte[] value, TimeSpan? ttl = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a tombstone ("negative cache entry"): a query for this key was performed and produced
    /// no result. Callers can then consult <see cref="IsKnownMissingAsync"/> before hitting the network
    /// again. Any existing value for the key (file, L1 item, index row) is removed.
    /// </summary>
    /// <param name="category">Category (sub-directory) name; must be a single safe path segment.</param>
    /// <param name="key">Cache key; arbitrary strings are allowed.</param>
    /// <param name="ttl">
    /// How long the "known missing" answer stays valid. Must be positive; after it elapses,
    /// <see cref="IsKnownMissingAsync"/> returns <see langword="false"/> and the tombstone is deleted,
    /// allowing the caller to retry the lookup.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task MarkMissingAsync(string category, string key, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports whether the key currently has a live (unexpired) tombstone written by
    /// <see cref="MarkMissingAsync"/>. Expired tombstones are deleted as a side effect and reported as
    /// <see langword="false"/>.
    /// </summary>
    /// <param name="category">Category (sub-directory) name; must be a single safe path segment.</param>
    /// <param name="key">Cache key; arbitrary strings are allowed.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns><see langword="true"/> when "no result" is known and still fresh.</returns>
    Task<bool> IsKnownMissingAsync(string category, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns occupancy statistics for the whole cache or a single category, computed from the SQLite
    /// index (never by scanning the file system).
    /// </summary>
    /// <param name="category"><see langword="null"/> for global stats; otherwise restricts to one category.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task<CacheStats> GetStatsAsync(string? category = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces LRU eviction now: trims the least-recently-used entries (in batches) until the total
    /// payload size is at or below 90% of <see cref="MaxBytes"/>. Normally trimming is triggered
    /// lazily by writes; this method exists for explicit maintenance (e.g. from a settings screen).
    /// </summary>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task TrimAsync(CancellationToken cancellationToken = default);
}
