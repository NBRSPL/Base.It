# Base.It

Database object capture, diff, and deployment tool.
Replacement for the existing WinForms `DB_Sync` app.
Built in stages, UI from day one.

---

## Prerequisites

- .NET 8 SDK (you already have .NET 10 which includes it)
- PowerShell 5.1+ (ships with Windows)
- A reachable SQL Server (only needed for live capture and real use)

No global install, no admin rights, no external services.

---

## Running a stage

From the repo root (`D:\nishant.bhatt\DB_Sync`):

```powershell
.\run.ps1 0              # build + tests + validator + UI start-up check
.\run.ps1 0 -Launch      # all of the above, then open the Base.it UI
.\run.ps1 0 -LaunchOnly  # skip checks, just open the UI
```

Exit code `0` = green.

---

## Architecture

```
Base.It.Core        pure engine. No UI, no hosting.
   Config\          IConnectionStore  + DPAPI / InMemory / legacy-JSON impls
   Sql, Sync,       object scripter, SyncService, CreateToAlter rewriter,
   Query, Parsing   T-SQL validator (ScriptDom), QueryService
   Hashing, Diff,   stable content hashing, line differ
   Batch, Backup,   xlsx/csv loader, zipped file backups, per-day file log
   Logging

Base.It.Core.Tests  44 unit tests. No DB required.
Base.It.Smoke       CLI verifier (engine is usable without the UI).
Base.It.App         Avalonia 11 + FluentAvalonia UI:
                    AppWindow with real Win 11 title bar, NavigationView
                    sidebar, FluentAvalonia Mica/Acrylic, card layout.
```

Key principle: the UI is **one consumer** of Core, not the owner of anything.
You can drive the same engine from PowerShell, a CLI, or a web UI later
without touching the engine.

---

## Security model (Stage 0)

**Connection strings are never stored beside the binary.** They live in
`%LOCALAPPDATA%\Base.It\connections.bin`, encrypted with Windows DPAPI
(CurrentUser scope). Properties:

- Only the **same Windows user on the same machine** can decrypt the file.
- Copying the file to another user or another machine yields an opaque blob
  that the code treats as empty (never silently decrypted).
- No master password; the OS binds the key to the Windows logon session.
- App folder ships with **no credentials**. Uninstall = delete the folder and
  `%LOCALAPPDATA%\Base.It\`.

Legacy `appsettings.json` is **never** used directly. The Settings tab lets
the user *import* it once into the encrypted store; the plaintext file is
left alone for the user to delete.

Per-user writeable paths:

| Path | What lives there |
|---|---|
| `%LOCALAPPDATA%\Base.It\connections.bin` | DPAPI-encrypted store |
| `%LOCALAPPDATA%\Base.It\Backups\`        | zipped object backups |
| `%LOCALAPPDATA%\Base.It\Logs\`           | daily append-only log |

---

## UI (Stage 0)

- **AppWindow** with real Windows 11 title bar, drag/min/max/close, mica/acrylic.
- **NavigationView** sidebar with 5 sections (icons + labels), collapsible pane.
- **Card layout** using FluentAvalonia dynamic brushes - proper spacing,
  rounded corners, consistent padding, readable typography.
- Dark theme by default; switchable if the user's system uses light.

Tabs (functional parity with old DB_Sync):

| Section | Replaces old form | What it does |
|---|---|---|
| **Compare** | MainForm (top) | Fetch object from every configured env, side-by-side diff highlighting. |
| **Sync** | MainForm (bottom) | Single-object sync source -> target. Target is backed up first; script is rewritten to ALTER and validated by the T-SQL parser before execution; source+target backups are zipped. |
| **Batch** | BatchExecutionForm | Import `.csv` / `.xlsx` (column `Object name`), manual add/remove, execute with per-row status. |
| **Query** | QueryExecutorForm | Ad-hoc T-SQL against multiple environments in one run. |
| **Settings** | SettingsForm | View/edit/save encrypted connection strings; one-click import from legacy appsettings.json. |

---

## Non-disruption

- New solution (`Base.It.slnx`), new folder (`Base.It\`), new exe (`Base.It.App.exe`).
- Existing `DB_Sync.sln` and `DB_Sync\` folder are untouched and still build.
- No shared files, packages, or config.
- Teammates continue using DB_Sync while Base.it matures.

---

## Deployment & auto-updates

Base.It ships via [Velopack](https://github.com/velopack/velopack), backed by
GitHub Releases in this repo. End-users install once via `Setup.exe`, then
every subsequent release is a **delta update** (typically a few MB, not the
full 100 MB bundle) downloaded from the Releases page and applied on
restart via Settings → Updates.

### One-time setup for the maintainer

1. Install the Velopack CLI (global .NET tool):
   ```powershell
   dotnet tool install -g vpk
   ```
2. Create a GitHub Personal Access Token with `repo` scope and expose it to
   the release script:
   ```powershell
   $env:GITHUB_TOKEN = "ghp_..."
   ```

### Cutting a new release

From the repo root:

```powershell
powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.0.1
```

The script:

1. Patches the csproj `<Version>` to match
2. Runs `dotnet publish` (same single-file self-contained bundle as before)
3. Runs `vpk pack` to produce the `Releases\` folder (`Setup.exe`, `.nupkg`,
   full + delta packages, `releases.win.json`)
4. Runs `vpk upload github` to create a `v1.0.1` tag + GitHub Release and
   upload every artifact the app needs

Add `-DryRun` to stop after step 3 and inspect the artifacts locally.

### End-user install

- **New user:** download `Setup.exe` from
  https://github.com/NBRSPL/Base.It/releases/latest and run it. The app
  installs silently to `%LOCALAPPDATA%\Base.It\`.
- **Existing user:** open the app → Settings → Updates → *Check for
  updates* → *Download* → *Install & Restart*.

### What about private repos?

`UpdaterService.cs` currently constructs `GithubSource(..., accessToken: null, ...)`
for a public repo. If the repo is private, pass a PAT here (and either
prompt users for one or embed a read-only token — both have well-known
tradeoffs). The simplest path for internal distribution is to make this
repo public or mirror releases to a separate public repo.

---

## Upcoming stages

| Stage | Delivers | Entry point |
|---|---|---|
| 1 | `baseit` CLI (single-file exe), deferred batch polish (URL import, status filter) | `.\run.ps1 1` |
| 2 | Git-backed `db-state` repo (capture / status / commit / drift) | `.\run.ps1 2` |
| 3 | Deployment plans, manifests, rollback, post-deploy verify | `.\run.ps1 3` |
| 4 | Portable single-file self-contained `Base.It.App.exe` for the prod VM | `.\run.ps1 4` |
| 5 | DACPAC bridge - auto-PR captured changes into the DACPAC repo | `.\run.ps1 5` |

Every stage is opt-in and additive. Old tool keeps working throughout.
