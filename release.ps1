# ---------------------------------------------------------------------
# Base.It - cut a new Velopack release and push it to GitHub Releases.
#
# What it does:
#   1. dotnet publish -> produces publish\Base.It.exe
#   2. vpk pack       -> writes Releases\ (Setup.exe, .nupkg, delta files)
#   3. vpk upload     -> creates a GitHub release at the given tag and
#                       uploads every artifact the end-user's app needs
#                       to find updates.
#
# Pre-reqs (one-time):
#   - .NET 8 SDK
#   - Velopack CLI:     dotnet tool install -g vpk
#   - GitHub PAT with `repo` scope in $env:GITHUB_TOKEN
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.0.1
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.1.0 -DryRun
#
# Notes on versioning:
#   Velopack uses semver. The version MUST be higher than the previous
#   release or delta generation won't produce anything meaningful. The
#   app's own csproj <Version> drives the running-version display in
#   Settings -> Updates, so bump both (this script patches the csproj
#   automatically to match the -Version flag).
# ---------------------------------------------------------------------

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    # Repo to release against - override for forks / test repos.
    [string]$RepoUrl = 'https://github.com/NBRSPL/Base.It',

    # When set, produces local artifacts but skips the upload to GitHub.
    # Handy for verifying pack output before burning a release number.
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishDir = Join-Path $scriptRoot 'publish'
$releasesDir = Join-Path $scriptRoot 'Releases'
$project = Join-Path $scriptRoot 'Base.It.App\Base.It.App.csproj'

# --- Validate inputs ---------------------------------------------------
if (-not $DryRun -and [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
    Write-Host "GITHUB_TOKEN env var not set. Create a PAT with 'repo' scope and:" -ForegroundColor Red
    Write-Host '    $env:GITHUB_TOKEN = "ghp_..."' -ForegroundColor Yellow
    exit 1
}

# --- Sync csproj <Version> to the requested release number ------------
# Use the .NET APIs for explicit UTF-8 (no BOM) round-tripping. PowerShell
# 5.1's Get-Content / Set-Content defaults to Windows-1252 unless the file
# has a BOM, which silently corrupts em-dashes and the (c) symbol.
Write-Host "Patching Version in $project to $Version..."
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
$csproj = [System.IO.File]::ReadAllText($project, $utf8NoBom)
$csproj = [Regex]::Replace($csproj, '<Version>[^<]+</Version>',                 "<Version>$Version</Version>")
$csproj = [Regex]::Replace($csproj, '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$Version.0</AssemblyVersion>")
$csproj = [Regex]::Replace($csproj, '<FileVersion>[^<]+</FileVersion>',         "<FileVersion>$Version.0</FileVersion>")
[System.IO.File]::WriteAllText($project, $csproj, $utf8NoBom)

# --- Publish (single-file self-contained) -----------------------------
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

Write-Host "Publishing Base.It to $publishDir ..."
& dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { Write-Host 'Publish failed.' -ForegroundColor Red; exit 1 }

# Rename the produced exe (AssemblyName must stay Base.It.App for XAML
# resources to resolve - we rename only for user-facing distribution).
$producedExe = Join-Path $publishDir 'Base.It.App.exe'
$finalExe    = Join-Path $publishDir 'Base.It.exe'
if (Test-Path $producedExe) {
    if (Test-Path $finalExe) { Remove-Item -Force $finalExe }
    Rename-Item -Path $producedExe -NewName 'Base.It.exe'
}

# --- vpk pack: build the Velopack release artifacts -------------------
# -u   PackId     (stable identifier - do not change across versions)
# -v   PackVersion
# -p   PackDir    (folder containing the built app)
# -o   OutputDir  (writes Setup.exe / .nupkg / releases files)
# -e   MainExeName (the exe the loader should launch post-update)
Write-Host "Packing Velopack release $Version..."
& vpk pack `
    -u 'Base.It' `
    -v $Version `
    -p $publishDir `
    -o $releasesDir `
    -e 'Base.It.exe' `
    --packTitle 'Base.It' `
    --packAuthors 'L2 Brands'

if ($LASTEXITCODE -ne 0) { Write-Host 'vpk pack failed.' -ForegroundColor Red; exit 1 }

Write-Host ''
Write-Host "Artifacts in ${releasesDir}:"
Get-ChildItem $releasesDir | Select-Object Name, @{n='Size';e={"{0:N0} bytes" -f $_.Length}} | Format-Table -AutoSize

if ($DryRun) {
    Write-Host ''
    Write-Host "DRY RUN - skipping upload. To publish, re-run without -DryRun." -ForegroundColor Yellow
    exit 0
}

# --- vpk upload github: push the release to GitHub Releases ----------
Write-Host "Uploading release $Version to $RepoUrl ..."
& vpk upload github `
    --repoUrl $RepoUrl `
    --publish `
    --releaseName "Base.It v$Version" `
    --tag "v$Version" `
    --token $env:GITHUB_TOKEN

if ($LASTEXITCODE -ne 0) { Write-Host 'Upload failed.' -ForegroundColor Red; exit 1 }

Write-Host ''
Write-Host "Done. v$Version published." -ForegroundColor Green
Write-Host ''
Write-Host "First-time install for new users:"
Write-Host "    Point them at https://github.com/NBRSPL/Base.It/releases/latest"
Write-Host "    and have them run Setup.exe. Everyone on v$Version or later"
Write-Host "    will pick up future releases automatically via the in-app"
Write-Host "    Settings > Updates section."
