using Base.It.Core.Config;
using Xunit;

namespace Base.It.Core.Tests;

public class WatchGroupStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"baseit_wg_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    private static WatchGroup MakeGroup(
        string name, string src, string tgtEnv, string db,
        IEnumerable<string> objects, int intervalSeconds = 30, bool enabled = true)
        => WatchGroup.Create(name, src, db,
            new[] { new TargetRoute(tgtEnv, db) },
            objects, intervalSeconds, enabled);

    [Fact]
    public async Task Roundtrips_through_disk()
    {
        var store = new WatchGroupStore(_path);
        var g1 = MakeGroup("Portal DEV->TEST", "DEV", "TEST", "Portal", new[] { "usp_A", "usp_B" });
        var g2 = MakeGroup("Prod DEV->PROD",  "DEV", "PROD", "Production", new[] { "vw_Orders" }, intervalSeconds: 60);
        store.Upsert(g1);
        store.Upsert(g2);
        await store.SaveAsync();

        var reloaded = new WatchGroupStore(_path);
        await reloaded.LoadAsync();
        var all = reloaded.All;

        Assert.Equal(2, all.Count);
        Assert.Equal("Portal DEV->TEST", all.First(g => g.Id == g1.Id).Name);
        Assert.Equal(60,                  all.First(g => g.Id == g2.Id).IntervalSeconds);
        Assert.Equal(new[] { "usp_A", "usp_B" }, all.First(g => g.Id == g1.Id).Objects);
    }

    [Fact]
    public async Task Missing_file_loads_as_empty()
    {
        var store = new WatchGroupStore(_path);
        await store.LoadAsync();
        Assert.Empty(store.All);
    }

    [Fact]
    public async Task Upsert_replaces_existing_by_id()
    {
        var store = new WatchGroupStore(_path);
        var g = MakeGroup("A", "DEV", "TEST", "Portal", new[] { "x" });
        store.Upsert(g);
        store.Upsert(g with { Name = "A (renamed)" });
        await store.SaveAsync();

        var reloaded = new WatchGroupStore(_path);
        await reloaded.LoadAsync();
        Assert.Single(reloaded.All);
        Assert.Equal("A (renamed)", reloaded.All[0].Name);
    }

    [Fact]
    public async Task Remove_deletes_by_id()
    {
        var store = new WatchGroupStore(_path);
        var g = MakeGroup("A", "DEV", "TEST", "Portal", new[] { "x" });
        store.Upsert(g);
        Assert.True(store.Remove(g.Id));
        Assert.False(store.Remove(g.Id)); // second remove is a no-op
        await store.SaveAsync();
        Assert.Empty(new WatchGroupStore(_path).All);
    }

    [Fact]
    public void Create_deduplicates_objects_and_enforces_min_interval()
    {
        var g = MakeGroup("A", "DEV", "TEST", "Portal",
            new[] { "foo", "Foo", "  bar ", "bar", "" }, intervalSeconds: 1);
        Assert.Equal(new[] { "foo", "bar" }, g.Objects);
        Assert.True(g.IntervalSeconds >= 5); // floor protects the DB
    }

    [Fact]
    public async Task Legacy_single_target_shape_migrates_to_multi_target_on_load()
    {
        // Simulate a file written by the previous schema:
        //   { sourceEnv, targetEnv, database, objects, intervalSeconds, enabled }
        var legacyJson =
            "[{"
            + "\"id\":\"" + Guid.NewGuid() + "\","
            + "\"name\":\"Legacy\","
            + "\"sourceEnv\":\"DEV\",\"targetEnv\":\"TEST\",\"database\":\"Portal\","
            + "\"objects\":[\"usp_A\"],"
            + "\"intervalSeconds\":30,\"enabled\":true"
            + "}]";
        await File.WriteAllTextAsync(_path, legacyJson);

        var store = new WatchGroupStore(_path);
        await store.LoadAsync();

        Assert.Single(store.All);
        var g = store.All[0];
        Assert.Equal("DEV",    g.SourceEnv);
        Assert.Equal("Portal", g.SourceDatabase);
        Assert.Single(g.Targets);
        Assert.Equal("TEST",   g.Targets[0].Environment);
        Assert.Equal("Portal", g.Targets[0].Database);
    }

    [Fact]
    public void Multi_target_is_deduped_on_create()
    {
        var dup = new TargetRoute("TEST", "Portal");
        var g = WatchGroup.Create("A", "DEV", "Portal",
            new[] { dup, new TargetRoute("test", "portal"), new TargetRoute("PROD", "Portal") },
            new[] { "x" });
        Assert.Equal(2, g.Targets.Count);
    }
}
