using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Base.It.App.Services;
using Base.It.Core.Config;
using Base.It.Core.Dacpac;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Base.It.App.ViewModels;

/// <summary>
/// Per-connection group-membership row shown inside the Connection details
/// expander. <see cref="IsInGroup"/> is TwoWay-bound to a CheckBox; the
/// whole collection is reconciled back into the group store on Save.
/// </summary>
public sealed partial class ConnectionRowGroupVm : ObservableObject
{
    public Guid   GroupId   { get; init; }
    [ObservableProperty] private string _groupName = "";
    [ObservableProperty] private bool   _isInGroup;
}

/// <summary>Editable version of a connection. Two-way bound to the form.</summary>
public sealed partial class ConnectionRow : ObservableObject
{
    [ObservableProperty] private string _environment = "";
    [ObservableProperty] private string _database = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _color = "";
    [ObservableProperty] private AuthMode _auth = AuthMode.RawConnectionString;
    [ObservableProperty] private string _server = "";
    [ObservableProperty] private string _databaseName = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _rawConnectionString = "";

    // ---- validation (inline) ----
    [ObservableProperty] private string _environmentError = "";
    [ObservableProperty] private string _databaseError = "";
    [ObservableProperty] private string _serverError = "";
    [ObservableProperty] private string _usernameError = "";
    [ObservableProperty] private string _passwordError = "";
    [ObservableProperty] private string _rawError = "";
    [ObservableProperty] private bool _isValid = true;

    /// <summary>Group-membership pickers for the Connection details panel.</summary>
    public ObservableCollection<ConnectionRowGroupVm> Groups { get; } = new();

    public string Label =>
        string.IsNullOrWhiteSpace(DisplayName) ? Database : DisplayName;

    public ConnectionRow() { Validate(); }
    public ConnectionRow(EnvironmentConfig c)
    {
        Environment          = c.Environment;
        Database             = c.Database;
        DisplayName          = c.DisplayName ?? "";
        Color                = c.Color ?? "";
        Auth                 = c.Auth;
        Server               = c.Server ?? "";
        DatabaseName         = c.DatabaseName ?? "";
        Username             = c.Username ?? "";
        Password             = c.Password ?? "";
        RawConnectionString  = c.ConnectionString ?? "";
        Validate();
    }

    public EnvironmentConfig ToConfig() =>
        new(Environment.Trim(), Database.Trim(), RawConnectionString.Trim())
        {
            DisplayName  = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim(),
            Color        = string.IsNullOrWhiteSpace(Color)       ? null : Color.Trim(),
            Auth         = Auth,
            Server       = string.IsNullOrWhiteSpace(Server)       ? null : Server.Trim(),
            DatabaseName = string.IsNullOrWhiteSpace(DatabaseName) ? null : DatabaseName.Trim(),
            Username     = string.IsNullOrWhiteSpace(Username)     ? null : Username.Trim(),
            Password     = string.IsNullOrEmpty(Password)          ? null : Password
        };

    /// <summary>
    /// Revalidate every field against the current values. Called on every
    /// edit so the form's error state + Save button stay in sync. Rules
    /// mirror what <see cref="SettingsViewModel.SaveAsync"/> would otherwise
    /// reject — surfacing them inline rather than after the fact.
    /// </summary>
    public void Validate()
    {
        EnvironmentError = string.IsNullOrWhiteSpace(Environment) ? "Environment is required (e.g. DEV, TEST, PROD)." : "";
        DatabaseError    = string.IsNullOrWhiteSpace(Database)    ? "Database is required (logical name used in the UI)." : "";

        if (Auth == AuthMode.RawConnectionString)
        {
            RawError      = string.IsNullOrWhiteSpace(RawConnectionString) ? "Connection string is required." : "";
            ServerError = UsernameError = PasswordError = "";
        }
        else
        {
            RawError    = "";
            ServerError = string.IsNullOrWhiteSpace(Server) ? "Server is required (hostname or host,1433)." : "";

            if (Auth == AuthMode.SqlAuth)
            {
                UsernameError = string.IsNullOrWhiteSpace(Username) ? "Username is required for SQL auth." : "";
                PasswordError = string.IsNullOrEmpty(Password)      ? "Password is required for SQL auth."  : "";
            }
            else { UsernameError = PasswordError = ""; }
        }

        IsValid =
            string.IsNullOrEmpty(EnvironmentError) &&
            string.IsNullOrEmpty(DatabaseError) &&
            string.IsNullOrEmpty(ServerError) &&
            string.IsNullOrEmpty(UsernameError) &&
            string.IsNullOrEmpty(PasswordError) &&
            string.IsNullOrEmpty(RawError);
    }

    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(Label));
    partial void OnEnvironmentChanged(string value) { OnPropertyChanged(nameof(Label)); Validate(); }
    partial void OnDatabaseChanged(string value)    { OnPropertyChanged(nameof(Label)); Validate(); }
    partial void OnAuthChanged(AuthMode value)                 => Validate();
    partial void OnServerChanged(string value)                 => Validate();
    partial void OnUsernameChanged(string value)               => Validate();
    partial void OnPasswordChanged(string value)               => Validate();
    partial void OnRawConnectionStringChanged(string value)    => Validate();
}

/// <summary>
/// One-row VM for the group-editor membership list. Each row holds an
/// (env, db) address + a bindable IsInGroup checkbox that edits the
/// currently-selected group's membership staging state.
/// </summary>
public sealed partial class ConnectionMembershipVm : ObservableObject
{
    public string  Environment { get; init; } = "";
    public string  Database    { get; init; } = "";
    public string  Label       { get; init; } = "";
    [ObservableProperty] private bool _isInGroup;
}

/// <summary>
/// Left-panel tree node: one group + the connections belonging to it.
/// Expandable, selectable. Connections in multiple groups appear under
/// each group they belong to — identity is the underlying
/// <see cref="ConnectionRow"/> (same instance everywhere).
/// </summary>
public sealed partial class ConnectionGroupNodeVm : ObservableObject
{
    public Guid    GroupId { get; }
    [ObservableProperty] private string _groupName = "";
    [ObservableProperty] private bool   _isExpanded = true;

    public ObservableCollection<ConnectionRow> Connections { get; } = new();

    public ConnectionGroupNodeVm(ConnectionGroup group)
    {
        GroupId   = group.Id;
        _groupName = group.Name;
    }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _svc;

    public event Action? ConnectionsChanged;
    /// <summary>Fired after a group is added/removed/renamed/saved so MainWindow can refresh its picker.</summary>
    public event Action? ConnectionGroupsChanged;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _legacyPath = "";
    [ObservableProperty] private ConnectionRow? _selected;

    /// <summary>
    /// Drives the Connection details expander. Auto-flipped to true whenever
    /// a row is selected (via Add or by tapping a tree node) so the editor
    /// is never invisibly collapsed behind a selected row. User can still
    /// collapse manually — it just re-opens on the next selection.
    /// </summary>
    [ObservableProperty] private bool _isConnectionEditorExpanded;

    partial void OnSelectedChanged(ConnectionRow? value)
    {
        if (value is not null) IsConnectionEditorExpanded = true;
    }

    // ---- Connection groups -------------------------------------------------
    [ObservableProperty] private ConnectionGroup? _selectedGroup;
    [ObservableProperty] private string           _groupName    = "";
    [ObservableProperty] private string           _groupStatus  = "";

    public ObservableCollection<ConnectionGroup>       Groups      { get; } = new();
    public ObservableCollection<ConnectionMembershipVm> Memberships { get; } = new();

    /// <summary>Tracks whether any groups exist so the per-row picker can hide itself when none do.</summary>
    public bool HasAnyGroup => Groups.Count > 0;

    /// <summary>Tree nodes for the left panel — one per group, each holding its connections.</summary>
    public ObservableCollection<ConnectionGroupNodeVm> GroupNodes { get; } = new();

    // DACPAC export config — loaded on construction, saved by SaveCommand.
    [ObservableProperty] private bool    _dacpacEnabled;
    [ObservableProperty] private string  _dacpacRootFolder = "";
    [ObservableProperty] private bool    _dacpacStageInGit;
    [ObservableProperty] private string  _dacpacBranchPrefix = "drift/";
    [ObservableProperty] private string  _dacpacStatus = "";

    public string StoreLocation => _svc.Connections.Location;
    public string StoreDescription =>
        _svc.Connections is DpapiConnectionStore
            ? "Windows DPAPI (CurrentUser). Only this Windows user on this machine can decrypt."
            : "Non-encrypted fallback store. Switch to Windows for secure DPAPI storage.";

    public ObservableCollection<ConnectionRow> Rows { get; } = new();

    /// <summary>Colour swatches offered in the UI. Purely cosmetic.</summary>
    public string[] Swatches { get; } = new[]
    {
        "#3478F6", "#65E68B", "#F0C75E", "#FF7070",
        "#B57BFF", "#5ECEFF", "#FF9F5E", "#8E8E93"
    };

    public AuthMode[] AuthModes { get; } = new[]
    {
        AuthMode.RawConnectionString, AuthMode.SqlAuth, AuthMode.WindowsIntegrated
    };

    // ---- theme -------------------------------------------------------------
    /// <summary>Theme preference bound to Settings → Appearance.</summary>
    [ObservableProperty] private AppSettingsStore.ThemePref _themePref;

    partial void OnThemePrefChanged(AppSettingsStore.ThemePref value)
    {
        if (_themeInitializing) return;
        _svc.Theme.Set(value);
    }

    private bool _themeInitializing;

    [RelayCommand] private void SetThemeDark()   => ThemePref = AppSettingsStore.ThemePref.Dark;
    [RelayCommand] private void SetThemeLight()  => ThemePref = AppSettingsStore.ThemePref.Light;
    [RelayCommand] private void SetThemeSystem() => ThemePref = AppSettingsStore.ThemePref.System;

    // ---- connection test ----
    [ObservableProperty] private bool _isTesting;

    // ---- backup folder ----
    /// <summary>
    /// Bound to the Settings → Backup folder field. Applied via
    /// <see cref="SaveBackupRootCommand"/> — typing here does not
    /// immediately switch the live store, so the user can correct typos
    /// before committing.
    /// </summary>
    [ObservableProperty] private string _backupRootInput = "";
    [ObservableProperty] private string _backupStatus = "";

    private void RefreshBackupStatus()
    {
        var effective = _svc.Backups.Root;
        BackupStatus = $"Active folder: {effective}. Every write uses a millisecond timestamp — existing backups are never overwritten.";
    }

    [RelayCommand]
    private async Task BrowseBackupFolderAsync()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime life ||
                life.MainWindow is null)
                return;
            var picked = await life.MainWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Pick a folder for Base.it backups"
            });
            var folder = picked?.FirstOrDefault();
            if (folder is not null) BackupRootInput = folder.Path.LocalPath;
        }
        catch (Exception ex) { BackupStatus = $"Folder picker failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void SaveBackupRoot()
    {
        try
        {
            var path = (BackupRootInput ?? "").Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                _svc.Toasts.Warning("Backup folder", "Pick a folder before saving.");
                return;
            }
            _svc.ChangeBackupRoot(path);
            RefreshBackupStatus();
            _svc.Toasts.Success("Backup folder saved", $"Future backups will go to {path}.");
            // Nudge the rest of the app (Home especially) to re-read the
            // effective backup root.
            ConnectionsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            BackupStatus = $"Couldn't use that folder: {ex.Message}";
            _svc.Toasts.Error("Backup folder failed", ex.Message);
        }
    }

    // ---- updates -----------------------------------------------------------
    /// <summary>The updater service, exposed to the view so it can bind to its
    /// observable state (State / Current / Latest / DownloadPercent).</summary>
    public UpdaterService Updater => _svc.Updater;

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (!_svc.Updater.IsInstalled)
        {
            _svc.Toasts.Info("Dev build",
                "Auto-updates only work when the app was installed via Setup.exe. You can still build and ship a release from source.");
            return;
        }
        await _svc.Updater.CheckForUpdatesAsync();
        switch (_svc.Updater.State)
        {
            case UpdateState.Available:
                _svc.Toasts.Success(
                    "Update available",
                    $"Version {_svc.Updater.LatestVersion} is out — click Download to stage it.");
                break;
            case UpdateState.UpToDate:
                _svc.Toasts.Info("You're up to date", $"Running the latest version ({_svc.Updater.CurrentVersion}).");
                break;
            case UpdateState.Failed:
                _svc.Toasts.Error("Update check failed", _svc.Updater.LastError);
                break;
        }
    }

    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        await _svc.Updater.DownloadAsync();
        if (_svc.Updater.State == UpdateState.ReadyToApply)
            _svc.Toasts.Success("Update downloaded", "Click 'Install & Restart' to apply.");
        else if (_svc.Updater.State == UpdateState.Failed)
            _svc.Toasts.Error("Download failed", _svc.Updater.LastError);
    }

    [RelayCommand]
    private void ApplyUpdate()
    {
        _svc.Updater.ApplyAndRestart();
        // Process exits here — no UI cleanup needed.
    }

    [RelayCommand]
    private void ResetBackupRoot()
    {
        try
        {
            _svc.ResetBackupRoot();
            BackupRootInput = _svc.Backups.Root;
            RefreshBackupStatus();
            _svc.Toasts.Info("Backup folder reset", $"Reverted to default: {_svc.Backups.Root}.");
            ConnectionsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            BackupStatus = $"Reset failed: {ex.Message}";
            _svc.Toasts.Error("Reset failed", ex.Message);
        }
    }

    public SettingsViewModel(AppServices svc)
    {
        _svc = svc;
        LegacyPath = ProbeLegacyPath();
        Groups.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HasAnyGroup));

        _themeInitializing = true;
        ThemePref = _svc.AppSettings.Theme;
        _themeInitializing = false;

        // Backup folder: seed the input with the live path and describe
        // the never-overwrite guarantee so the user understands what the
        // field actually controls.
        BackupRootInput = _svc.Backups.Root;
        RefreshBackupStatus();

        Load();
        _ = LoadDacpacAsync();
        _ = LoadGroupsAsync();
    }

    /// <summary>
    /// Test the currently-selected connection by opening a real SqlConnection
    /// and running SELECT 1. Reports success / failure via the global toast
    /// service so the feedback is loud and dismissable. Respects a 6s timeout
    /// so an unreachable server doesn't hang the button indefinitely.
    /// </summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (Selected is null)
        {
            _svc.Toasts.Warning("No connection selected", "Pick a connection in the left panel first.");
            return;
        }

        Selected.Validate();
        if (!Selected.IsValid)
        {
            _svc.Toasts.Error("Connection has errors", "Fix the highlighted fields before testing.");
            return;
        }

        IsTesting = true;
        try
        {
            var cfg = Selected.ToConfig();
            var cs  = cfg.BuildConnectionString();
            if (string.IsNullOrWhiteSpace(cs))
            {
                _svc.Toasts.Error("Can't build connection string", "Check the server, database and credentials.");
                return;
            }

            var csb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs) { ConnectTimeout = 6 };
            await using var conn = new Microsoft.Data.SqlClient.SqlConnection(csb.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new Microsoft.Data.SqlClient.SqlCommand("SELECT 1", conn) { CommandTimeout = 6 };
            await cmd.ExecuteScalarAsync();

            _svc.Toasts.Success(
                "Connection OK",
                $"{Selected.Environment} · {Selected.Database} — reachable.");
        }
        catch (Exception ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            _svc.Toasts.Error("Connection failed", msg);
        }
        finally { IsTesting = false; }
    }

    // ---- Connection groups -------------------------------------------------

    /// <summary>Reload the groups from disk into the bindable collection.</summary>
    private async Task LoadGroupsAsync()
    {
        try
        {
            // First-run bootstrap: if connections exist without groups, a
            // "Default" group is seeded with every connection so the tree
            // view always has at least one node.
            await _svc.EnsureDefaultConnectionGroupAsync();
            Groups.Clear();
            foreach (var g in _svc.ConnectionGroups.All.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
                Groups.Add(g);
            SelectedGroup = Groups.FirstOrDefault();
            RebuildGroupNodes();
            RefreshRowGroupPickers(overwriteFromStore: true);
            GroupStatus = Groups.Count == 0
                ? "No groups yet. Create one to filter the pickers by project."
                : $"{Groups.Count} group(s).";
        }
        catch (Exception ex) { GroupStatus = $"Couldn't load groups: {ex.Message}"; }
    }

    /// <summary>
    /// Rebuild the left-panel tree: one node per group, each populated
    /// with the <see cref="ConnectionRow"/> instances that match the
    /// group's member keys. The same row may appear under multiple
    /// groups (multi-membership is allowed).
    ///
    /// Un-saved rows (just added via +Conn) are also surfaced into their
    /// pre-checked groups via the per-row picker state. Without this the
    /// new row is in <see cref="Rows"/> but in no ListBox's items, which
    /// causes the tree's TwoWay <see cref="Selected"/> binding to null
    /// itself out when Add sets Selected = row, and the editor vanishes.
    /// </summary>
    private void RebuildGroupNodes()
    {
        // Preserve each node's expanded state across rebuilds by id.
        var wasExpanded = GroupNodes.ToDictionary(n => n.GroupId, n => n.IsExpanded);
        GroupNodes.Clear();

        foreach (var g in Groups)
        {
            var node = new ConnectionGroupNodeVm(g)
            {
                IsExpanded = wasExpanded.TryGetValue(g.Id, out var exp) ? exp : true
            };
            var keySet = new HashSet<string>(g.ConnectionKeys, StringComparer.Ordinal);
            foreach (var row in Rows)
            {
                var key = ConnectionGroup.KeyFor(row.Environment, row.Database);
                var inStore      = keySet.Contains(key);
                var pickerSaysIn = row.Groups.Any(p => p.GroupId == g.Id && p.IsInGroup);
                if (inStore || pickerSaysIn)
                    node.Connections.Add(row);
            }
            GroupNodes.Add(node);
        }
    }

    /// <summary>
    /// When the selected group flips we rebuild the membership list and
    /// mirror <see cref="ConnectionGroup.Name"/> into the bindable
    /// <see cref="GroupName"/> text box.
    /// </summary>
    partial void OnSelectedGroupChanged(ConnectionGroup? value)
    {
        GroupName = value?.Name ?? "";
        RebuildMemberships();
    }

    /// <summary>Recompute the membership checkboxes against the currently-selected group.</summary>
    private void RebuildMemberships()
    {
        Memberships.Clear();
        var active = SelectedGroup;
        foreach (var c in _svc.Connections.Load().OrderBy(c => c.Environment, StringComparer.OrdinalIgnoreCase)
                                                  .ThenBy (c => c.Database,    StringComparer.OrdinalIgnoreCase))
        {
            var label = !string.IsNullOrWhiteSpace(c.DisplayName) ? c.DisplayName! : $"{c.Environment} · {c.Database}";
            Memberships.Add(new ConnectionMembershipVm
            {
                Environment = c.Environment,
                Database    = c.Database,
                Label       = label,
                IsInGroup   = active is not null &&
                              active.Contains(c.Environment, c.Database)
            });
        }
    }

    [RelayCommand]
    private void AddGroup()
    {
        var g = ConnectionGroup.Create($"Group {Groups.Count + 1}", Array.Empty<string>());
        _svc.ConnectionGroups.Upsert(g);
        Groups.Add(g);
        SelectedGroup = g;
        RefreshRowGroupPickers();
        GroupStatus = "New group — fill in a name, tick connections, click Save.";
    }

    [RelayCommand]
    private void RemoveGroup()
    {
        if (SelectedGroup is null) return;
        var id = SelectedGroup.Id;
        _svc.ConnectionGroups.Remove(id);
        Groups.Remove(SelectedGroup);
        SelectedGroup = Groups.FirstOrDefault();
        RefreshRowGroupPickers();
    }

    [RelayCommand]
    private async Task SaveGroupsAsync()
    {
        if (SelectedGroup is null) { GroupStatus = "Nothing to save — pick or add a group first."; return; }

        var name = (GroupName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) { GroupStatus = "Group name is required."; return; }

        // Enforce unique group names (case-insensitive).
        var dup = Groups.Any(g => g.Id != SelectedGroup.Id &&
                                   string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        if (dup) { GroupStatus = $"Another group already has the name '{name}'."; return; }

        var keys = Memberships.Where(m => m.IsInGroup)
                              .Select(m => ConnectionGroup.KeyFor(m.Environment, m.Database))
                              .ToList();
        var updated = SelectedGroup with { Name = name, ConnectionKeys = keys };

        _svc.ConnectionGroups.Upsert(updated);
        await _svc.ConnectionGroups.SaveAsync();

        // Replace the in-memory record so the binding sees the new name.
        var idx = Groups.IndexOf(SelectedGroup);
        if (idx >= 0) Groups[idx] = updated;
        SelectedGroup = updated;

        GroupStatus = $"Saved '{name}' with {keys.Count} connection(s).";
        _svc.Toasts.Success("Group saved", $"'{name}' · {keys.Count} connection(s).");
        RebuildGroupNodes();
        RefreshRowGroupPickers(overwriteFromStore: true);
        ConnectionGroupsChanged?.Invoke();
    }

    [RelayCommand]
    private async Task AddGroupAndRefreshAsync()
    {
        AddGroup();
        await _svc.ConnectionGroups.SaveAsync();
        RebuildGroupNodes();
        ConnectionGroupsChanged?.Invoke();
    }

    /// <summary>
    /// Persist an inline-rename from the left-panel tree. Validates that
    /// the name is non-empty and unique (case-insensitive) against other
    /// groups; silently reverts when either check fails so the tree and
    /// the store never diverge.
    /// </summary>
    public async Task RenameGroupFromTreeAsync(Guid groupId, string? newName)
    {
        var name = (newName ?? "").Trim();
        var existing = Groups.FirstOrDefault(g => g.Id == groupId);
        if (existing is null) return;

        // Unchanged? don't touch disk.
        if (string.Equals(existing.Name, name, StringComparison.Ordinal)) return;

        // Validate: non-empty + unique. On failure, snap the tree node
        // back to the persisted name so the UI never shows a stale value.
        var node = GroupNodes.FirstOrDefault(n => n.GroupId == groupId);
        if (string.IsNullOrWhiteSpace(name))
        {
            GroupStatus = "Group name is required.";
            if (node is not null) node.GroupName = existing.Name;
            return;
        }
        var dup = Groups.Any(g => g.Id != groupId &&
                                   string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        if (dup)
        {
            GroupStatus = $"Another group already has the name '{name}'.";
            if (node is not null) node.GroupName = existing.Name;
            return;
        }

        var updated = existing with { Name = name };
        _svc.ConnectionGroups.Upsert(updated);
        await _svc.ConnectionGroups.SaveAsync();

        var idx = Groups.IndexOf(existing);
        if (idx >= 0) Groups[idx] = updated;
        if (ReferenceEquals(SelectedGroup, existing)) SelectedGroup = updated;
        // Mirror the new name into the editor textbox if this group is
        // selected in the right panel.
        if (SelectedGroup?.Id == groupId) GroupName = updated.Name;

        // Mirror the new name into every row's per-connection picker so
        // the checkbox label updates without a full rebuild.
        foreach (var row in Rows)
            foreach (var gm in row.Groups)
                if (gm.GroupId == groupId) gm.GroupName = name;

        GroupStatus = $"Renamed to '{name}'.";
        ConnectionGroupsChanged?.Invoke();
    }

    private async Task LoadDacpacAsync()
    {
        try
        {
            var opts = await _svc.DacpacOptions.LoadAsync();
            DacpacEnabled      = opts.Enabled;
            DacpacRootFolder   = opts.RootFolder;
            DacpacStageInGit   = opts.StageInGit;
            DacpacBranchPrefix = string.IsNullOrWhiteSpace(opts.BranchPrefix) ? "drift/" : opts.BranchPrefix;
            RefreshDacpacStatus();
        }
        catch (Exception ex) { DacpacStatus = $"Couldn't load DACPAC config: {ex.Message}"; }
    }

    private void RefreshDacpacStatus()
    {
        if (!DacpacEnabled) { DacpacStatus = "Disabled. Flip 'Enabled' and point at an SSDT folder to activate."; return; }
        if (string.IsNullOrWhiteSpace(DacpacRootFolder)) { DacpacStatus = "Pick a DACPAC root folder."; return; }
        if (!Directory.Exists(DacpacRootFolder))         { DacpacStatus = "Folder does not exist yet — will be created on save."; return; }
        DacpacStatus = DacpacStageInGit
            ? "Ready — each batch/watch export will also create a git branch and stage files. No commits."
            : "Ready — files will be written but no git actions are performed.";
    }

    partial void OnDacpacEnabledChanged(bool value)       => RefreshDacpacStatus();
    partial void OnDacpacRootFolderChanged(string value)  => RefreshDacpacStatus();
    partial void OnDacpacStageInGitChanged(bool value)    => RefreshDacpacStatus();

    [RelayCommand]
    private async Task BrowseDacpacFolderAsync()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime life ||
                life.MainWindow is null)
                return;
            var picked = await life.MainWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Pick the DACPAC / SSDT root folder"
            });
            var folder = picked?.FirstOrDefault();
            if (folder is not null) DacpacRootFolder = folder.Path.LocalPath;
        }
        catch (Exception ex) { DacpacStatus = $"Folder picker failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task SaveDacpacAsync()
    {
        try
        {
            // Create the folder if it doesn't exist — the exporter's IsUsable
            // check requires the directory to be present.
            if (DacpacEnabled && !string.IsNullOrWhiteSpace(DacpacRootFolder))
            {
                try { Directory.CreateDirectory(DacpacRootFolder); }
                catch (Exception ex) { DacpacStatus = $"Couldn't create folder: {ex.Message}"; return; }
            }

            var opts = new DacpacExportOptions(
                Enabled:     DacpacEnabled,
                RootFolder:  DacpacRootFolder ?? "",
                StageInGit:  DacpacStageInGit,
                BranchPrefix: string.IsNullOrWhiteSpace(DacpacBranchPrefix) ? "drift/" : DacpacBranchPrefix);
            await _svc.DacpacOptions.SaveAsync(opts);
            DacpacStatus = "DACPAC settings saved.";
            _svc.Toasts.Success("DACPAC settings saved",
                DacpacEnabled ? $"Exports will be written to {DacpacRootFolder}." : "Disabled — no files will be written.");
            // Piggy-back on the connections-changed broadcast so the
            // Batch pane refreshes its DACPAC availability flag.
            ConnectionsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            DacpacStatus = $"Save failed: {ex.Message}";
            _svc.Toasts.Error("DACPAC save failed", ex.Message);
        }
    }

    [RelayCommand]
    public void Load()
    {
        Rows.Clear();
        foreach (var e in _svc.Connections.Load())
            Rows.Add(new ConnectionRow(e));
        Selected = Rows.FirstOrDefault();
        Status   = $"{Rows.Count} connection(s).";
        RebuildGroupNodes();
        // If groups are already hydrated (subsequent Load after the
        // initial async group load finished) the pickers populate
        // immediately; otherwise LoadGroupsAsync will fill them in when
        // it completes.
        RefreshRowGroupPickers(overwriteFromStore: true);
    }

    [RelayCommand]
    private void Add()
    {
        // Blank env + db: hardcoded defaults like "DEV"/"Portal" bleed
        // through as if the product shipped with a specific tenant, which
        // is confusing for new users.
        var row = new ConnectionRow
        {
            Environment = "",
            Database    = "",
            Auth        = AuthMode.SqlAuth,
            Color       = Swatches[Rows.Count % Swatches.Length]
        };

        // Which group gets the default check-mark in the new row's picker.
        // Priority: the group the user is currently editing > the active
        // group set by the top-bar > a group named "Default" > the first
        // group alphabetically. The user can tick more in the picker
        // before clicking Save all.
        var preferredId = SelectedGroup?.Id
            ?? _svc.ConnectionGroups.ActiveGroupId
            ?? _svc.ConnectionGroups.All.FirstOrDefault(g => string.Equals(g.Name, "Default", StringComparison.OrdinalIgnoreCase))?.Id
            ?? Groups.FirstOrDefault()?.Id;

        foreach (var g in Groups)
        {
            row.Groups.Add(new ConnectionRowGroupVm
            {
                GroupId   = g.Id,
                GroupName = g.Name,
                IsInGroup = g.Id == preferredId
            });
        }

        Rows.Add(row);
        // Rebuild the tree BEFORE assigning Selected so the new row is
        // already in the preferred group's ListBox items when the TwoWay
        // SelectedItem binding propagates — otherwise the ListBox nulls
        // SelectedItem (row not in its items) and that null flows back
        // into Selected, hiding the editor section.
        RebuildGroupNodes();
        Selected = row;
        var pre = Groups.FirstOrDefault(g => g.Id == preferredId)?.Name;
        Status = Groups.Count == 0
            ? "Fill in the details, then Save."
            : $"Fill in the details and tick the groups this connection belongs to (pre-selected: {pre ?? "none"}), then Save all.";
    }

    /// <summary>
    /// Rebuild each row's per-connection group picker to match the current
    /// <see cref="Groups"/> collection. Entries for groups that no longer
    /// exist drop out; new groups arrive unchecked; existing entries keep
    /// their IsInGroup state unless <paramref name="overwriteFromStore"/>
    /// is true, in which case IsInGroup is re-read from the persisted
    /// <see cref="ConnectionGroup.ConnectionKeys"/>.
    /// </summary>
    private void RefreshRowGroupPickers(bool overwriteFromStore = false)
    {
        foreach (var row in Rows)
        {
            var key = ConnectionGroup.KeyFor(row.Environment, row.Database);

            for (int i = row.Groups.Count - 1; i >= 0; i--)
            {
                if (!Groups.Any(g => g.Id == row.Groups[i].GroupId))
                    row.Groups.RemoveAt(i);
            }

            foreach (var g in Groups)
            {
                var existing = row.Groups.FirstOrDefault(x => x.GroupId == g.Id);
                if (existing is null)
                {
                    row.Groups.Add(new ConnectionRowGroupVm
                    {
                        GroupId   = g.Id,
                        GroupName = g.Name,
                        IsInGroup = g.ConnectionKeys.Contains(key, StringComparer.Ordinal)
                    });
                }
                else
                {
                    existing.GroupName = g.Name;
                    if (overwriteFromStore)
                        existing.IsInGroup = g.ConnectionKeys.Contains(key, StringComparer.Ordinal);
                }
            }
        }
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is null) return;
        Rows.Remove(Selected);
        Selected = Rows.FirstOrDefault();
        Status = "Removed (click Save to persist).";
        RebuildGroupNodes();
    }

    /// <summary>
    /// Delete a specific connection row with a confirmation dialog. Used by
    /// the trash icon inline on each tree row. Persists the connection store
    /// immediately (unlike the bulk −Conn button, which defers to Save all)
    /// so the row doesn't silently reappear on reload.
    /// </summary>
    [RelayCommand]
    private async Task DeleteConnectionAsync(ConnectionRow? row)
    {
        if (row is null) return;
        var ok = await ConfirmDialog.AskAsync(
            "Delete connection?",
            $"Remove '{row.Label}' ({row.Environment}/{row.Database})? It'll be dropped from every group. Backups already on disk are kept.",
            primaryText: "Delete");
        if (!ok) return;

        Rows.Remove(row);
        if (ReferenceEquals(Selected, row)) Selected = Rows.FirstOrDefault();

        // Persist immediately so reload doesn't resurrect the row.
        try
        {
            var valid = Rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Environment) && !string.IsNullOrWhiteSpace(r.Database))
                .Select(r => r.ToConfig()).ToList();
            _svc.Connections.Save(valid);
            await ReconcileGroupMembershipsAsync();
            ConnectionsChanged?.Invoke();
            ConnectionGroupsChanged?.Invoke();
            RebuildGroupNodes();
            Status = $"Deleted {row.Label}.";
            _svc.Toasts.Success("Connection deleted", row.Label);
        }
        catch (Exception ex)
        {
            Status = $"Delete failed: {ex.Message}";
            _svc.Toasts.Error("Delete failed", ex.Message);
        }
    }

    /// <summary>
    /// Delete a group with confirmation. Members keep their rows — the
    /// connections aren't deleted, just the group membership. Any orphans
    /// fall into Default (auto-created) so they stay discoverable.
    /// </summary>
    [RelayCommand]
    private async Task DeleteGroupAsync(Guid groupId)
    {
        var group = Groups.FirstOrDefault(g => g.Id == groupId);
        if (group is null) return;

        var ok = await ConfirmDialog.AskAsync(
            "Delete group?",
            $"Delete the '{group.Name}' group? Connections inside it stay, they just lose this group tag. Orphans drop into the Default group.",
            primaryText: "Delete");
        if (!ok) return;

        _svc.ConnectionGroups.Remove(groupId);
        Groups.Remove(group);
        await _svc.ConnectionGroups.SaveAsync();
        await _svc.EnsureDefaultConnectionGroupAsync();

        // Reload groups + rebuild tree + reconcile row pickers.
        Groups.Clear();
        foreach (var g in _svc.ConnectionGroups.All.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            Groups.Add(g);
        SelectedGroup = Groups.FirstOrDefault();
        RefreshRowGroupPickers(overwriteFromStore: true);
        RebuildGroupNodes();

        Status = $"Deleted group '{group.Name}'.";
        _svc.Toasts.Success("Group deleted", group.Name);
        ConnectionGroupsChanged?.Invoke();
    }

    /// <summary>
    /// "+" on a group header: adds a new connection row with that group
    /// pre-ticked in the picker, selects it so the editor is visible.
    /// </summary>
    [RelayCommand]
    private void AddConnectionToGroup(Guid groupId)
    {
        var group = Groups.FirstOrDefault(g => g.Id == groupId);

        var row = new ConnectionRow
        {
            Environment = "",
            Database    = "",
            Auth        = AuthMode.SqlAuth,
            Color       = Swatches[Rows.Count % Swatches.Length]
        };

        foreach (var g in Groups)
        {
            row.Groups.Add(new ConnectionRowGroupVm
            {
                GroupId   = g.Id,
                GroupName = g.Name,
                IsInGroup = g.Id == groupId
            });
        }

        Rows.Add(row);
        RebuildGroupNodes();
        Selected = row;
        IsConnectionEditorExpanded = true;
        Status = group is null
            ? "New connection added. Fill in the details, then Save."
            : $"New connection pre-assigned to '{group.Name}'. Fill in the details, then Save.";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            // Cross-row validation: revalidate every row so the inline
            // errors are fresh, then reject the save if any row is invalid.
            foreach (var r in Rows) r.Validate();
            var firstInvalid = Rows.FirstOrDefault(r => !r.IsValid);
            if (firstInvalid is not null)
            {
                Selected = firstInvalid;
                IsConnectionEditorExpanded = true;
                var msg = "One or more connections have missing or invalid fields — see the highlighted entries.";
                Status = msg;
                _svc.Toasts.Error("Can't save", msg);
                return;
            }

            var totalRows = Rows.Count;
            var skippedBlank = Rows.Count(r =>
                string.IsNullOrWhiteSpace(r.Environment) || string.IsNullOrWhiteSpace(r.Database));

            var valid = Rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Environment) && !string.IsNullOrWhiteSpace(r.Database))
                .Select(r => r.ToConfig())
                .ToList();

            if (skippedBlank > 0)
            {
                var msg = $"Can't save — {skippedBlank} row(s) are missing Environment or Database. Fill them in first.";
                Status = msg;
                _svc.Toasts.Error("Missing fields", msg);
                return;
            }

            // Display names must be unique across the whole connection
            // store: target pickers show the display name alone, so two
            // connections with the same label would be indistinguishable
            // in Watch/Sync/Batch dropdowns.
            var dupDisplay = valid
                .Where(c => !string.IsNullOrWhiteSpace(c.DisplayName))
                .GroupBy(c => c.DisplayName!.Trim(), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (dupDisplay is not null)
            {
                var msg = $"Display name '{dupDisplay.Key}' is used on {dupDisplay.Count()} connections. Each display name must be unique.";
                Status = msg;
                _svc.Toasts.Error("Duplicate display name", msg);
                return;
            }

            // (env, database) must also be unique — the store keys on that
            // pair, so duplicates silently clobber each other.
            var dupKey = valid
                .GroupBy(c => $"{c.Environment}|{c.Database}", StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (dupKey is not null)
            {
                var msg = $"Two connections share the same (environment, database): {dupKey.Key.Replace('|', '/')}.";
                Status = msg;
                _svc.Toasts.Error("Duplicate connection key", msg);
                return;
            }

            // Compare against disk to surface whether this save is net-adds
            // or just updates — so the user can see the new row being
            // persisted when they report "nothing saved".
            var previousKeys = _svc.Connections.Load()
                .Select(c => $"{c.Environment}|{c.Database}".ToUpperInvariant())
                .ToHashSet();
            var newKeys = valid
                .Select(c => $"{c.Environment}|{c.Database}".ToUpperInvariant())
                .ToList();
            var added   = newKeys.Count(k => !previousKeys.Contains(k));
            var updated = newKeys.Count(k =>  previousKeys.Contains(k));

            _svc.Connections.Save(valid);
            Status = $"Saved {valid.Count} connection(s) — {added} new, {updated} updated (encrypted, {totalRows} row(s) in editor).";
            _svc.Toasts.Success(
                "Connections saved",
                $"{valid.Count} total · {added} new · {updated} updated.");

            // Reconcile group memberships from each row's per-connection
            // picker, then rescue orphans into Default. Awaiting properly —
            // the sync-over-async path here previously froze the app.
            await ReconcileGroupMembershipsAsync();

            ConnectionsChanged?.Invoke();
            ConnectionGroupsChanged?.Invoke();
            RebuildMemberships();
            RebuildGroupNodes();
        }
        catch (Exception ex)
        {
            // Surface the inner-most message — DpapiConnectionStore and
            // JsonSerializer both wrap real faults in generic messages, so
            // the outermost ex.Message is often uninformative ("One or
            // more errors occurred").
            var inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            var failMsg = $"Save failed: {inner.GetType().Name}: {inner.Message}";
            Status = failMsg;
            _svc.Toasts.Error("Save failed", inner.Message);
        }
    }

    /// <summary>
    /// Rewrite every group's <see cref="ConnectionGroup.ConnectionKeys"/>
    /// from the per-row Groups pickers. Rows whose env/db is empty are
    /// skipped. After persisting, any connection that ended up in no group
    /// is rescued into "Default" (created if absent) so pickers elsewhere
    /// in the app never hide a connection entirely.
    ///
    /// Race safety: if a row has no picker entry for a given group (e.g.
    /// Save was pressed before LoadGroupsAsync populated the picker), we
    /// fall back to the currently-persisted membership for that row instead
    /// of wiping it. Only explicit picker edits ever flip a row out of a
    /// group — a missing picker entry is treated as "no change".
    /// </summary>
    private async Task ReconcileGroupMembershipsAsync()
    {
        foreach (var group in _svc.ConnectionGroups.All.ToList())
        {
            var previousKeys = new HashSet<string>(group.ConnectionKeys, StringComparer.Ordinal);
            var keys = new List<string>();
            foreach (var row in Rows)
            {
                if (string.IsNullOrWhiteSpace(row.Environment) || string.IsNullOrWhiteSpace(row.Database))
                    continue;
                var key = ConnectionGroup.KeyFor(row.Environment, row.Database);
                var m = row.Groups.FirstOrDefault(x => x.GroupId == group.Id);
                bool include = m is null
                    ? previousKeys.Contains(key)
                    : m.IsInGroup;
                if (include) keys.Add(key);
            }
            _svc.ConnectionGroups.Upsert(group with { ConnectionKeys = keys });
        }
        await _svc.ConnectionGroups.SaveAsync();

        // Orphan rescue — guarantees every connection is in ≥1 group.
        await _svc.EnsureDefaultConnectionGroupAsync();

        // Reload in-memory groups and re-sync each row's picker with the
        // post-rescue state (Default may have just been auto-created /
        // auto-populated).
        Groups.Clear();
        foreach (var g in _svc.ConnectionGroups.All.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            Groups.Add(g);
        RefreshRowGroupPickers(overwriteFromStore: true);
    }

    [RelayCommand]
    private void ImportLegacy()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LegacyPath) || !File.Exists(LegacyPath))
            {
                Status = "Point 'Source file' at the old DB_Sync appsettings.json.";
                return;
            }
            var result = ConnectionImporter.ImportFromLegacy(LegacyPath, _svc.Connections);
            Load();
            ConnectionsChanged?.Invoke();
            Status = $"Imported {result.Imported} (skipped {result.Skipped}) from {Path.GetFileName(LegacyPath)}.";
            _svc.Toasts.Success("Legacy import",
                $"Imported {result.Imported} · skipped {result.Skipped} · from {Path.GetFileName(LegacyPath)}.");
        }
        catch (Exception ex)
        {
            Status = $"Import failed: {ex.Message}";
            _svc.Toasts.Error("Import failed", ex.Message);
        }
    }

    [RelayCommand]
    private void ApplyColor(string? hex)
    {
        if (Selected is null || string.IsNullOrWhiteSpace(hex)) return;
        Selected.Color = hex;
    }

    private static string ProbeLegacyPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DB_Sync", "appsettings.json"),
        };
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { }
        }
        return "";
    }
}
