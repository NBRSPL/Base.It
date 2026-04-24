using Base.It.App.Services;
using Base.It.Core.Config;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Base.It.App.ViewModels;

/// <summary>
/// One row in a multi-target picker used by the Sync and Batch panes.
/// Bundles the (env, db) address with a checkbox state and a short label
/// derived from <see cref="EnvironmentConfig"/> if available.
/// </summary>
public sealed partial class TargetPickVm : ObservableObject
{
    [ObservableProperty] private bool _isChecked;

    public string Environment { get; }
    public string Database    { get; }
    public string Label       { get; }

    public TargetPickVm(string environment, string database, string label, bool isChecked = false)
    {
        Environment = environment;
        Database    = database;
        Label       = string.IsNullOrWhiteSpace(label) ? $"{environment} · {database}" : label;
        _isChecked  = isChecked;
    }

    public static TargetPickVm From(AppServices svc, string? env, string? database, bool isChecked = false)
    {
        env ??= ""; database ??= "";
        var profile = svc.Connections.GetProfile(env, database);
        // Show just the display name when the user has set one (they are
        // enforced unique by Settings), otherwise fall back to the
        // environment. The database name is redundant when a display name
        // is present, and for multi-env setups the env name is the
        // natural identifier.
        var label = profile is not null && !string.IsNullOrWhiteSpace(profile.DisplayName)
            ? profile.DisplayName!
            : env;
        return new TargetPickVm(env, database, label, isChecked);
    }

    public string Key => $"{Environment?.ToUpperInvariant()}|{Database?.ToUpperInvariant()}";
}
