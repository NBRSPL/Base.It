using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Base.It.App.Services;
using Base.It.App.Views;
using Base.It.Core.Config;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Base.It.App.ViewModels;

/// <summary>
/// One editable target row — an (environment, database) pair plus a
/// display label resolved from the connection store. The editor's target
/// list is a bindable collection of these; dedup/validation happens on
/// Save.
/// </summary>
public sealed partial class TargetEditVm : ObservableObject
{
    [ObservableProperty] private string? _environment;
    [ObservableProperty] private string? _database;

    public ObservableCollection<string> Environments { get; }
    public ObservableCollection<string> Databases    { get; }

    public TargetEditVm(IEnumerable<string> environments, IEnumerable<string> databases,
                        string? env = null, string? database = null)
    {
        Environments = new ObservableCollection<string>(environments);
        Databases    = new ObservableCollection<string>(databases);
        _environment = env;
        _database    = database;
    }
}

/// <summary>
/// VM for the New / Edit Watch Group dialog. The editor supports multiple
/// target endpoints per group: either "same source database, different
/// environments" or "arbitrary (env, db) pairs" — both go through the same
/// target list.
/// </summary>
public sealed partial class WatchGroupEditorViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly WatchGroup? _existing;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string? _sourceEnv;
    [ObservableProperty] private string? _sourceDatabase;
    [ObservableProperty] private int    _intervalSeconds = 30;
    // New groups default to DISABLED — the user must press Start on the
    // Watch pane to begin polling. This keeps freshly-created groups from
    // hammering the database in the background before the user is ready.
    [ObservableProperty] private bool   _enabled = false;

    // Preserved when editing, null for new groups. The live per-type
    // toggles on the Watch pane are the single source of truth for this
    // filter — the editor doesn't expose them to avoid two places to toggle.
    private IReadOnlyList<Base.It.Core.Models.SqlObjectType>? _existingObjectTypes;

    [ObservableProperty] private string _objectsText = "";
    [ObservableProperty] private string _validationError = "";

    public ObservableCollection<string> Environments { get; } = new();
    public ObservableCollection<string> Databases    { get; } = new();

    /// <summary>Editable target rows. At least one target is required on Save.</summary>
    public ObservableCollection<TargetEditVm> Targets { get; } = new();

    /// <summary>Non-null on successful save; otherwise null (user cancelled).</summary>
    public WatchGroup? Result { get; private set; }

    /// <summary>Invoked when the window should close after a successful save or cancel.</summary>
    public event Action? RequestClose;

    public WatchGroupEditorViewModel(AppServices svc, WatchGroup? existing)
    {
        _svc = svc;
        _existing = existing;

        foreach (var e in EnvironmentListProvider.Environments(svc)) Environments.Add(e);
        foreach (var d in EnvironmentListProvider.Databases(svc))    Databases.Add(d);

        if (existing is not null)
        {
            Name            = existing.Name;
            SourceEnv       = existing.SourceEnv;
            SourceDatabase  = existing.SourceDatabase;
            IntervalSeconds = existing.IntervalSeconds;
            Enabled         = existing.Enabled;
            ObjectsText     = string.Join(Environment.NewLine, existing.Objects);
            _existingObjectTypes = existing.ObjectTypes;

            foreach (var t in existing.Targets)
                Targets.Add(new TargetEditVm(Environments, Databases, t.Environment, t.Database));
            if (Targets.Count == 0)
                Targets.Add(BuildDefaultTarget());
        }
        else
        {
            SourceEnv      = Environments.FirstOrDefault();
            SourceDatabase = Databases.FirstOrDefault();
            Targets.Add(BuildDefaultTarget());
        }
    }

    /// <summary>
    /// Builds a new target row with sensible defaults: the database mirrors
    /// the source (common case), and the env is the first environment that
    /// isn't the source.
    /// </summary>
    private TargetEditVm BuildDefaultTarget()
    {
        var db  = SourceDatabase ?? Databases.FirstOrDefault();
        var env = Environments.FirstOrDefault(e => !string.Equals(e, SourceEnv, StringComparison.OrdinalIgnoreCase))
                  ?? Environments.FirstOrDefault();
        return new TargetEditVm(Environments, Databases, env, db);
    }

    [RelayCommand]
    private void AddTarget() => Targets.Add(BuildDefaultTarget());

    [RelayCommand]
    private void RemoveTarget(TargetEditVm? t)
    {
        if (t is null) return;
        Targets.Remove(t);
    }

    /// <summary>Opens the editor window, awaits close, returns the VM for caller to read <see cref="Result"/>.</summary>
    public static async Task<WatchGroupEditorViewModel?> ShowAsync(AppServices svc, WatchGroup? existing)
    {
        var vm = new WatchGroupEditorViewModel(svc, existing);
        var win = new WatchGroupEditorWindow { DataContext = vm };
        vm.RequestClose += () => win.Close();
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life
            && life.MainWindow is { } owner)
        {
            await win.ShowDialog(owner);
        }
        else
        {
            win.Show();
            await Task.CompletedTask;
        }
        return vm;
    }

    [RelayCommand]
    private void Save()
    {
        ValidationError = "";
        if (string.IsNullOrWhiteSpace(Name))            { ValidationError = "Name is required."; return; }
        if (string.IsNullOrWhiteSpace(SourceEnv))       { ValidationError = "Pick a source environment."; return; }
        if (string.IsNullOrWhiteSpace(SourceDatabase))  { ValidationError = "Pick a source database."; return; }
        if (Targets.Count == 0)                         { ValidationError = "Add at least one target."; return; }

        var routes = new List<TargetRoute>();
        foreach (var t in Targets)
        {
            if (string.IsNullOrWhiteSpace(t.Environment) || string.IsNullOrWhiteSpace(t.Database))
            { ValidationError = "Every target needs both an environment and a database."; return; }
            if (string.Equals(t.Environment, SourceEnv,      StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Database,    SourceDatabase, StringComparison.OrdinalIgnoreCase))
            { ValidationError = $"Target {t.Environment}·{t.Database} is the same as the source."; return; }
            routes.Add(new TargetRoute(t.Environment!, t.Database!));
        }

        // Empty objects list is valid: the watcher treats that as "every
        // object in the database" and auto-discovers on each tick.
        var objs = (ObjectsText ?? "")
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        // Object-type filter is owned by the Watch pane's section toggles;
        // the editor just round-trips whatever is already persisted (null
        // for new groups = "scan all types").
        var built = WatchGroup.Create(
            Name, SourceEnv!, SourceDatabase!, routes, objs, IntervalSeconds, Enabled, _existingObjectTypes);

        // Preserve the existing Id when editing so the store replaces in-place.
        Result = _existing is null ? built : built with { Id = _existing.Id };
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        RequestClose?.Invoke();
    }
}
