# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Base.It is a Windows desktop tool for SQL Server database object capture, diff, sync, and drift-watching — a replacement for a legacy WinForms `DB_Sync` app. Avalonia 11 desktop client (`Base.It.App`) on top of a pure, UI-free engine library (`Base.It.Core`). Targets `net8.0`, Windows-only at runtime (depends on DPAPI for credential storage and the `git` CLI for DACPAC staging).

## Build / test / run

There is **no `.sln`/`.slnx` and no `run.ps1`** in this checkout (the README and `docs/ARCHITECTURE.md` reference them, but they're absent). Operate per-project via the `.csproj` files:

```powershell
# Build the whole app (App references Core, so this builds both)
dotnet build "Base.It.App\Base.It.App.csproj" -c Debug -v minimal

# Run the unit tests (no database required)
dotnet test "Base.It.Core.Tests\Base.It.Core.Tests.csproj" -c Debug --nologo

# Run a single test by name
dotnet test "Base.It.Core.Tests\Base.It.Core.Tests.csproj" --filter "FullyQualifiedName~DacpacExporterTests"

# Run the CLI smoke harness (verifies the engine works without the UI)
dotnet run --project "Base.It.Smoke\Base.It.Smoke.csproj"
```

Distribution build (single-file, self-contained, no .NET runtime needed on target):

```powershell
.\publish.ps1                         # -> publish\Base.It.exe
.\release.ps1 -Version 1.2.1          # patches csproj <Version>, publishes, vpk pack + upload to GitHub Releases
.\release.ps1 -Version 1.2.1 -DryRun  # stop before upload, inspect artifacts
```

`release.ps1` needs the Velopack CLI (`dotnet tool install -g vpk`) and `$env:GITHUB_TOKEN`. Update `<Version>`/`<FileVersion>` in `Base.It.App.csproj` per release (release.ps1 does this for you).

### Build gotchas (real, will bite you)

- **Do NOT add `PublishTrimmed=true`** — Avalonia loads XAML via reflection; trimming strips the types and the window renders blank. (Called out in `publish.ps1`.)
- **Do NOT rename `AssemblyName` away from `Base.It.App`** — every `avares://` URI in XAML is compiled against that exact assembly name; changing it breaks style/resource resolution at runtime. The published exe is renamed to `Base.It.exe` as a post-step instead.
- If the app or Visual Studio is running, it locks `Base.It.Core.dll` in `bin/` → rebuild produces lock warnings/errors. Close the app first.

## Architecture

The hard rule: **`Base.It.Core` is a pure engine with no UI and no hosting.** The Avalonia app, the smoke CLI, and any future CLI/web frontend are all just *consumers* of Core. Keep logic in Core (and its ViewModels); don't push engine behavior into views. New engine features get unit tests in `Base.It.Core.Tests`; the App project has no tests by design (test logic via ViewModels if needed).

### Base.It.Core folders

| Folder          | Role                                  | Key types |
|-----------------|---------------------------------------|-----------|
| `Abstractions/` | Service contracts                     | `IObjectScripter`, `SqlObjectRef` |
| `Models/`       | Domain records                        | `ObjectIdentifier(Schema,Name)` (defaults `dbo`), `SqlObject`, `SqlObjectType` |
| `Sql/`          | Live catalog + definition reads       | `SqlObjectScripter` |
| `Drift/`        | Change detection                      | `DriftDetector` (`StreamAsync`), `ChangeWatcher`, `WatchEvent` hierarchy |
| `Config/`       | Persisted user state                  | `ConnectionConfigStore`, `DpapiConnectionStore`, `WatchGroup`, `WatchGroupStore` |
| `Dacpac/`       | SSDT export + git branch staging      | `DacpacExporter`, `DacpacExportStore`, `GitStager` |
| `Backup/`       | Pre-sync file snapshots               | `FileBackupStore`, `BackupService` |
| `Sync/`         | CREATE→ALTER rewrite + apply          | `SyncService`, `CreateToAlterRewriter`, `SyncResult` |
| `Batch/`        | Parallel object loader                | `ObjectListLoader` |
| `Query/`        | Ad-hoc query runner                   | `QueryService` |
| `Hashing/`      | Definition canonicalisation           | `DefinitionHasher` |
| `Parsing/`      | T-SQL validation (ScriptDom)          | `TSqlValidator` |
| `Diff/`         | Line-level diff + alignment           | `LineDiffer`, `LineAligner` |
| `Schema/`       | SQL project / schema handling         | (see `SchemaStore`, `SqlProjUpdater` tests) |
| `Logging/`      | File-based logging                    | `FileLogger` |

### Base.It.App (Avalonia 11 + FluentAvaloniaUI 2.2 + CommunityToolkit.Mvvm)

Single-window navigation shell (`NavigationView` sidebar). One ViewModel per pane: **Compare** (side-by-side diff), **Sync** (single-object src→tgt push with preview + backup), **Batch** (multi-object serial push), **Query** (free-form runner), **Watch** (live drift lists, streamed), **Settings** (connections, auth, DACPAC config, legacy import). Composition root is `Services/AppServices.cs` — a single container wiring Core services; `TryBuildDacpacExporterAsync` builds the exporter lazily when config is usable.

### Cross-cutting behaviors worth knowing before editing

- **Non-blocking reads:** every `SqlObjectScripter` catalog read prepends `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SET LOCK_TIMEOUT 2000;`, and `DriftDetector` caps parallelism at 2 — by design, so the watcher never blocks production SQL. Preserve this when touching read paths.
- **Sync safety pipeline:** target is backed up first → CREATE script rewritten to ALTER (`CreateToAlterRewriter`) → validated by `TSqlValidator` (ScriptDom) → executed → source+target backups zipped. Don't bypass steps.
- **DACPAC export is non-destructive:** updates files in place when a same-named file exists under the root (schema-scoped match preferred); new objects go to `{Root}/{Schema}/{Type}2/{Name}.sql` (the `2` suffix flags them for human review). `GitStager` only creates branches and stages — it **never commits, pushes, or opens PRs**. Writes UTF-8 BOM + CRLF (SSDT convention).
- **Watch pane** filters out `InSync` rows, fixed section order (Stored Procedures → Functions → Triggers → Tables → Views), streams via `System.Threading.Channels` (DropOldest) + `IAsyncEnumerable`, parallel shutdown with a per-watcher 3s budget.

### Persistence (per-user, encrypted)

Connection strings are **never** stored beside the binary. They live under `%LOCALAPPDATA%\Base.It\` (README) / `%AppData%\BaseIt\` (ARCHITECTURE — the two docs disagree; confirm against `Config/` code before relying on a path), DPAPI-encrypted (CurrentUser scope): only the same Windows user on the same machine can decrypt. Legacy `appsettings.json` is never read directly — the Settings tab imports it once into the encrypted store. Backups and daily logs also live under the per-user app data folder.

## Docs accuracy note

`README.md` and `docs/ARCHITECTURE.md` are useful for intent but partly stale: they cite a `run.ps1` workflow, a `Base.It.slnx` solution, and differing test counts / appdata paths that don't match this checkout. Trust the `.csproj` files and source over the prose when they conflict.
