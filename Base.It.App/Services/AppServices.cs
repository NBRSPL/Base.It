using Base.It.Core.Abstractions;
using Base.It.Core.Backup;
using Base.It.Core.Config;
using Base.It.Core.Dacpac;
using Base.It.Core.Drift;
using Base.It.Core.Logging;
using Base.It.Core.Models;
using Base.It.Core.Query;
using Base.It.Core.Sql;
using Base.It.Core.Sync;

namespace Base.It.App.Services;

/// <summary>
/// Composition root. Picks a secure connection store (DPAPI on Windows),
/// keeps user data under %LOCALAPPDATA%\Base.It so nothing sensitive lives
/// beside the binary. Every writable path resolves at runtime from the
/// user's machine — nothing is hardcoded to a specific drive letter or
/// project directory, so cloning the repo on a fresh machine Just Works.
/// </summary>
public sealed class AppServices
{
    public string UserDataRoot { get; }
    public IConnectionStore Connections { get; }
    public SqlObjectScripter Scripter { get; }
    public FileBackupStore Backups { get; }
    public FileLogger Logger { get; }
    public SyncService Sync { get; }
    public BackupService Backup { get; }
    public QueryService Query { get; }

    // UI-only services: theme + toast notifications.
    public AppSettingsStore AppSettings { get; }
    public ThemeService Theme { get; }
    public ToastService Toasts { get; }

    /// <summary>Velopack-backed updater. Null-safe when running from a dev build.</summary>
    public UpdaterService Updater { get; }

    // Drift / Watch.
    public DriftDetector Drift { get; }
    public WatchGroupStore WatchGroups { get; }

    // Connection groups: logical bundles of connections, one active at a time.
    public ConnectionGroupStore ConnectionGroups { get; }

    // DACPAC export.
    public DacpacExportStore DacpacOptions { get; }

    /// <summary>Raised after the active connection group changes so VMs can re-pull filtered environment lists.</summary>
    public event Action? ActiveConnectionGroupChanged;

    public AppServices()
    {
        UserDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Base.It");
        Directory.CreateDirectory(UserDataRoot);

        Connections = OperatingSystem.IsWindows()
            ? new DpapiConnectionStore()                                   // encrypted per-user
            : new InMemoryConnectionStore();                               // non-Windows fallback

        Scripter = new SqlObjectScripter();

        // Load user preferences BEFORE the backup store so a persisted
        // custom backup folder can be honored at startup.
        AppSettings = new AppSettingsStore(UserDataRoot);

        // Backup root resolution order:
        //   1. User setting (Settings → Backup folder) — strongest
        //   2. BASEIT_BACKUP_ROOT env var — for CI / alternate setups
        //   3. C:\DB_Backup — shared default on Windows
        //   4. %LOCALAPPDATA%\Base.It\Backups — fallback if nothing else works
        // None of these paths ever overwrite: FileBackupStore names every
        // write with a millisecond timestamp plus a collision counter.
        var backupRoot = AppSettings.BackupRoot;
        if (string.IsNullOrWhiteSpace(backupRoot))
            backupRoot = Environment.GetEnvironmentVariable("BASEIT_BACKUP_ROOT");
        if (string.IsNullOrWhiteSpace(backupRoot))
        {
            const string SharedBackupRoot = @"C:\DB_Backup";
            try
            {
                Directory.CreateDirectory(SharedBackupRoot);
                backupRoot = SharedBackupRoot;
            }
            catch
            {
                backupRoot = Path.Combine(UserDataRoot, "Backups");
            }
        }
        else
        {
            // User-chosen path: best-effort ensure it exists. If we can't
            // create it, fall back rather than crashing — the Settings UI
            // surfaces the error the next time the user opens it.
            try { Directory.CreateDirectory(backupRoot); }
            catch { backupRoot = Path.Combine(UserDataRoot, "Backups"); }
        }
        Backups  = new FileBackupStore(backupRoot);

        Logger   = new FileLogger(Path.Combine(UserDataRoot, "Logs"));
        Sync     = new SyncService(Scripter, Backups, Logger);
        Backup   = new BackupService(Scripter, Backups, Logger);
        Query    = new QueryService();

        // Parallelism = 2 for the watcher's catalog fetches — continuous
        // polling load stays light regardless of group size.
        Drift            = new DriftDetector(Scripter, maxParallelism: 2);
        WatchGroups      = new WatchGroupStore     (Path.Combine(UserDataRoot, "watchgroups.json"));
        ConnectionGroups = new ConnectionGroupStore(Path.Combine(UserDataRoot, "connectiongroups.json"));

        DacpacOptions = new DacpacExportStore(Path.Combine(UserDataRoot, "dacpac.json"));

        // UI services — theme + toasts. AppSettings was constructed earlier
        // (above) so the backup-root lookup could use it. Theme loads the
        // persisted preference but does NOT apply it here (Application
        // isn't ready yet); MainWindow calls Theme.ApplyFromSettings() once
        // the app is up.
        Theme       = new ThemeService(AppSettings);
        Toasts      = new ToastService();
        Updater     = new UpdaterService();
    }

    /// <summary>
    /// Apply a user-chosen backup folder at runtime. Creates the folder if
    /// needed, persists the choice in <see cref="AppSettings"/>, and
    /// re-points the running <see cref="Backups"/> store. Existing backups
    /// at the old path are never touched — only future writes go to the
    /// new folder. Throws if the path is blank or can't be created.
    /// </summary>
    public void ChangeBackupRoot(string newRoot)
    {
        Backups.SetRoot(newRoot);          // throws on bad path / permission issue
        AppSettings.BackupRoot = newRoot;  // only persisted once SetRoot succeeded
    }

    /// <summary>
    /// Clear the user-chosen backup folder and revert to the resolved
    /// default (env var → C:\DB_Backup → per-user fallback).
    /// </summary>
    public void ResetBackupRoot()
    {
        var fallback = Environment.GetEnvironmentVariable("BASEIT_BACKUP_ROOT");
        if (string.IsNullOrWhiteSpace(fallback))
        {
            const string SharedBackupRoot = @"C:\DB_Backup";
            try { Directory.CreateDirectory(SharedBackupRoot); fallback = SharedBackupRoot; }
            catch { fallback = Path.Combine(UserDataRoot, "Backups"); }
        }
        Backups.SetRoot(fallback!);
        AppSettings.BackupRoot = null;
    }

    /// <summary>
    /// Switch the active connection group. <paramref name="id"/> null means
    /// "no group active" — pickers show every configured connection.
    /// Persists the change and fires <see cref="ActiveConnectionGroupChanged"/>.
    /// </summary>
    public async Task SetActiveConnectionGroupAsync(Guid? id)
    {
        ConnectionGroups.SetActive(id);
        await ConnectionGroups.SaveAsync();
        ActiveConnectionGroupChanged?.Invoke();
    }

    /// <summary>
    /// First-run bootstrap + orphan rescue. Guarantees that:
    ///   1. if there are any connections configured, at least one group exists
    ///      (a "Default" group is created automatically if none does), and
    ///   2. every connection is a member of at least one group — orphans
    ///      are added to the Default group so they remain discoverable via
    ///      the active-group filter.
    /// Idempotent. Call whenever the connection or group set changes.
    /// </summary>
    public async Task EnsureDefaultConnectionGroupAsync()
    {
        // Ensure both stores have been loaded at least once.
        await ConnectionGroups.LoadAsync();

        var connections = Connections.Load();
        if (connections.Count == 0) return;

        var groups = ConnectionGroups.All;

        // (1) No groups at all → create "Default" seeded with every known connection.
        if (groups.Count == 0)
        {
            var keys = connections
                .Select(c => ConnectionGroup.KeyFor(c.Environment, c.Database))
                .ToList();
            var def = ConnectionGroup.Create("Default", keys);
            ConnectionGroups.Upsert(def);
            ConnectionGroups.SetActive(def.Id);
            await ConnectionGroups.SaveAsync();
            ActiveConnectionGroupChanged?.Invoke();
            return;
        }

        // (2) Rescue orphans — connections that don't appear in any group.
        var covered = new HashSet<string>(
            groups.SelectMany(g => g.ConnectionKeys),
            StringComparer.Ordinal);
        var orphans = connections
            .Select(c => ConnectionGroup.KeyFor(c.Environment, c.Database))
            .Where(k => !covered.Contains(k))
            .ToList();

        if (orphans.Count == 0) return;

        var defGroup = groups.FirstOrDefault(g => string.Equals(g.Name, "Default", StringComparison.OrdinalIgnoreCase))
                    ?? groups[0]; // fall back to the first group if no "Default" is named

        var merged = defGroup.ConnectionKeys.Concat(orphans).Distinct(StringComparer.Ordinal).ToList();
        ConnectionGroups.Upsert(defGroup with { ConnectionKeys = merged });
        await ConnectionGroups.SaveAsync();
    }

    public async Task<DacpacExporter?> TryBuildDacpacExporterAsync()
    {
        var opts = await DacpacOptions.LoadAsync();
        return opts.IsUsable ? new DacpacExporter(opts) : null;
    }

    /// <summary>
    /// Result of a single DACPAC export step.
    /// <see cref="Path"/> is the absolute file actually written (or null
    /// when nothing was exported). <see cref="ExistedBefore"/> tells the
    /// caller whether that file pre-existed in the SSDT tree, so it can
    /// distinguish "updated" from "created (new)" in run logs without
    /// re-querying the filesystem after the write.
    /// </summary>
    public readonly record struct DacpacExportResult(string? Path, bool ExistedBefore);

    /// <summary>
    /// Centralised DACPAC export step shared by Sync, Batch and Watch.
    /// Fetches the object's DACPAC-shaped definition and routes it to the
    /// right SSDT file. The trigger-inline policy lives here: when a
    /// trigger has no existing standalone file in the SSDT tree, its
    /// definition is folded into the parent table's file (re-emitting the
    /// table's full DACPAC script, which already inlines every bound
    /// trigger). Existing standalone trigger files keep being updated in
    /// place so a team's chosen layout isn't disturbed.
    /// </summary>
    public async Task<DacpacExportResult> ExportToDacpacAsync(
        DacpacExporter exporter,
        string connectionString,
        Base.It.Core.Models.ObjectIdentifier id,
        CancellationToken ct = default)
    {
        var src = await Scripter.GetObjectForDacpacAsync(connectionString, id, ct);
        if (src is null) return new DacpacExportResult(null, false);

        if (src.Type == Base.It.Core.Models.SqlObjectType.Trigger
            && !exporter.HasExistingFile(id))
        {
            var parent = await Scripter.GetTriggerParentAsync(connectionString, id, ct);
            if (parent is not null)
            {
                var parentObj = await Scripter.GetObjectForDacpacAsync(connectionString, parent.Value, ct);
                if (parentObj is not null)
                {
                    var existed = exporter.HasExistingFile(parent.Value);
                    var path    = exporter.Export(parentObj.Id, parentObj.Type, parentObj.Definition);
                    return new DacpacExportResult(path, existed);
                }
            }
            // Parent lookup failed — fall through to write the trigger as
            // its own file rather than dropping the export entirely.
        }

        var existedSrc = exporter.HasExistingFile(src.Id);
        var pathSrc    = exporter.Export(src.Id, src.Type, src.Definition);
        return new DacpacExportResult(pathSrc, existedSrc);
    }

    public ChangeWatcher CreateWatcher(
        TimeSpan interval,
        Func<CancellationToken, Task<WatchPlan?>> planSupplier,
        int channelCapacity = 8)
        => new ChangeWatcher(Drift, Logger, interval, planSupplier, channelCapacity);

    /// <summary>
    /// Fire-and-forget: open every configured connection once in the
    /// background so the first real sync / compare / query doesn't pay
    /// the cold-start TLS + auth + pool cost. Runs on a worker thread,
    /// short per-connection timeout, bounded concurrency, and silent on
    /// failure — a warm-up failing never surfaces to the user, they'll
    /// see the real error when they actually use the connection.
    /// </summary>
    public Task WarmUpConnectionsAsync(CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            try
            {
                var profiles = Connections.Load();
                if (profiles.Count == 0) return;

                // Cap parallelism so a 50-connection store doesn't open
                // 50 sockets at once — that triggers anti-abuse on some
                // SQL edges and can drown the app's startup.
                using var gate = new System.Threading.SemaphoreSlim(4);
                var tasks = profiles.Select(async p =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var cs = p.BuildConnectionString();
                        if (string.IsNullOrWhiteSpace(cs)) return;

                        // Short connect timeout so an unreachable server
                        // doesn't keep a warm-up slot busy for 30 seconds.
                        var csb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs)
                        {
                            ConnectTimeout = 5
                        };

                        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(csb.ConnectionString);
                        await conn.OpenAsync(ct).ConfigureAwait(false);
                        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand("SELECT 1", conn)
                        {
                            CommandTimeout = 5
                        };
                        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // Log, don't surface. The real call path will
                        // show the same error if the user actually tries
                        // this connection later.
                        Logger.Log($"Warm-up skipped for {p.Environment}·{p.Database}: {ex.Message}");
                    }
                    finally { gate.Release(); }
                });
                await Task.WhenAll(tasks).ConfigureAwait(false);
                Logger.Log($"Warm-up complete for {profiles.Count} connection(s).");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Log($"Warm-up task aborted: {ex.Message}");
            }
        }, ct);
    }
}
