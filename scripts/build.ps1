<#
.SYNOPSIS
    Build, run, publish or diagnose the FlowerWall project.

.DESCRIPTION
    One entry point for all local tasks, so you do not have to remember MSBuild
    flags or where the SDK lives. The script is idempotent and re-runnable.

    It prefers the project-local SDK in .tools\dotnet (installed by
    bootstrap-sdk.ps1) and falls back to a dotnet on PATH.

    NOTE: this file is intentionally ASCII-only. Windows PowerShell 5.1 reads
    .ps1 files without a BOM as ANSI, which corrupts non-ASCII characters and
    makes the script fail to parse. Keep C# sources in UTF-8; keep .ps1 ASCII.

.PARAMETER Task
    build        - compile the whole solution (default)
    run          - build and start the desktop app
    publish      - single-file Release build, self-contained (target needs no .NET)
    publish-fd   - single-file Release build, framework-dependent (much smaller)
    diag         - run the diagnostics tool (FFT / ring buffer / analyzer / render)
    clean        - remove bin\ and obj\ directories

.PARAMETER Configuration
    Debug (default) or Release. Ignored by 'clean'.

.PARAMETER DiagArgs
    Mode(s) for the 'diag' task, forwarded to the diagnostics tool.
    One or more of: all, fft, ring, analyzer, interaction, render, icon, config, language.
    Omit to run every check.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 run
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 publish
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 diag -DiagArgs render
#>
[CmdletBinding()]
param(
    [ValidateSet('build', 'run', 'publish', 'publish-fd', 'diag', 'clean')]
    [string] $Task = 'build',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    # Forwarded to the diagnostics tool (only used by the 'diag' task).
    [string[]] $DiagArgs = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'FlowerWall.sln'

# ------------------------------------------------------------------ locate SDK

function Resolve-DotNet {
    $local = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $local) { return $local }

    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    throw 'No dotnet found. Run scripts\bootstrap-sdk.ps1 first.'
}

$dotnet = Resolve-DotNet

function Invoke-Publish {
    param([Parameter(Mandatory)][bool] $SelfContained)

    $project = Join-Path $repoRoot 'src\FlowerWall.Desktop\FlowerWall.Desktop.csproj'
    $output = Join-Path $repoRoot 'dist'

    $publishArgs = @(
        'publish', $project,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', $(if ($SelfContained) { 'true' } else { 'false' }),
        '-p:PublishSingleFile=true',
        '-o', $output
    )

    if ($SelfContained) {
        # Bundle native libraries into the single file (WPF needs this to run standalone).
        $publishArgs += '-p:IncludeNativeLibrariesForSelfExtract=true'
    }

    Write-Host ('publishing ({0}) ...' -f $(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }))
    & $dotnet @publishArgs @msbuildArgs

    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host 'publish failed.' -ForegroundColor Yellow
        Write-Host 'If the error mentions NU1301 / a missing runtime pack, this machine cannot reach' -ForegroundColor Yellow
        Write-Host 'nuget.org. Publishing a -r win-x64 build needs the win-x64 runtime packs, which are' -ForegroundColor Yellow
        Write-Host 'downloaded on first use. Run it once on a machine with network access.' -ForegroundColor Yellow
        return
    }

    $exe = Join-Path $output 'FlowerWall.exe'
    if (Test-Path -LiteralPath $exe) {
        $size = (Get-Item -LiteralPath $exe).Length / 1MB
        Write-Host ''
        Write-Host ('published: {0} ({1:N1} MB)' -f $exe, $size) -ForegroundColor Green
    }
}

# Keep dotnet's first-run markers and caches inside the project so the workspace
# stays self-contained and the user profile is untouched.
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools\cli-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null

# Single-node build.
#
# Rationale: in restricted environments (sandboxes, restricted tokens, read-only
# temp dirs) MSBuild's parallel worker nodes can fail to start, and the symptom is
# a "Build FAILED" with 0 errors - no information at all, very hard to diagnose.
# Building on one node costs a few seconds here and behaves deterministically.
#
# Do NOT set MSBUILDNOINPROCNODE here: disabling the in-process node in addition to
# -m:1 leaves MSBuild with no usable node at all and reproduces the same silent
# failure. Verified by experiment - one of the two, not both.
$env:MSBUILDDISABLENODEREUSE = '1'
$msbuildArgs = @('-m:1')

Write-Host "dotnet : $dotnet"
Write-Host "task   : $Task ($Configuration)"

switch ($Task) {
    'build' {
        & $dotnet build $solution -c $Configuration @msbuildArgs
    }

    'run' {
        & $dotnet build $solution -c $Configuration @msbuildArgs
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        $exe = Join-Path $repoRoot "src\FlowerWall.Desktop\bin\$Configuration\net8.0-windows\FlowerWall.exe"
        Write-Host "starting $exe"
        & $exe
    }

    'publish' {
        Invoke-Publish -SelfContained $true
    }

    'publish-fd' {
        Invoke-Publish -SelfContained $false
    }

    'diag' {
        # Build single-node like every other task, then launch the produced assembly
        # directly. Going through 'dotnet run' would make argument separation
        # ambiguous once -m:1 is in the option list: the tool then received "-m:1"
        # as its mode name, matched no check, and still reported success - a silent
        # false pass. Launching the DLL removes that failure mode entirely.
        $toolProject = Join-Path $repoRoot 'tools\FlowerWall.Diagnostics\FlowerWall.Diagnostics.csproj'
        & $dotnet build $toolProject -c $Configuration @msbuildArgs
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        $toolDll = Get-ChildItem -Path (Join-Path $repoRoot "tools\FlowerWall.Diagnostics\bin\$Configuration") `
            -Recurse -Filter 'FlowerWall.Diagnostics.dll' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if (-not $toolDll) {
            throw "Diagnostics assembly not found under tools\FlowerWall.Diagnostics\bin\$Configuration."
        }

        & $dotnet $toolDll.FullName @DiagArgs
    }

    'clean' {
        Get-ChildItem $repoRoot -Recurse -Directory -Include bin, obj -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notlike '*\.tools\*' } |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host 'cleaned bin\ and obj\' -ForegroundColor Green
    }
}

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
