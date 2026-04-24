namespace Base.It.Core.Config;

/// <summary>
/// A logical, named cluster of (source env, source database, one-or-more
/// target routes, object list) to monitor for drift. Grouping keeps
/// unrelated projects from being mixed: you might have a "Portal
/// DEV→TEST+PROD" group and a "Production BI DEV→TEST" group living
/// side-by-side, each polled at its own interval.
///
/// Identity: <see cref="Id"/> is the stable key; <see cref="Name"/> is the
/// user-facing label and may be renamed.
///
/// An EMPTY <see cref="Objects"/> list means "every user object in the
/// source database" — the watcher auto-discovers the list on each tick via
/// <c>IObjectScripter.ListAllAsync</c>.
///
/// <see cref="Targets"/> holds one-or-more endpoints to compare against.
/// A target may live in a different database than the source (same-env
/// cross-db is a common case), so each <see cref="TargetRoute"/> carries
/// its own (env, database) pair. If a file on disk predates this model
/// (single <c>TargetEnv</c> + shared <c>Database</c>), the store converts
/// it at load time.
/// </summary>
public sealed record WatchGroup(
    Guid                                    Id,
    string                                  Name,
    string                                  SourceEnv,
    string                                  SourceDatabase,
    IReadOnlyList<TargetRoute>              Targets,
    IReadOnlyList<string>                   Objects,
    int                                     IntervalSeconds,
    bool                                    Enabled,
    IReadOnlyList<Models.SqlObjectType>?    ObjectTypes = null)
{
    /// <summary>First target — convenience accessor. Null when no targets are configured (should not happen in practice).</summary>
    public TargetRoute? PrimaryTarget => Targets is { Count: > 0 } ? Targets[0] : null;

    /// <summary>Back-compat alias: the env of the primary target.</summary>
    public string TargetEnv => PrimaryTarget?.Environment ?? "";

    /// <summary>Back-compat alias: maps to the source database.</summary>
    public string Database => SourceDatabase;

    /// <summary>
    /// Effective set of SQL types the watcher scans.
    /// <see cref="ObjectTypes"/> semantics:
    /// <list type="bullet">
    /// <item><c>null</c> → no filter configured, scan everything (default).</item>
    /// <item><c>[]</c>   → user explicitly deselected every type, scan nothing.</item>
    /// <item><c>{…}</c>  → scan exactly these types.</item>
    /// </list>
    /// </summary>
    public IReadOnlyList<Models.SqlObjectType> EffectiveObjectTypes =>
        ObjectTypes ?? AllUserTypes;

    /// <summary>The canonical full set used when <see cref="ObjectTypes"/> is null/empty.</summary>
    public static readonly IReadOnlyList<Models.SqlObjectType> AllUserTypes = new[]
    {
        Models.SqlObjectType.Table,
        Models.SqlObjectType.View,
        Models.SqlObjectType.StoredProcedure,
        Models.SqlObjectType.ScalarFunction,
        Models.SqlObjectType.InlineTableFunction,
        Models.SqlObjectType.TableValuedFunction,
        Models.SqlObjectType.Trigger,
    };

    public static WatchGroup Create(
        string name,
        string sourceEnv,
        string sourceDatabase,
        IEnumerable<TargetRoute> targets,
        IEnumerable<string> objects,
        int intervalSeconds = 30,
        bool enabled = true,
        IEnumerable<Models.SqlObjectType>? objectTypes = null)
    {
        var targetList = (targets ?? Array.Empty<TargetRoute>())
            .Where(t => t is not null
                     && !string.IsNullOrWhiteSpace(t.Environment)
                     && !string.IsNullOrWhiteSpace(t.Database))
            .GroupBy(t => t!.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // Preserve null (no filter) vs empty (explicit "scan nothing"):
        // see EffectiveObjectTypes for the semantics.
        List<Models.SqlObjectType>? typeList = objectTypes is null
            ? null
            : objectTypes.Distinct().ToList();

        return new WatchGroup(
            Guid.NewGuid(),
            (name ?? "").Trim(),
            sourceEnv,
            sourceDatabase,
            targetList!,
            (objects ?? Array.Empty<string>())
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .Select(o => o.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Math.Max(5, intervalSeconds), // hard floor: 5s, protects the DB
            enabled,
            typeList);
    }
}
