using Base.It.App.Services;
using Base.It.Core.Config;

namespace Base.It.App.ViewModels;

/// <summary>
/// Helpers for populating environment / database drop-downs. Respects the
/// currently-active <see cref="ConnectionGroup"/> — if one is set, the
/// lists are filtered to just that group's members. No active group means
/// "everything configured". Reads live on each call so Settings changes
/// propagate without restart.
/// </summary>
public static class EnvironmentListProvider
{
    /// <summary>Connection profiles visible to the current active group (or all, if no group is active).</summary>
    public static IReadOnlyList<EnvironmentConfig> VisibleConnections(AppServices svc)
    {
        var all = svc.Connections.Load();
        var active = svc.ConnectionGroups.ActiveGroup;
        if (active is null || active.ConnectionKeys.Count == 0) return all;
        return all
            .Where(c => active.Contains(c.Environment, c.Database))
            .ToList();
    }

    public static string[] Environments(AppServices svc) =>
        VisibleConnections(svc).Select(e => e.Environment)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string[] Databases(AppServices svc) =>
        VisibleConnections(svc).Select(e => e.Database)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string? ConnectionString(AppServices svc, string env, string db) =>
        svc.Connections.Get(env, db);
}
