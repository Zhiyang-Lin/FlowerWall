<#
.SYNOPSIS
    Ensure a usable .NET 8 SDK is present on this machine (run once).

.DESCRIPTION
    Why this script exists:
      The project targets net8.0-windows, so building requires the .NET SDK
      (compiler + MSBuild). This machine initially only had the .NET 8 runtime.

    Behaviour:
      1. If an SDK is already available (on PATH or in -InstallDir), exit without
         changing anything (idempotent).
      2. Otherwise download the official dotnet-install.ps1 and install the
         .NET 8 SDK into -InstallDir.

    Downloads prefer Python (urllib / OpenSSL) because in some restricted
    environments Windows Schannel cannot complete a TLS handshake, while
    Python's bundled OpenSSL is unaffected.

    NOTE: this file is intentionally ASCII-only. Windows PowerShell 5.1 reads
    .ps1 files without a BOM as ANSI, which corrupts non-ASCII characters.

.PARAMETER InstallDir
    Where to install the SDK. Defaults to <repo>\.tools\dotnet so nothing outside
    the project folder is touched and no administrator rights are needed.
    Pass -InstallDir $env:LOCALAPPDATA\Microsoft\dotnet to install per-user instead.

.PARAMETER Channel
    SDK channel to install. Defaults to 8.0.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\bootstrap-sdk.ps1
#>
[CmdletBinding()]
param(
    [string] $InstallDir,
    [string] $Channel    = '8.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Default install location: <repo>\.tools\dotnet
#
# NOTE - why this is resolved here and not as a param() default:
# In Windows PowerShell 5.1, $PSScriptRoot is NOT populated while the parameter
# block of an *advanced* script ([CmdletBinding()]) is being bound. It arrives as
# an empty string, so `Split-Path -Parent $PSScriptRoot` in a default fails with
# "Cannot bind argument to parameter 'Path' because it is an empty string".
# A plain script without [CmdletBinding()] does populate it, which makes this
# easy to miss. Verified by experiment; keep the default in the body.
if (-not $InstallDir) {
    $InstallDir = Join-Path (Split-Path -Parent $PSScriptRoot) '.tools\dotnet'
}

# Search the usual Anaconda locations first, then whatever "python" resolves to.
# Deliberately no hard-coded drive letters: a path that only exists on the
# author's machine is noise in a public repository, and PATH already covers it.
function Get-PythonPath {
    foreach ($candidate in @(
            (Join-Path $env:USERPROFILE 'anaconda3\python.exe'),
            'C:\ProgramData\anaconda3\python.exe'
        )) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    $onPath = Get-Command python -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    return $null
}

function Invoke-Download {
    param(
        [Parameter(Mandatory)][string] $Url,
        [Parameter(Mandatory)][string] $Destination
    )

    $python = Get-PythonPath
    if ($python) {
        $helper = Join-Path $PSScriptRoot 'download.py'
        Write-Host "  downloading with Python: $python"
        & $python $helper $Url $Destination
        if ($LASTEXITCODE -ne 0) { throw "Python download failed for $Url" }
        return
    }

    Write-Host '  Python not found, falling back to Invoke-WebRequest'
    Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing
}

# --- 1. Skip when an SDK is already available --------------------------------
$dotnetExe = $null
$localDotnet = Join-Path $InstallDir 'dotnet.exe'
if (Test-Path -LiteralPath $localDotnet) {
    $dotnetExe = $localDotnet
}
else {
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $dotnetExe = $onPath.Source }
}

if ($dotnetExe) {
    $sdks = & $dotnetExe --list-sdks 2>$null
    if ($sdks) {
        Write-Host 'A .NET SDK is already installed, nothing to do:' -ForegroundColor Green
        $sdks | ForEach-Object { Write-Host "  $_" }
        Write-Host "dotnet host: $dotnetExe"
        return
    }
    Write-Host "Found a dotnet host but no SDK: $dotnetExe"
}

# --- 2. Install the SDK -----------------------------------------------------
# Prefer the Python installer: it downloads and unpacks the official SDK
# archive itself, avoiding the BITS / Schannel download path used inside
# dotnet-install.ps1 (that path fails in restricted environments).
# Fall back to the official script when Python is unavailable.
$pythonInstaller = Join-Path $PSScriptRoot 'install-dotnet-sdk.py'
$python = Get-PythonPath

if ($python -and (Test-Path -LiteralPath $pythonInstaller)) {
    Write-Host "Installing .NET SDK (channel $Channel) into $InstallDir ..."
    & $python $pythonInstaller --install-dir $InstallDir --channel $Channel
}
else {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) 'flowerwall-bootstrap'
    New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
    $installer = Join-Path $tempDir 'dotnet-install.ps1'

    Write-Host 'Python unavailable, fetching official dotnet-install.ps1 ...'
    Invoke-Download -Url 'https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.ps1' -Destination $installer
    if (-not (Test-Path -LiteralPath $installer)) { throw 'dotnet-install.ps1 download failed' }

    Write-Host "Installing .NET SDK (channel $Channel) into $InstallDir ..."
    & $installer -Channel $Channel -InstallDir $InstallDir -NoPath
}

if ($LASTEXITCODE -ne 0) { throw "SDK installation failed with exit code $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $localDotnet)) { throw "After install, $localDotnet is missing" }

Write-Host ''
Write-Host 'Done. Installed SDKs:' -ForegroundColor Green
& $localDotnet --list-sdks
Write-Host ''
Write-Host "Next: powershell -ExecutionPolicy Bypass -File scripts\build.ps1 run"
