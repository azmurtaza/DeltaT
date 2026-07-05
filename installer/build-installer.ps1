<#
  build-installer.ps1 - one command to produce installer\out\DeltaT-Setup-<ver>.exe

  Steps:
    1. Publish DeltaT.App self-contained (bundles .NET 8 - target machines need nothing).
    2. Compile installer\DeltaT.iss with Inno Setup's ISCC.

  Usage (from anywhere):
    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
    ...optional: -Runtime win-x64  -Configuration Release  -SkipPublish
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$InstallerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot     = Split-Path -Parent $InstallerDir
$AppProject   = Join-Path $RepoRoot 'src\DeltaT.App\DeltaT.App.csproj'
$PublishDir   = Join-Path $InstallerDir 'publish'
$IssFile      = Join-Path $InstallerDir 'DeltaT.iss'

# --- Version: read <Version> from the app .csproj so the installer name matches ---
[xml]$csproj = Get-Content $AppProject
$Version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if (-not $Version) { $Version = '1.0.0' }
Write-Host "DeltaT version: $Version" -ForegroundColor Cyan

# --- 1. Publish (self-contained, single folder) ---
if (-not $SkipPublish) {
    if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
    Write-Host "Publishing $Runtime self-contained..." -ForegroundColor Cyan
    dotnet publish $AppProject `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -o $PublishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
} else {
    Write-Host "Skipping publish (using existing $PublishDir)" -ForegroundColor Yellow
}

if (-not (Test-Path (Join-Path $PublishDir 'DeltaT.App.exe'))) {
    throw "Publish output missing DeltaT.App.exe - cannot build installer."
}

# --- 2. Locate ISCC (Inno Setup 6 compiler) ---
$IsccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$Iscc = $IsccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Iscc) {
    throw "ISCC.exe not found. Install Inno Setup 6: winget install --id JRSoftware.InnoSetup"
}
Write-Host "Using ISCC: $Iscc" -ForegroundColor Cyan

# --- 3. Compile the installer ---
& $Iscc "/DAppVersion=$Version" "/DPublishDir=publish" $IssFile
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)" }

$Output = Join-Path $InstallerDir "out\DeltaT-Setup-$Version.exe"
Write-Host ""
Write-Host "Done -> $Output" -ForegroundColor Green
