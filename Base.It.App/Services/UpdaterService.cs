using CommunityToolkit.Mvvm.ComponentModel;
using Velopack;
using Velopack.Sources;

namespace Base.It.App.Services;

public enum UpdateState
{
    /// <summary>App launched from `dotnet run` / `dotnet publish` output, not via Setup.exe.</summary>
    NotInstalled,
    /// <summary>Idle — no check in flight, no pending update.</summary>
    Idle,
    /// <summary>Talking to GitHub to see if a newer release exists.</summary>
    Checking,
    /// <summary>A newer release is available but not yet downloaded.</summary>
    Available,
    /// <summary>Downloading the delta / full package from GitHub Releases.</summary>
    Downloading,
    /// <summary>Update has been staged on disk and is ready to install on next restart.</summary>
    ReadyToApply,
    /// <summary>Something went wrong during check/download — see <see cref="UpdaterService.LastError"/>.</summary>
    Failed,
    /// <summary>App is up to date — most recent version is the one running.</summary>
    UpToDate,
}

/// <summary>
/// Wraps Velopack's <see cref="UpdateManager"/> against our GitHub Releases
/// feed. The VM / View surface bindable state (current version, latest
/// version, progress %, error text) and two actions: CheckForUpdates +
/// ApplyAndRestart. Every public method is safe to call even when the app
/// was launched from a dev build (state stays NotInstalled and commands
/// degrade to no-ops).
/// </summary>
public sealed partial class UpdaterService : ObservableObject
{
    /// <summary>Repo we pull releases from. Public repos need no auth.</summary>
    public const string ReleasesRepo = "https://github.com/NBRSPL/Base.It";

    private readonly UpdateManager? _um;
    private UpdateInfo? _pendingUpdate;

    [ObservableProperty] private UpdateState _state = UpdateState.Idle;
    [ObservableProperty] private string _currentVersion = "";
    [ObservableProperty] private string _latestVersion = "";
    [ObservableProperty] private int _downloadPercent;
    [ObservableProperty] private string _lastError = "";

    public bool IsInstalled => _um?.IsInstalled ?? false;

    /// <summary>Does it make sense to show the "Check for updates" UI?</summary>
    public bool CanCheck => IsInstalled && State is UpdateState.Idle or UpdateState.UpToDate or UpdateState.Failed;

    /// <summary>Is there a downloaded update waiting to be applied?</summary>
    public bool CanApply => IsInstalled && State == UpdateState.ReadyToApply;

    public UpdaterService()
    {
        try
        {
            // GitHub releases — no auth needed for a public repo. For a
            // private repo, pass a PAT via GithubSource(..., accessToken).
            var src = new GithubSource(ReleasesRepo, accessToken: null, prerelease: false);
            _um = new UpdateManager(src);

            CurrentVersion = _um.CurrentVersion?.ToString() ?? "";
            State = _um.IsInstalled ? UpdateState.Idle : UpdateState.NotInstalled;
        }
        catch (Exception ex)
        {
            _um = null;
            State = UpdateState.NotInstalled;
            LastError = ex.Message;
        }
    }

    /// <summary>
    /// Hit the release feed and see if a newer version exists. On success
    /// moves to <see cref="UpdateState.Available"/> (if a newer version is
    /// published) or <see cref="UpdateState.UpToDate"/> (already current).
    /// </summary>
    public async Task CheckForUpdatesAsync()
    {
        if (_um is null || !_um.IsInstalled)
        {
            State = UpdateState.NotInstalled;
            return;
        }

        try
        {
            State = UpdateState.Checking;
            LastError = "";
            var info = await _um.CheckForUpdatesAsync();
            if (info is null)
            {
                _pendingUpdate = null;
                LatestVersion = CurrentVersion;
                State = UpdateState.UpToDate;
                return;
            }

            _pendingUpdate = info;
            LatestVersion  = info.TargetFullRelease.Version.ToString();
            State          = UpdateState.Available;
        }
        catch (Exception ex)
        {
            State = UpdateState.Failed;
            LastError = ex.Message;
        }
    }

    /// <summary>
    /// Download the pending update. Call <see cref="CheckForUpdatesAsync"/>
    /// first — if no update was found this is a no-op. Reports progress
    /// via <see cref="DownloadPercent"/>. On success moves to
    /// <see cref="UpdateState.ReadyToApply"/>.
    /// </summary>
    public async Task DownloadAsync()
    {
        if (_um is null || _pendingUpdate is null) return;
        try
        {
            State = UpdateState.Downloading;
            DownloadPercent = 0;
            await _um.DownloadUpdatesAsync(_pendingUpdate, p => DownloadPercent = p);
            State = UpdateState.ReadyToApply;
        }
        catch (Exception ex)
        {
            State = UpdateState.Failed;
            LastError = ex.Message;
        }
    }

    /// <summary>
    /// Restart the app via the Velopack loader so the staged update is
    /// swapped in. Does not return — the current process exits.
    /// </summary>
    public void ApplyAndRestart()
    {
        if (_um is null || _pendingUpdate is null) return;
        _um.ApplyUpdatesAndRestart(_pendingUpdate);
    }

    partial void OnStateChanged(UpdateState value)
    {
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CanApply));
    }
}
