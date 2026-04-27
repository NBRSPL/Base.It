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
        // DisplayName takes priority when set — that's the whole reason
        // the user typed it. Fall back to the explicit "ENV / Database"
        // pair so chip labels are unambiguous when there's no custom name,
        // and so the chip matches the source picker's primary line.
        var label = profile is not null && !string.IsNullOrWhiteSpace(profile.DisplayName)
            ? profile.DisplayName!
            : $"{env} / {database}";
        return new TargetPickVm(env, database, label, isChecked);
    }

    public string Key => $"{Environment?.ToUpperInvariant()}|{Database?.ToUpperInvariant()}";
}
