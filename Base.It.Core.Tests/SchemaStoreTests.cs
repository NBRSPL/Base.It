using Base.It.Core.Models;
using Base.It.Core.Schema;
using Xunit;

namespace Base.It.Core.Tests;

/// <summary>
/// Unit-tests the schema store's on-disk behaviour without touching a SQL
/// Server. Each test uses a temporary folder under the system temp dir so
/// tests can run in parallel without colliding.
/// </summary>
public class SchemaStoreTests : IDisposable
{
    private readonly string _root;

    public SchemaStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "baseit-schemastore-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* tests leaving turds in temp is fine */ }
    }

    // ---------- Construction + layout ----------

    [Fact]
    public void Constructor_creates_objects_snapshots_refs_subdirs()
    {
        var store = new SchemaStore(_root, "DEV", "OrdersDB");
        Assert.True(Directory.Exists(Path.Combine(store.Root, "objects")));
        Assert.True(Directory.Exists(Path.Combine(store.Root, "snapshots")));
        Assert.True(Directory.Exists(Path.Combine(store.Root, "refs")));
    }

    [Fact]
    public void Constructor_slugifies_env_and_db_for_filesystem_safety()
    {
        var store = new SchemaStore(_root, "DEV / staging", "L2.Platform/ProductionDB");
        // Slug rule: lowercase + a-z / 0-9 / underscore / hyphen only.
        Assert.Contains("dev___staging", store.Root);
        Assert.Contains("l2_platform_productiondb", store.Root);
    }

    // ---------- Objects (gzip + content-addressing) ----------

    [Fact]
    public async Task WriteObject_then_ReadObject_roundtrips_definition_text()
    {
        var store = new SchemaStore(_root, "DEV", "OrdersDB");
        const string sql = "CREATE PROCEDURE dbo.sp_Foo AS SELECT 1;";
        const string hash = "abcdef1234567890";

        var wrote = await store.WriteObjectAsync(hash, sql);
        Assert.True(wrote);
        Assert.True(store.ObjectExists(hash));

        var readBack = await store.ReadObjectAsync(hash);
        Assert.Equal(sql, readBack);
    }

    [Fact]
    public async Task WriteObject_is_a_noop_when_hash_already_exists()
    {
        var store = new SchemaStore(_root, "DEV", "OrdersDB");
        const string hash = "abcdef1234567890";

        Assert.True(await store.WriteObjectAsync(hash, "first"));
        // A second write under the same hash should be a no-op (returns
        // false) — content-addressing means same hash = same content,
        // so we never overwrite.
        Assert.False(await store.WriteObjectAsync(hash, "this string would be ignored"));

        // And the original content is preserved.
        Assert.Equal("first", await store.ReadObjectAsync(hash));
    }

    [Fact]
    public async Task WriteObject_uses_two_char_fanout_in_objects_dir()
    {
        // Guard the dedup-friendly directory structure — large databases
        // would slow file listings to a crawl without sharding.
        var store = new SchemaStore(_root, "DEV", "OrdersDB");
        const string hash = "abcdef1234567890";
        await store.WriteObjectAsync(hash, "x");

        var expectedPath = Path.Combine(store.Root, "objects", "ab", "cdef1234567890.sql.gz");
        Assert.True(File.Exists(expectedPath), $"Expected fanout file at {expectedPath}");
    }

    [Fact]
    public async Task ReadObject_returns_null_when_hash_is_unknown()
    {
        var store = new SchemaStore(_root, "DEV", "OrdersDB");
        Assert.Null(await store.ReadObjectAsync("0011223344556677"));
    }

    [Fact]
    public async Task Object_is_gzipped_on_disk_so_storage_is_smaller_than_raw_text()
    {
        // Highly-compressible payload — gzip should shrink it ~10×.
        var sql = string.Concat(Enumerable.Repeat("CREATE PROCEDURE dbo.x AS BEGIN SELECT 1; END;\n", 200));
        var store = new SchemaStore(_root, "DEV", "OrdersDB");
        const string hash = "aabbccddeeff0011";
        await store.WriteObjectAsync(hash, sql);

        var path = Path.Combine(store.Root, "objects", "aa", "bbccddeeff0011.sql.gz");
        var diskSize = new FileInfo(path).Length;

        Assert.True(diskSize < sql.Length / 2,
            $"Gzip didn't compress the repetitive payload: disk={diskSize} raw={sql.Length}");
    }

    // ---------- Snapshots ----------

    [Fact]
    public async Task WriteSnapshot_then_ReadSnapshot_roundtrips_entries()
    {
        var store = new SchemaStore(_root, "DEV", "OrdersDB");
        var snapshot = new Snapshot(
            Id:          "20260514T103015Z",
            TakenAtUtc:  new DateTime(2026, 5, 14, 10, 30, 15, DateTimeKind.Utc),
            Environment: "DEV",
            Database:    "OrdersDB",
            Entries:     new[]
            {
                new SnapshotEntry("dbo", "sp_Foo", SqlObjectType.StoredProcedure, "hash1", 100),
                new SnapshotEntry("dbo", "vw_Bar", SqlObjectType.View,            "hash2", 200),
            });

        await store.WriteSnapshotAsync(snapshot);

        var readBack = await store.ReadSnapshotAsync("20260514T103015Z");
        Assert.NotNull(readBack);
        Assert.Equal(2, readBack!.Entries.Count);
        Assert.Equal("sp_Foo", readBack.Entries[0].Name);
        Assert.Equal(SqlObjectType.View, readBack.Entries[1].Kind);
    }

    [Fact]
    public async Task ListSnapshots_returns_newest_first()
    {
        var store = new SchemaStore(_root, "DEV", "OrdersDB");
        var older = new Snapshot("20260513T100000Z", new DateTime(2026,5,13,10,0,0, DateTimeKind.Utc), "DEV", "X", Array.Empty<SnapshotEntry>());
        var newer = new Snapshot("20260514T100000Z", new DateTime(2026,5,14,10,0,0, DateTimeKind.Utc), "DEV", "X", Array.Empty<SnapshotEntry>());

        // Intentionally write older first so the test catches any "rely on filesystem order" assumption.
        await store.WriteSnapshotAsync(older);
        await store.WriteSnapshotAsync(newer);

        var list = store.ListSnapshots();
        Assert.Equal(2, list.Count);
        Assert.Equal("20260514T100000Z", list[0].Id);
        Assert.Equal("20260513T100000Z", list[1].Id);
    }

    [Fact]
    public async Task WriteSnapshot_updates_refs_main_to_point_at_latest()
    {
        var store = new SchemaStore(_root, "DEV", "OrdersDB");
        var snap = new Snapshot("20260514T100000Z", DateTime.UtcNow, "DEV", "X", Array.Empty<SnapshotEntry>());
        await store.WriteSnapshotAsync(snap);

        var refPath = Path.Combine(store.Root, "refs", "main.json");
        Assert.True(File.Exists(refPath));
        var refJson = await File.ReadAllTextAsync(refPath);
        Assert.Contains("20260514T100000Z", refJson);
    }

    // ---------- Stats ----------

    [Fact]
    public async Task GetStats_counts_unique_objects_and_disk_size()
    {
        var store = new SchemaStore(_root, "DEV", "OrdersDB");
        await store.WriteObjectAsync("aabbccdd00", "CREATE PROCEDURE dbo.A AS SELECT 1;");
        await store.WriteObjectAsync("aabbccdd01", "CREATE PROCEDURE dbo.B AS SELECT 2;");
        // Duplicate of A — no new file, no change to unique count.
        await store.WriteObjectAsync("aabbccdd00", "CREATE PROCEDURE dbo.A AS SELECT 1;");

        var stats = store.GetStats();
        Assert.Equal(2, stats.UniqueObjectCount);
        Assert.True(stats.ObjectsDiskBytes > 0);
    }

    [Fact]
    public async Task GetStats_raw_bytes_sums_across_every_snapshot_to_demonstrate_dedup_savings()
    {
        var store = new SchemaStore(_root, "DEV", "OrdersDB");

        var sameEntry = new SnapshotEntry("dbo", "sp_X", SqlObjectType.StoredProcedure, "aabbccdd00", 1000);
        await store.WriteSnapshotAsync(new Snapshot("20260514T100000Z", DateTime.UtcNow, "DEV", "X", new[] { sameEntry }));
        await store.WriteSnapshotAsync(new Snapshot("20260514T110000Z", DateTime.UtcNow, "DEV", "X", new[] { sameEntry }));
        await store.WriteObjectAsync("aabbccdd00", "CREATE PROCEDURE dbo.sp_X AS SELECT 1;");

        var stats = store.GetStats();
        // 2 snapshots × 1 entry × 1000 bytes = 2000 raw bytes counted —
        // i.e. what storage would have cost without dedup. Only 1 actual
        // object file on disk.
        Assert.Equal(2000, stats.ObjectsRawBytes);
        Assert.Equal(1, stats.UniqueObjectCount);
    }

    // ---------- Diff ----------

    [Fact]
    public void Diff_picks_up_added_removed_and_changed_objects()
    {
        var from = new Snapshot("a", DateTime.UtcNow, "DEV", "X", new[]
        {
            new SnapshotEntry("dbo", "sp_Same",    SqlObjectType.StoredProcedure, "h1", 10),
            new SnapshotEntry("dbo", "sp_Changes", SqlObjectType.StoredProcedure, "h2", 10),
            new SnapshotEntry("dbo", "sp_Removed", SqlObjectType.StoredProcedure, "h3", 10),
        });
        var to = new Snapshot("b", DateTime.UtcNow, "DEV", "X", new[]
        {
            new SnapshotEntry("dbo", "sp_Same",    SqlObjectType.StoredProcedure, "h1",  10),
            new SnapshotEntry("dbo", "sp_Changes", SqlObjectType.StoredProcedure, "h2b", 12),  // hash changed
            new SnapshotEntry("dbo", "sp_Added",   SqlObjectType.StoredProcedure, "h4",  10),
        });

        var diff = SchemaStore.Diff(from, to);

        Assert.Equal("a", diff.FromId);
        Assert.Equal("b", diff.ToId);
        Assert.Single(diff.Added);
        Assert.Equal("sp_Added", diff.Added[0].Name);
        Assert.Single(diff.Removed);
        Assert.Equal("sp_Removed", diff.Removed[0].Name);
        Assert.Single(diff.Changed);
        Assert.Equal("h2",  diff.Changed[0].From.Hash);
        Assert.Equal("h2b", diff.Changed[0].To.Hash);
        Assert.Equal(3, diff.TotalChanges);
    }

    [Fact]
    public void Diff_matches_object_names_case_insensitively()
    {
        // SQL Server schema/object names are case-insensitive by default;
        // the diff key must match that semantic so DBO.SP_X and dbo.sp_X
        // don't show up as an Add + Remove.
        var from = new Snapshot("a", DateTime.UtcNow, "DEV", "X", new[]
        {
            new SnapshotEntry("dbo", "sp_X", SqlObjectType.StoredProcedure, "h1", 10),
        });
        var to = new Snapshot("b", DateTime.UtcNow, "DEV", "X", new[]
        {
            new SnapshotEntry("DBO", "SP_X", SqlObjectType.StoredProcedure, "h1", 10),  // same content, different casing
        });

        var diff = SchemaStore.Diff(from, to);

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
    }
}
