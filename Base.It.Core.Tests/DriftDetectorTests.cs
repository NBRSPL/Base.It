using Base.It.Core.Abstractions;
using Base.It.Core.Drift;
using Base.It.Core.Models;
using Xunit;

namespace Base.It.Core.Tests;

public class DriftDetectorTests
{
    /// <summary>
    /// Fake scripter: returns canned <see cref="SqlObject"/>s keyed by
    /// (connection string, object id). Lets tests express "this env has
    /// object X with definition Y" declaratively.
    /// </summary>
    private sealed class FakeScripter : IObjectScripter
    {
        private readonly Dictionary<(string conn, ObjectIdentifier id), SqlObject?> _world = new();
        private readonly HashSet<(string conn, ObjectIdentifier id)> _errors = new();

        public void Put(string conn, ObjectIdentifier id, string? definition, SqlObjectType type = SqlObjectType.StoredProcedure)
        {
            _world[(conn, id)] = definition is null
                ? null
                : new SqlObject(id, type, definition, Base.It.Core.Hashing.DefinitionHasher.Hash(definition));
        }

        public void FailOn(string conn, ObjectIdentifier id) => _errors.Add((conn, id));

        public Task<SqlObjectType> GetObjectTypeAsync(string conn, ObjectIdentifier id, CancellationToken ct = default)
            => Task.FromResult(_world.TryGetValue((conn, id), out var obj) && obj is not null ? obj.Type : SqlObjectType.Unknown);

        public Task<SqlObject?> GetObjectAsync(string conn, ObjectIdentifier id, CancellationToken ct = default)
        {
            if (_errors.Contains((conn, id))) throw new InvalidOperationException("Simulated fetch failure.");
            _world.TryGetValue((conn, id), out var obj);
            return Task.FromResult(obj);
        }

        public Task<SqlObject?> GetObjectForDacpacAsync(string conn, ObjectIdentifier id, CancellationToken ct = default)
            => GetObjectAsync(conn, id, ct);

        public Task<IReadOnlyList<SqlObjectRef>> ListAllAsync(string conn, CancellationToken ct = default)
        {
            var refs = _world
                .Where(kv => kv.Key.conn == conn && kv.Value is not null)
                .Select(kv => new SqlObjectRef(kv.Value!.Id, kv.Value.Type))
                .ToList();
            return Task.FromResult<IReadOnlyList<SqlObjectRef>>(refs);
        }
    }

    private static readonly ObjectIdentifier Foo = new("dbo", "Foo");
    private static readonly ObjectIdentifier Bar = new("dbo", "Bar");
    private static readonly ObjectIdentifier Baz = new("dbo", "Baz");

    [Fact]
    public async Task Identical_definitions_are_InSync()
    {
        var s = new FakeScripter();
        s.Put("src", Foo, "CREATE PROC Foo AS SELECT 1");
        s.Put("tgt", Foo, "CREATE PROC Foo AS SELECT 1");

        var batch = await new DriftDetector(s).CompareAsync("src", "tgt", "DEV", "PROD", new[] { Foo });

        var item = Assert.Single(batch.Items);
        Assert.Equal(DriftKind.InSync, item.Kind);
        Assert.Equal(0, batch.ChangedCount);
    }

    [Fact]
    public async Task Different_definitions_are_flagged_Different_and_are_syncable()
    {
        var s = new FakeScripter();
        s.Put("src", Foo, "CREATE PROC Foo AS SELECT 1");
        s.Put("tgt", Foo, "CREATE PROC Foo AS SELECT 2"); // diverged

        var batch = await new DriftDetector(s).CompareAsync("src", "tgt", "DEV", "PROD", new[] { Foo });

        var item = Assert.Single(batch.Items);
        Assert.Equal(DriftKind.Different, item.Kind);
        Assert.True(item.IsSyncable);
        Assert.NotEqual(item.SourceHash, item.TargetHash);
        Assert.Equal(1, batch.ChangedCount);
    }

    [Fact]
    public async Task Missing_in_target_is_syncable_missing_in_source_is_not()
    {
        var s = new FakeScripter();
        s.Put("src", Foo, "CREATE PROC Foo AS SELECT 1"); // only in source
        s.Put("tgt", Bar, "CREATE PROC Bar AS SELECT 1"); // only in target

        var batch = await new DriftDetector(s).CompareAsync("src", "tgt", "DEV", "PROD", new[] { Foo, Bar });

        var foo = batch.Items.Single(i => i.Id == Foo);
        var bar = batch.Items.Single(i => i.Id == Bar);
        Assert.Equal(DriftKind.MissingInTarget, foo.Kind);
        Assert.True(foo.IsSyncable);
        Assert.Equal(DriftKind.MissingInSource, bar.Kind);
        Assert.False(bar.IsSyncable);
    }

    [Fact]
    public async Task Fetch_exception_becomes_Error_kind_not_a_thrown_exception()
    {
        var s = new FakeScripter();
        s.Put("src", Foo, "CREATE PROC Foo AS SELECT 1");
        s.FailOn("tgt", Foo); // target fetch throws

        var batch = await new DriftDetector(s).CompareAsync("src", "tgt", "DEV", "PROD", new[] { Foo });

        var item = Assert.Single(batch.Items);
        Assert.Equal(DriftKind.Error, item.Kind);
        Assert.False(item.IsSyncable);
        Assert.Contains("Simulated", item.Message);
        Assert.Equal(1, batch.ErrorCount);
    }

    [Fact]
    public async Task Empty_id_list_returns_empty_batch_without_fetches()
    {
        var s = new FakeScripter();
        var batch = await new DriftDetector(s).CompareAsync("src", "tgt", "DEV", "PROD", Array.Empty<ObjectIdentifier>());
        Assert.Empty(batch.Items);
    }

    [Fact]
    public async Task Parallelism_bound_is_respected()
    {
        // A scripter that records concurrent call count and asserts it never
        // exceeds the configured max parallelism.
        int concurrent = 0, maxSeen = 0;
        var lockObj = new object();
        var s = new InstrumentedScripter(async () =>
        {
            lock (lockObj) { concurrent++; if (concurrent > maxSeen) maxSeen = concurrent; }
            await Task.Delay(20);
            lock (lockObj) { concurrent--; }
        });

        var ids = Enumerable.Range(0, 12).Select(i => new ObjectIdentifier("dbo", $"Obj{i}")).ToArray();
        await new DriftDetector(s, maxParallelism: 3).CompareAsync("src", "tgt", "DEV", "PROD", ids);

        // Each object fires 2 concurrent fetches (src + tgt) within the same
        // semaphore slot, so cap is 2 * maxParallelism.
        Assert.True(maxSeen <= 2 * 3, $"Expected <= 6 concurrent fetches, saw {maxSeen}");
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        // Scripter that respects the cancellation token via Task.Delay(infinite, ct).
        var s = new InstrumentedScripter(ct => Task.Delay(Timeout.Infinite, ct));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new DriftDetector(s).CompareAsync("src", "tgt", "DEV", "PROD", new[] { Foo, Bar, Baz }, cts.Token));
    }

    private sealed class InstrumentedScripter : IObjectScripter
    {
        private readonly Func<CancellationToken, Task> _onCall;
        public InstrumentedScripter(Func<Task> onCall) : this(_ => onCall()) { }
        public InstrumentedScripter(Func<CancellationToken, Task> onCall) { _onCall = onCall; }

        public async Task<SqlObject?> GetObjectAsync(string conn, ObjectIdentifier id, CancellationToken ct = default)
        {
            await _onCall(ct);
            ct.ThrowIfCancellationRequested();
            return new SqlObject(id, SqlObjectType.StoredProcedure, "x", "deadbeef");
        }

        public Task<SqlObject?> GetObjectForDacpacAsync(string conn, ObjectIdentifier id, CancellationToken ct = default)
            => GetObjectAsync(conn, id, ct);

        public Task<SqlObjectType> GetObjectTypeAsync(string conn, ObjectIdentifier id, CancellationToken ct = default)
            => Task.FromResult(SqlObjectType.StoredProcedure);

        public Task<IReadOnlyList<SqlObjectRef>> ListAllAsync(string conn, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SqlObjectRef>>(Array.Empty<SqlObjectRef>());
    }

    [Fact]
    public async Task StreamAsync_yields_each_result_as_soon_as_it_is_ready()
    {
        // Three objects, fetch-delays tuned so Baz returns first, then Bar,
        // then Foo. StreamAsync must deliver them in that order (completion),
        // not in submission order.
        var delays = new Dictionary<ObjectIdentifier, int>
        {
            { Foo, 120 }, { Bar, 60 }, { Baz, 10 }
        };
        var s = new DelayScripter(delays);

        var received = new List<string>();
        await foreach (var d in new DriftDetector(s).StreamAsync("src", "tgt", new[] { Foo, Bar, Baz }))
            received.Add(d.Id.Name);

        Assert.Equal(3, received.Count);
        // Completion order must be Baz, then Bar, then Foo.
        Assert.Equal("Baz", received[0]);
        Assert.Equal("Bar", received[1]);
        Assert.Equal("Foo", received[2]);
    }

    private sealed class DelayScripter : IObjectScripter
    {
        private readonly IReadOnlyDictionary<ObjectIdentifier, int> _delays;
        public DelayScripter(IReadOnlyDictionary<ObjectIdentifier, int> delays) { _delays = delays; }

        public async Task<SqlObject?> GetObjectAsync(string conn, ObjectIdentifier id, CancellationToken ct = default)
        {
            await Task.Delay(_delays[id], ct);
            var def = $"def-{id.Name}-{conn}";
            return new SqlObject(id, SqlObjectType.StoredProcedure, def,
                Base.It.Core.Hashing.DefinitionHasher.Hash(def));
        }

        public Task<SqlObject?> GetObjectForDacpacAsync(string conn, ObjectIdentifier id, CancellationToken ct = default)
            => GetObjectAsync(conn, id, ct);

        public Task<SqlObjectType> GetObjectTypeAsync(string conn, ObjectIdentifier id, CancellationToken ct = default)
            => Task.FromResult(SqlObjectType.StoredProcedure);

        public Task<IReadOnlyList<SqlObjectRef>> ListAllAsync(string conn, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SqlObjectRef>>(Array.Empty<SqlObjectRef>());
    }
}
