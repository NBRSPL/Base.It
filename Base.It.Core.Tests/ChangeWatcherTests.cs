using Base.It.Core.Abstractions;
using Base.It.Core.Drift;
using Base.It.Core.Logging;
using Base.It.Core.Models;
using Xunit;

namespace Base.It.Core.Tests;

public class ChangeWatcherTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), $"baseit_watch_{Guid.NewGuid():N}");
    public void Dispose()
    {
        try { if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private static readonly ObjectIdentifier Foo = new("dbo", "Foo");

    private sealed class MutableScripter : IObjectScripter
    {
        public string CurrentDefinition = "CREATE PROC Foo AS SELECT 1";
        public Task<SqlObjectType> GetObjectTypeAsync(string conn, ObjectIdentifier id, CancellationToken ct = default)
            => Task.FromResult(SqlObjectType.StoredProcedure);
        public Task<SqlObject?> GetObjectAsync(string conn, ObjectIdentifier id, CancellationToken ct = default)
        {
            var def = conn == "src" ? CurrentDefinition : "CREATE PROC Foo AS SELECT 1";
            return Task.FromResult<SqlObject?>(new SqlObject(id, SqlObjectType.StoredProcedure, def,
                Base.It.Core.Hashing.DefinitionHasher.Hash(def)));
        }
        public Task<SqlObject?> GetObjectForDacpacAsync(string conn, ObjectIdentifier id, CancellationToken ct = default)
            => GetObjectAsync(conn, id, ct);
        public Task<ObjectIdentifier?> GetTriggerParentAsync(string conn, ObjectIdentifier triggerId, CancellationToken ct = default)
            => Task.FromResult<ObjectIdentifier?>(null);
        // Tests don't exercise discovery; return an empty list.
        public Task<IReadOnlyList<SqlObjectRef>> ListAllAsync(string conn, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SqlObjectRef>>(Array.Empty<SqlObjectRef>());
    }

    [Fact]
    public async Task Watcher_emits_per_object_then_tick_completed()
    {
        var scripter = new MutableScripter();
        var logger = new FileLogger(_logDir);
        var detector = new DriftDetector(scripter);
        var plan = new WatchPlan("src", "tgt", "DEV", "PROD", new[] { Foo });

        await using var watcher = new ChangeWatcher(
            detector, logger,
            interval: TimeSpan.FromMilliseconds(40),
            planSupplier: _ => Task.FromResult<WatchPlan?>(plan));

        watcher.Start();

        // Consume until we've seen a full tick: Started → Drifted → Completed.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        bool sawStarted = false, sawDrifted = false;
        TickCompleted? firstComplete = null;
        while (firstComplete is null && !cts.IsCancellationRequested)
        {
            var ev = await watcher.Events.ReadAsync(cts.Token);
            switch (ev)
            {
                case TickStarted:                  sawStarted  = true; break;
                case ObjectDrifted:                sawDrifted  = true; break;
                case TickCompleted tc:             firstComplete = tc; break;
            }
        }
        Assert.True(sawStarted);
        Assert.True(sawDrifted);
        Assert.NotNull(firstComplete);
        Assert.Equal(0, firstComplete!.Changed);

        // Flip source and wait for a tick whose ObjectDrifted reports Different.
        scripter.CurrentDefinition = "CREATE PROC Foo AS SELECT 2";
        ObjectDrifted? diff = null;
        while (diff is null && !cts.IsCancellationRequested)
        {
            var ev = await watcher.Events.ReadAsync(cts.Token);
            if (ev is ObjectDrifted od && od.Drift.Kind == DriftKind.Different)
                diff = od;
        }
        Assert.NotNull(diff);

        await watcher.StopAsync();
    }

    [Fact]
    public async Task Start_is_idempotent()
    {
        var detector = new DriftDetector(new MutableScripter());
        var logger = new FileLogger(_logDir);
        await using var watcher = new ChangeWatcher(detector, logger, TimeSpan.FromMilliseconds(50),
            _ => Task.FromResult<WatchPlan?>(null));

        watcher.Start();
        watcher.Start();
        watcher.Start();

        await watcher.StopAsync();
    }

    [Fact]
    public async Task Tick_exception_does_not_kill_the_watcher()
    {
        int ticks = 0;
        var detector = new DriftDetector(new MutableScripter());
        var logger = new FileLogger(_logDir);
        await using var watcher = new ChangeWatcher(detector, logger, TimeSpan.FromMilliseconds(20),
            _ =>
            {
                Interlocked.Increment(ref ticks);
                if (ticks == 1) throw new InvalidOperationException("boom");
                return Task.FromResult<WatchPlan?>(new WatchPlan("src", "tgt", "DEV", "PROD", new[] { Foo }));
            });

        watcher.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        // Must still see at least one TickCompleted after the failing tick.
        TickCompleted? done = null;
        while (done is null && !cts.IsCancellationRequested)
        {
            if (await watcher.Events.ReadAsync(cts.Token) is TickCompleted tc) done = tc;
        }
        Assert.NotNull(done);
        Assert.True(ticks >= 2);

        await watcher.StopAsync();
    }

    [Fact]
    public async Task StopAsync_is_idempotent_and_safe_before_start()
    {
        var detector = new DriftDetector(new MutableScripter());
        var logger = new FileLogger(_logDir);
        var watcher = new ChangeWatcher(detector, logger, TimeSpan.FromMilliseconds(20),
            _ => Task.FromResult<WatchPlan?>(null));

        await watcher.StopAsync();
        watcher.Start();
        await watcher.StopAsync();
        await watcher.StopAsync();
    }
}
