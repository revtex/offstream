<#
.SYNOPSIS
    Restores, builds, tests, formats and publishes Offstream from an ordinary PowerShell prompt.

.DESCRIPTION
    Everything runs through the `dotnet` CLI, so no Developer PowerShell, vswhere, MSBuild
    discovery or nuget.exe bootstrap is needed. The script verifies the .NET 10 SDK is
    present (not just a runtime) and warns when ffmpeg is missing, since the encode
    integration tests shell out to it.

    Publishing is pinned to self-contained, untrimmed, non-AOT. That is a correctness
    constraint, not a preference - the audio-routing COM interop does not survive AOT and
    WPF trims poorly. See CLAUDE.md.

.EXAMPLE
    .\build.ps1                          # Debug build
    .\build.ps1 -Configuration Release
    .\build.ps1 -Test                    # build, then run the suite (desktop tests excluded)
    .\build.ps1 -Test -IncludeDesktop    # also run the FlaUI tests, needs a real session
    .\build.ps1 -Test -Filter FileNameTemplate
    .\build.ps1 -Clean -Test             # rebuild from scratch, then test
    .\build.ps1 -VerifyFormat            # what CI enforces
    .\build.ps1 -Publish                 # self-contained win-x64 publish
    .\build.ps1 -Publish -BundleFfmpeg   # ...with the ffmpeg a release ships (108 MB)
    .\build.ps1 -Run                     # build and launch the app
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [switch] $Clean,
    [switch] $Test,
    [string] $Filter,
    [switch] $IncludeDesktop,
    [switch] $Format,
    [switch] $VerifyFormat,
    [switch] $Publish,
    [switch] $BundleFfmpeg,
    [switch] $Run,

    [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$sln = Join-Path $PSScriptRoot 'Offstream.slnx'
$app = Join-Path $PSScriptRoot 'src\Offstream.App'

function Step($message) { Write-Host "`n$message" -ForegroundColor Cyan }

function Assert-ExitCode($what) {
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)." }
}

# --- Environment checks -----------------------------------------------------

# PowerShell 5.1 has no $IsWindows; treat its absence as "on Windows".
if ($PSVersionTable.PSVersion.Major -ge 6 -and -not $IsWindows) {
    throw "Offstream targets net10.0-windows and uses Windows-only COM and audio APIs. Build from Windows, not WSL or Linux."
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet not found. Install the .NET 10 SDK (winget install --id Microsoft.DotNet.SDK.10) and open a new terminal - see README.md."
}

# A runtime-only install is the common trap: `dotnet` exists but cannot build.
$sdks = & dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
    Write-Host 'Installed SDKs:' -ForegroundColor Yellow
    if ($sdks) { $sdks | ForEach-Object { Write-Host "  $_" } } else { Write-Host '  (none - runtime only)' }
    throw "No .NET 10 SDK found. Install it with: winget install --id Microsoft.DotNet.SDK.10"
}

if (-not (Test-Path $sln)) {
    throw "Offstream.slnx not found. Expected it at the repo root - see docs/MODERNIZATION-PLAN.md."
}

$hasFfmpeg = [bool] (Get-Command ffmpeg -ErrorAction SilentlyContinue)

if ($Test -and -not $hasFfmpeg) {
    Write-Warning "ffmpeg is not on PATH; encode integration tests will be skipped."
    Write-Warning "Install it with: winget install --id BtbN.FFmpeg.LGPL.8.1"
}

# --- Clean ------------------------------------------------------------------

if ($Clean) {
    Step "Cleaning ($Configuration)..."
    & dotnet clean $sln -c $Configuration --nologo -v minimal
    Assert-ExitCode 'Clean'
}

# --- Format -----------------------------------------------------------------

if ($Format) {
    Step 'Formatting...'
    & dotnet format $sln
    Assert-ExitCode 'Format'
}

if ($VerifyFormat) {
    Step 'Verifying formatting...'
    & dotnet format $sln --verify-no-changes
    Assert-ExitCode 'Format verification'
}

# --- Restore and build ------------------------------------------------------

Step 'Restoring...'
& dotnet restore $sln
Assert-ExitCode 'Restore'

Step "Building ($Configuration)..."
& dotnet build $sln -c $Configuration --no-restore --nologo -v minimal
Assert-ExitCode 'Build'

Write-Host "`nBuilt: $Configuration" -ForegroundColor Green

# --- Test -------------------------------------------------------------------

if ($Test) {
    Step 'Running tests...'
    # Not $args - that is an automatic variable.
    $testArgs = @($sln, '-c', $Configuration, '--no-build', '--nologo')

    # FlaUI tests drive a real window and need an interactive desktop session, so they are
    # opt-in here and excluded in CI. Same filter both places.
    $filters = @()
    if ($Filter) { $filters += "FullyQualifiedName~$Filter" }
    if (-not $IncludeDesktop) { $filters += 'Category!=Desktop' }

    # Skip rather than fail when ffmpeg is absent: a missing tool is a setup gap, not a
    # regression, and reporting it as a test failure trains people to ignore red runs.
    if (-not $hasFfmpeg) { $filters += 'Category!=Ffmpeg' }

    if ($filters) { $testArgs += @('--filter', ($filters -join '&')) }

    & dotnet test @testArgs
    Assert-ExitCode 'Tests'
    Write-Host "`nTests passed." -ForegroundColor Green
}

# --- Publish ----------------------------------------------------------------

if ($Publish) {
    # PublishTrimmed and PublishAot stay false - see the .DESCRIPTION note.
    Step "Publishing ($Runtime, self-contained, untrimmed)..."
    & dotnet publish $app -c Release -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        --nologo
    Assert-ExitCode 'Publish'

    # Read the TFM rather than repeat it. This line held a second copy of it and went stale the
    # moment Phase 7 raised the target to net10.0-windows10.0.22621.0 for the WinRT projections,
    # pointing the success message at a directory that no longer existed.
    $props = [xml](Get-Content (Join-Path $PSScriptRoot 'Directory.Build.props'))
    $tfm = @($props.Project.PropertyGroup.TargetFramework | Where-Object { $_ })[0]

    if (-not $tfm) { throw 'Could not read TargetFramework from Directory.Build.props.' }

    $out = Join-Path $app "bin\Release\$tfm\$Runtime\publish"

    # Off by default: a developer machine has ffmpeg on PATH, which FFmpegLocator finds on
    # its own, and 108 MB is a lot to fetch for a publish that is only being smoke-tested.
    # A release always bundles - this switch is for reproducing that locally.
    if ($BundleFfmpeg) {
        Step 'Bundling ffmpeg...'
        & (Join-Path $PSScriptRoot 'build\windows\fetch-ffmpeg.ps1') -Destination (Join-Path $out 'ffmpeg')
        Assert-ExitCode 'Bundling ffmpeg'
    }

    Write-Host "`nPublished to: $out" -ForegroundColor Green
}

# --- Run --------------------------------------------------------------------

if ($Run) {
    Step 'Starting Offstream...'
    & dotnet run --project $app -c $Configuration --no-build
    Assert-ExitCode 'Run'
}
