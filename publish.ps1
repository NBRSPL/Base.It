# ---------------------------------------------------------------------
# Base.It — standalone Windows publish.
#
# Produces a single self-contained Base.It.App.exe in dist\Base.It that
# needs NO .NET runtime on the target machine. Zip the folder and hand
# it out — recipients extract and double-click the exe.
#
# DO NOT add PublishTrimmed=true. Avalonia loads XAML via reflection;
# trimming strips the types it needs and the window renders blank.
# ---------------------------------------------------------------------

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir  = Join-Path $scriptRoot 'publish'
$project = Join-Path $scriptRoot 'Base.It.App\Base.It.App.csproj'

if (Test-Path $outDir) {
    Write-Host "Cleaning $outDir ..."
    Remove-Item -Recurse -Force $outDir
}

Write-Host "Publishing Base.It to $outDir ..."
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
    -o $outDir

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "*** Publish failed. ***" -ForegroundColor Red
    exit 1
}

# Rename the produced exe to Base.It.exe for distribution. We keep
# AssemblyName=Base.It.App so Avalonia's avares:// URIs still resolve,
# but end users see a cleaner filename.
$producedExe = Join-Path $outDir 'Base.It.App.exe'
$finalExe    = Join-Path $outDir 'Base.It.exe'
if (Test-Path $producedExe) {
    if (Test-Path $finalExe) { Remove-Item -Force $finalExe }
    Rename-Item -Path $producedExe -NewName 'Base.It.exe'
}

Write-Host ""
Write-Host "Done. Artifacts in ${outDir}:"
Get-ChildItem $outDir | Select-Object Name, @{n='Size';e={"{0:N0} bytes" -f $_.Length}} | Format-Table -AutoSize
Write-Host ""
Write-Host "Share $outDir\Base.It.exe directly, or zip it first."
