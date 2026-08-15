<#
.SYNOPSIS
    Compiles the Inno Setup installer from a staged publish folder, and signs it.

.DESCRIPTION
    The staged folder is what the portable zip contains: the self-contained application, the
    bundled ffmpeg, and the licence files. The installer is a second wrapping of the same
    bytes rather than a separate build, so the two downloads can never contain different
    software under the same version number.

    Signing runs through sign.ps1, which is a no-op until a certificate is configured. An
    unsigned installer is worse than an unsigned executable - it is the file SmartScreen
    challenges first - which is why the step is here rather than being left to the pipeline
    to remember.

.PARAMETER Version
    The version to stamp, without a leading v. Comes from the git tag.

.PARAMETER SourceDir
    The staged publish folder to package.

.PARAMETER OutputDir
    Where to write the installer. Defaults to artifacts/ at the repository root.

.EXAMPLE
    .\build-installer.ps1 -Version 0.1.0 -SourceDir ..\..\artifacts\staging\Offstream-0.1.0-win-x64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $SourceDir,

    [string] $OutputDir
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script = Join-Path $PSScriptRoot 'offstream.iss'

if (-not $OutputDir) { $OutputDir = Join-Path $repositoryRoot 'artifacts' }

$SourceDir = (Resolve-Path -LiteralPath $SourceDir).Path
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path

# Refuse to package a folder that is missing the things the .iss assumes are there, so the
# failure names what is absent instead of producing an installer that installs a broken app.
foreach ($required in 'Offstream.exe', 'LICENSE', 'NOTICE', 'ffmpeg\ffmpeg.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDir $required))) {
        throw "$SourceDir has no '$required'. Publish and stage before building the installer."
    }
}

# --- Locate the Inno Setup compiler -----------------------------------------

# Preinstalled on GitHub's windows-latest images, so this normally finds it without help.
$compiler = Get-Command iscc.exe -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty Source

if (-not $compiler) {
    $compiler = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

if (-not $compiler) {
    throw 'Inno Setup 6 was not found. Install it with: winget install --id JRSoftware.InnoSetup'
}

# --- Compile ----------------------------------------------------------------

Write-Host "Compiling the installer for $Version..."

& $compiler `
    "/DAppVersion=$Version" `
    "/DSourceDir=$SourceDir" `
    "/DOutputDir=$OutputDir" `
    $script

if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (exit $LASTEXITCODE)." }

$installer = Join-Path $OutputDir "Offstream-$Version-setup.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw "Inno Setup reported success but $installer is not there." }

# --- Sign -------------------------------------------------------------------

& (Join-Path $PSScriptRoot 'sign.ps1') -Path $installer
if ($LASTEXITCODE -ne 0) { throw "Signing the installer failed (exit $LASTEXITCODE)." }

$size = [math]::Round((Get-Item $installer).Length / 1MB, 1)
Write-Host "Built $installer ($size MB)." -ForegroundColor Green

$installer
