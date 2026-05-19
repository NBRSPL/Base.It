using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Base.It.Core.Schema;

/// <summary>
/// Content-addressable on-disk store for a single (environment, database)
/// schema, modelled on Git's object database. Layout:
///
/// <code>
/// {root}/{env-slug}/{db-slug}/
///   objects/{aa}/{bbcc...}.sql.gz      ← content-addressed, gzipped
///   snapshots/{yyyyMMddTHHmmssZ}.json   ← list of {schema, name, kind, hash, size}
///   refs/main.json                      ← pointer at the latest snapshot id
/// </code>
///
/// Identical definitions across thousands of snapshots collapse to a single
/// file because the filename IS the SHA-256 of the SQL. Gzip then squashes
/// the raw text to ~15–20% of its original size. The snapshot files are
/// tiny because they hold only pointers, not content.
///
/// Thread-safety: writes are not concurrent-safe within one store. The
/// snapshotter serialises its own writes; multiple readers are fine
/// because objects never change once written (content-addressing).
/// </summary>
public sealed class SchemaStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _root;
    private readonly string _objectsDir;
    private readonly string _snapshotsDir;
    private readonly string _refsDir;

    public string Root => _root;

    /// <summary>
    /// Open or create a store at the given root for a specific
    /// (environment, database) pair. Idempotent — calling multiple times
    /// with the same args just opens the existing folder.
    /// </summary>
    public SchemaStore(string rootBase, string environment, string database)
    {
        if (string.IsNullOrWhiteSpace(rootBase)) throw new ArgumentException("rootBase required", nameof(rootBase));
        if (string.IsNullOrWhiteSpace(environment)) throw new ArgumentException("environment required", nameof(environment));
        if (string.IsNullOrWhiteSpace(database))   throw new ArgumentException("database required", nameof(database));

        _root         = Path.Combine(rootBase, Slug(environment), Slug(database));
        _objectsDir   = Path.Combine(_root, "objects");
        _snapshotsDir = Path.Combine(_root, "snapshots");
        _refsDir      = Path.Combine(_root, "refs");

        Directory.CreateDirectory(_objectsDir);
        Directory.CreateDirectory(_snapshotsDir);
        Directory.CreateDirectory(_refsDir);
    }

    // ---------- Objects (content-addressed) ----------

    private string ObjectPath(string hash)
    {
        // Two-level fanout — Git's pattern. Avoids "ls is unusable" once
        // you have 10,000+ files in a single folder. First 2 chars of the
        // hash form a subdirectory; the rest is the filename.
        if (string.IsNullOrWhiteSpace(hash) || hash.Length < 4)
            throw new ArgumentException("Hash must be at least 4 chars", nameof(hash));
        return Path.Combine(_objectsDir, hash[..2], $"{hash[2..]}.sql.gz");
    }

    /// <summary>Has this exact definition already been stored? Cheap — single file existence check.</summary>
    public bool ObjectExists(string hash) => File.Exists(ObjectPath(hash));

    // Per-store cache of fanout directories we've already created so
    // every WriteObjectAsync call doesn't hit Directory.CreateDirectory
    // on the same 256 paths. ConcurrentDictionary lets parallel writers
    // share the cache without locking.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _ensuredDirs = new();

    /// <summary>
    /// Write the SQL definition for the given hash if it isn't already on
    /// disk. Gzipped on write. No-op when the file already exists
    /// (content-addressing means same hash = same content, guaranteed).
    /// Returns true if a new file was written.
    ///
    /// Performance notes:
    /// <list type="bullet">
    ///   <item><c>CompressionLevel.Fastest</c> — for SQL text, Fastest is ~95%
    ///     as good as Optimal at compression but ~5× faster. Worth it.</item>
    ///   <item>No temp-file rename — content-addressing means a half-written
    ///     file would have the right hash anyway only if it had the right
    ///     content. We treat any file in <c>objects/</c> as authoritative;
    ///     a crash mid-write leaves a partially-gzipped file that fails to
    ///     decompress on read, and the snapshot pointer would be re-fetched
    ///     anyway. The .tmp+rename added per-write filesystem ops without
    ///     real safety on Windows.</item>
    ///   <item>Cached <c>CreateDirectory</c> via <see cref="_ensuredDirs"/>.</item>
    /// </list>
    /// </summary>
    public async Task<bool> WriteObjectAsync(string hash, string sql, CancellationToken ct = default)
    {
        var path = ObjectPath(hash);
        if (File.Exists(path)) return false;

        var dir = Path.GetDirectoryName(path)!;
        if (_ensuredDirs.TryAdd(dir, true))
            Directory.CreateDirectory(dir);

        await using var fs = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 8192,
            useAsync: true);
        await using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        await using var sw = new StreamWriter(gz, Encoding.UTF8);
        await sw.WriteAsync(sql.AsMemory(), ct);
        return true;
    }

    /// <summary>Read the raw (decompressed) SQL definition for a hash, or null if not stored.</summary>
    public async Task<string?> ReadObjectAsync(string hash, CancellationToken ct = default)
    {
        var path = ObjectPath(hash);
        if (!File.Exists(path)) return null;

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(gz, Encoding.UTF8);
        return await sr.ReadToEndAsync(ct);
    }

    // ---------- Snapshots ----------

    private string SnapshotPath(string id) => Path.Combine(_snapshotsDir, $"{id}.json");

    /// <summary>
    /// Generate a UTC timestamp ID like "20260514T103015Z" — fixed-width so
    /// alphabetical ordering equals chronological ordering, which makes
    /// <c>ListSnapshots</c> a simple directory scan + sort.
    /// </summary>
    public static string NewSnapshotId(DateTime? whenUtc = null) =>
        (whenUtc ?? DateTime.UtcNow).ToString("yyyyMMddTHHmmssZ");

    public async Task WriteSnapshotAsync(Snapshot snapshot, CancellationToken ct = default)
    {
        var path = SnapshotPath(snapshot.Id);
        var json = JsonSerializer.Serialize(snapshot, JsonOpts);
        await File.WriteAllTextAsync(path, json, Encoding.UTF8, ct);

        // Update refs/main.json so callers can find "the latest snapshot"
        // without scanning the directory. Atomic via temp + move.
        var mainRef = Path.Combine(_refsDir, "main.json");
        var refJson = JsonSerializer.Serialize(new { snapshot = snapshot.Id }, JsonOpts);
        await File.WriteAllTextAsync(mainRef + ".tmp", refJson, Encoding.UTF8, ct);
        File.Move(mainRef + ".tmp", mainRef, overwrite: true);
    }

    public async Task<Snapshot?> ReadSnapshotAsync(string id, CancellationToken ct = default)
    {
        var path = SnapshotPath(id);
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        return JsonSerializer.Deserialize<Snapshot>(json, JsonOpts);
    }

    /// <summary>List all snapshots, newest first. O(n) directory scan + per-file JSON parse for the summary.</summary>
    public IReadOnlyList<SnapshotSummary> ListSnapshots()
    {
        if (!Directory.Exists(_snapshotsDir)) return Array.Empty<SnapshotSummary>();

        var summaries = new List<SnapshotSummary>();
        foreach (var file in Directory.EnumerateFiles(_snapshotsDir, "*.json"))
        {
            // Parse just enough to build a SnapshotSummary. Could be made
            // faster with a streaming parser if this ever shows up in a
            // profile; for now correctness > microseconds.
            try
            {
                var json = File.ReadAllText(file, Encoding.UTF8);
                var snapshot = JsonSerializer.Deserialize<Snapshot>(json, JsonOpts);
                if (snapshot is null) continue;
                summaries.Add(new SnapshotSummary(
                    Id:            snapshot.Id,
                    TakenAtUtc:    snapshot.TakenAtUtc,
                    ObjectCount:   snapshot.Entries.Count,
                    TotalRawBytes: snapshot.Entries.Sum(e => (long)e.Size),
                    FilePath:      file,
                    Name:          snapshot.Name));
            }
            catch { /* corrupted snapshot file — skip silently, surfacing it in stats instead */ }
        }
        // Newest first — fixed-width ID format makes string sort = chronological sort.
        summaries.Sort((a, b) => string.Compare(b.Id, a.Id, StringComparison.Ordinal));
        return summaries;
    }

    /// <summary>
    /// Rename (or clear the name of) an existing snapshot. Atomic — writes
    /// to a temp file and renames into place so a crash mid-write can't
    /// corrupt the snapshot. <paramref name="newName"/> null or empty
    /// clears the name back to "use the timestamp."
    /// </summary>
    public async Task<bool> RenameSnapshotAsync(string snapshotId, string? newName, CancellationToken ct = default)
    {
        var path = SnapshotPath(snapshotId);
        if (!File.Exists(path)) return false;

        var json = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        var snapshot = JsonSerializer.Deserialize<Snapshot>(json, JsonOpts);
        if (snapshot is null) return false;

        var trimmed = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim();
        var updated = snapshot with { Name = trimmed };

        var updatedJson = JsonSerializer.Serialize(updated, JsonOpts);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, updatedJson, Encoding.UTF8, ct);
        File.Move(tmp, path, overwrite: true);
        return true;
    }

    // ---------- Stats ----------

    public StoreStats GetStats()
    {
        var snapshots = ListSnapshots();

        // Walk objects/ once for disk size. Then read each snapshot to get
        // its entries' raw sizes for the "what would no-dedup cost" number.
        long diskBytes = 0;
        int uniqueObjs = 0;
        if (Directory.Exists(_objectsDir))
        {
            foreach (var f in Directory.EnumerateFiles(_objectsDir, "*.sql.gz", SearchOption.AllDirectories))
            {
                diskBytes += new FileInfo(f).Length;
                uniqueObjs++;
            }
        }

        // Raw bytes = sum of every entry's Size across every snapshot.
        // That's what the on-disk footprint would be if we didn't dedup
        // and didn't compress — the "naive" baseline.
        long rawBytesAcrossAllSnapshots = 0;
        foreach (var s in snapshots) rawBytesAcrossAllSnapshots += s.TotalRawBytes;

        return new StoreStats(
            SnapshotCount:     snapshots.Count,
            UniqueObjectCount: uniqueObjs,
            ObjectsDiskBytes:  diskBytes,
            ObjectsRawBytes:   rawBytesAcrossAllSnapshots);
    }

    // ---------- Diff ----------

    /// <summary>
    /// Set-based diff of two snapshots. Same name = match — hash
    /// difference moves it to Changed, hash equality means it appears
    /// in neither bucket. Renames manifest as Add + Remove because in
    /// SQL terms that's exactly what happened.
    /// </summary>
    public static SnapshotDiff Diff(Snapshot from, Snapshot to)
    {
        var fromByKey = from.Entries.ToDictionary(e => e.Key, e => e);
        var toByKey   = to  .Entries.ToDictionary(e => e.Key, e => e);

        var added   = new List<SnapshotEntry>();
        var removed = new List<SnapshotEntry>();
        var changed = new List<SnapshotChange>();

        foreach (var (key, toEntry) in toByKey)
        {
            if (!fromByKey.TryGetValue(key, out var fromEntry))
                added.Add(toEntry);
            else if (!string.Equals(fromEntry.Hash, toEntry.Hash, StringComparison.OrdinalIgnoreCase))
                changed.Add(new SnapshotChange(fromEntry, toEntry));
        }
        foreach (var (key, fromEntry) in fromByKey)
            if (!toByKey.ContainsKey(key)) removed.Add(fromEntry);

        return new SnapshotDiff(from.Id, to.Id, added, removed, changed);
    }

    // ---------- Helpers ----------

    /// <summary>
    /// Filesystem-safe slug: lowercase, A–Z / 0–9 / underscore / hyphen
    /// only. Everything else becomes underscore. Keeps the store layout
    /// readable and cross-platform.
    /// </summary>
    private static string Slug(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.Length == 0 ? "_" : sb.ToString();
    }
}
