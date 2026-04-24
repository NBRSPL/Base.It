# Base.It — Technical Architecture

**As of:** 2026-04-22
**Status:** Replaces legacy WinForms `DB_Sync`. Avalonia desktop client + .NET 8 core library.

---

## 1. Solution layout

```
Base.It/
├── Base.It.Core/          Pure library — SQL, drift, sync, DACPAC, config
├── Base.It.App/           Avalonia 11 desktop (MVVM) — the shipping UI
├── Base.It.Core.Tests/    xunit — 105 tests
└── Base.It.Smoke/         Console smoke harness
```

Target framework: `net8.0`. Windows-only at runtime (DPAPI, git CLI).

---

## 2. Base.It.Core — folders & responsibilities

| Folder          | Role                                  | Key types                                                                 |
|-----------------|---------------------------------------|---------------------------------------------------------------------------|
| `Abstractions/` | Service contracts                     | `IObjectScripter`, `SqlObjectRef`                                         |
| `Models/`       | Domain records                        | `ObjectIdentifier(Schema,Name)`, `SqlObject`, `SqlObjectType` enum        |
| `Sql/`          | Live catalog + definition reads       | `SqlObjectScripter` (non-blocking: READ UNCOMMITTED + LOCK_TIMEOUT 2000)  |
| `Drift/`        | Change detection                      | `DriftDetector` (`StreamAsync`), `ChangeWatcher`, `WatchEvent` hierarchy  |
| `Config/`       | Persisted user state                  | `ConnectionConfigStore`, `DpapiConnectionStore`, `WatchGroup`, `WatchGroupStore` |
| `Dacpac/`       | SSDT export + git branch staging      | `DacpacExporter`, `DacpacExportOptions`, `DacpacExportStore`, `GitStager` |
| `Backup/`       | Pre-sync file snapshots               | `FileBackupStore`, `BackupService`                                        |
| `Sync/`         | CREATE→ALTER rewrite + apply          | `SyncService`, `CreateToAlterRewriter`, `SyncResult`                      |
| `Batch/`        | Parallel object loader for UI lists   | `ObjectListLoader`                                                        |
| `Query/`        | Ad-hoc query runner                   | `QueryService`                                                            |
| `Hashing/`      | Definition canonicalisation           | `DefinitionHasher`                                                        |
| `Parsing/`      | T-SQL validation                      | `TSqlValidator` (ScriptDom)                                               |
| `Diff/`         | Line-level diff + alignment           | `LineDiffer`, `LineAligner`                                               |
| `Logging/`      | File-based logging                    | `FileLogger`                                                              |

---

## 3. Core contracts

```csharp
// Abstractions
interface IObjectScripter {
    Task<SqlObjectType> GetObjectTypeAsync(string conn, ObjectIdentifier id, CT);
    Task<SqlObject?>    GetObjectAsync    (string conn, ObjectIdentifier id, CT);
    Task<IReadOnlyList<SqlObjectRef>> ListAllAsync(string conn, CT);
}

// Models
record ObjectIdentifier(string Schema, string Name);     // "dbo" default
enum   SqlObjectType { Unknown, Table, View, StoredProcedure,
                       ScalarFunction, InlineTableFunction,
                       TableValuedFunction, Trigger }
record SqlObject(ObjectIdentifier Id, SqlObjectType Type, string Definition, string Hash);

// Drift
enum   DriftKind { InSync, Different, MissingInTarget, MissingInSource, Error }
record ObjectDrift(ObjectIdentifier Id, SqlObjectType SourceType, SqlObjectType TargetType, DriftKind Kind, ...);
abstract WatchEvent → TickStarted | ObjectDrifted | TickCompleted

// Config
record WatchGroup(Guid Id, string Name, string SourceEnv, string TargetEnv,
                  string Database, List<string> Objects, int IntervalSeconds, bool Enabled);
//   Objects == [] means "auto-discover all user objects"

// Dacpac
record DacpacExportOptions(bool Enabled, string RootFolder, bool StageInGit, string BranchPrefix);
```

---

## 4. Base.It.App — UI layer

Stack: **Avalonia 11.1.3 + FluentAvaloniaUI 2.2 + CommunityToolkit.Mvvm 8.3**. Single-window navigation shell, sidebar `NavigationView`.

**Navigation panes**

| Pane     | ViewModel               | Purpose                                                           |
|----------|-------------------------|-------------------------------------------------------------------|
| Compare  | `CompareViewModel`      | Tabbed side-by-side diff viewer                                   |
| Sync     | `SyncViewModel`         | Single-object push (src → tgt) with preview + backup              |
| Batch    | `BatchViewModel`        | Multi-object serial pushes, # column, failure Message column      |
| Query    | `QueryViewModel`        | Free-form query runner                                            |
| Watch    | `WatchViewModel`        | Drift lists by group, sectioned by type, streaming                |
| Settings | `SettingsViewModel`     | Connections, auth, DACPAC config, legacy import                   |

**Watch pane specifics**
- Fixed 5 sections in order: **Stored Procedures → Functions → Triggers → Tables → Views**
- `WatchSectionVm` uses `Predicate<SqlObjectType>` for routing; dot turns red when section has rows
- Filters out `InSync` rows (display-only-differences)
- Streams via `System.Threading.Channels` (DropOldest) + `IAsyncEnumerable`
- Safe shutdown: `Task.WhenAll` parallel stops, per-watcher 3s budget
- Two action buttons: **Stage Changes as DACPAC Branch** (DACPAC-only, never touches target) + **Send Changed to Batch**

**Composition root:** `Services/AppServices.cs` — single container for `Connections`, `Scripter`, `Drift`, `Sync`, `WatchGroups`, `DacpacOptions`, `Backup`, `Logger`. `TryBuildDacpacExporterAsync` lazily builds when config is usable.

---

## 5. Non-blocking SQL strategy

Every catalog read from `SqlObjectScripter` prepends:
```sql
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET LOCK_TIMEOUT 2000;
```
Plus: `maxParallelism = 2` in DriftDetector, short command timeouts, per-tick connection lifecycle. Net: the watcher never blocks production SQL.

---

## 6. DACPAC export — current contract

- **Update-in-place** when a file with the same name exists anywhere under `{Root}` (schema-scoped match preferred, root-wide fallback). Preserves whatever folder convention the team's SSDT project uses (`Procs/` vs `Stored Procedures/`, flat vs nested).
- **New objects** go to `{Root}/{Schema}/{NewTypeFolder}/{Name}.sql` where `NewTypeFolder` = `Procs2 | Functions2 | Tables2 | Views2 | Triggers2`. The `2` suffix flags them for human review.
- Writes UTF-8 BOM + CRLF (SSDT convention).
- **Never commits, never pushes, never opens a PR.** `GitStager` only exposes `CreateBranchAsync`, `StageAsync`, `IsInsideWorkingCopyAsync`.
- Batch per-run opt-in checkbox overrides persisted `StageInGit` default.
- Status line reports `N updated / M new`.

---

## 7. Backup layout

```
{BackupRoot}/{yyyy-MM-dd}/{env}/{objectType}/{Name}_{HHmmss}.sql
```
Single authority: `FileBackupStore`. No schema prefix in filename, no `dbo_` pollution.

---

## 8. Persistence locations

- `%AppData%/BaseIt/connections.json` — DPAPI-encrypted connection strings
- `%AppData%/BaseIt/watchgroups.json` — watch group configs
- `%AppData%/BaseIt/dacpac.json` — DACPAC export options
- Backup root + DACPAC root: user-chosen folders

---

## 9. External dependencies

| Project           | Packages                                                                      |
|-------------------|-------------------------------------------------------------------------------|
| Core              | `Microsoft.Data.SqlClient 6.1.2`, `Microsoft.SqlServer.TransactSql.ScriptDom 170.23.0`, `NPOI 2.7.5`, `System.Security.Cryptography.ProtectedData 8.0.0` |
| App               | `Avalonia 11.1.3` (+Desktop/Fluent/DataGrid/Diagnostics), `FluentAvaloniaUI 2.2.0`, `CommunityToolkit.Mvvm 8.3.2` |
| Tests             | `xunit 2.5.3`, `Microsoft.NET.Test.Sdk 17.8.0`, `coverlet.collector 6.0.0`    |

External CLI: **git** (optional, only for DACPAC staging).

---

## 10. Test coverage

**105 passing tests** in `Base.It.Core.Tests`. Noteworthy coverage:
- `DacpacExporterTests` (24) — new/update in-place, schema-scoped match, flat layout, CRLF, disabled paths
- `DriftDetectorTests` — streaming, cancellation propagation, producer lifetime
- `ChangeWatcherTests` — event channel, DropOldest, tick lifecycle
- `SyncServiceTests` — CREATE→ALTER rewrite correctness
- `FileBackupStoreTests` — layout contract
- `CreateToAlterRewriterTests` — parser-driven rewriting

---

## 11. Known runtime constraints

- Windows-only (DPAPI, `System.Diagnostics.Process` for git).
- Visual Studio / the running app lock `Base.It.Core.dll` in `bin/` → build warnings, not errors. Close app before rebuild.
- DACPAC folder must be a git working copy for `StageInGit` to do anything; otherwise silently skipped with a status message.

---

# Next stages

Ordered by priority. Each one is independently shippable.

### A. Sync-pane DACPAC opt-in (small, deferred from last turn)
Add a per-run "Stage as DACPAC branch" checkbox to `SyncView` mirroring the Batch one. Single object, same flow. Already scaffolded in Batch; lift `StageAsDacpacBranch` + `DacpacConfigured` into `SyncViewModel` and re-use `GitStager.TimestampedBranch`.

### B. Watch → drift-history / audit log
Persist `WatchEvent`s to a rolling JSONL file per group. Lets the user answer "when did this proc start drifting?" without staring at the live pane. Cheap: append from `ChangeWatcher`, cap at N MB, rotate.

### C. Proper DACPAC **build** (not just file drop)
Shell out to `sqlpackage.exe` or call `Microsoft.SqlServer.DacFx` to produce a real `.dacpac` from the folder. Today we only drop `.sql` files; the DACPAC build gives schema-validation + drift reports for free.

### D. Multi-schema + multi-DB watch groups
Current `WatchGroup.Database` is a single string and `ObjectIdentifier` defaults to `dbo`. Extend to: list of `(Database, Schema)` pairs, and plumb schema through `ListAllAsync`. Needed for any non-trivial project.

### E. Sync dry-run / plan-preview
Before executing, show the user the generated ALTER script (and the diff against the target's current definition). `CreateToAlterRewriter` already produces the script — just wire to a preview pane with Accept/Cancel.

### F. Tests for the App layer
App project has 0 tests. Add ViewModel-level tests for `WatchViewModel` (section routing, prune-on-tick, shutdown) and `BatchViewModel` (renumber on add/remove, DACPAC availability refresh). No Avalonia view tests needed — keep logic in VMs.

### G. Login / role gating
Currently any user who runs the app can sync to any configured environment. Minimum: per-environment "write-allowed" flag in `ConnectionConfigStore`, surfaced as a confirmation before target mutation (Sync + Batch).

### H. Telemetry hooks
Structured log lines from `SyncService` and `ChangeWatcher` (operation, object, duration, result). Already have `FileLogger`; add a `StructuredLogger` wrapper emitting JSON so it's greppable.

### I. Installer / distribution
Today: `dotnet publish` + zip. Target: signed MSIX or plain self-extracting exe that the team can double-click. Decide after G lands (signing matters once prod-write is gated).

### J. Compare pane — visual polish
`LineDiffer` output is wired but the UI is plain. Syntax highlighting (AvaloniaEdit) + collapsed unchanged regions.
