<#
.SYNOPSIS
    Downloads the pinned LGPL ffmpeg build and stages it for bundling.

.DESCRIPTION
    Offstream ships ffmpeg rather than asking every user to install it (plan §5.1, DR-0001),
    but the binaries are not in this repository: they are 108 MB, they are not ours, and a
    git history is the wrong place for either. This fetches them instead, from the release
    and with the digest recorded in ffmpeg.json beside this script.

    The digest is the point. A build fetched by name from a moving tag is a build nobody
    can reproduce: an encoder that changes between two builds of the same Offstream version
    turns a reproducible bug into an unreproducible one. A mismatch fails, loudly, rather
    than quietly shipping something else.

    What lands in -Destination is what ships:

      ffmpeg.exe        the encoder, and the only executable of the three that is bundled
      LICENSE.txt       ffmpeg's own licence text, as LGPL-3.0 requires travel with it
      SOURCE.txt        written here, naming the exact commit and where its source is

.PARAMETER Destination
    Where to put the bundle. The release pipeline points this at the `ffmpeg` subfolder of
    the staged application directory, which is where FFmpegLocator looks.

.PARAMETER CacheDirectory
    Where to keep the downloaded zip between runs. Defaults to a folder under TEMP, so a
    second run on the same machine re-verifies rather than re-downloads 146 MB.

.EXAMPLE
    .\fetch-ffmpeg.ps1 -Destination .\artifacts\staging\Offstream-0.1.0-win-x64\ffmpeg
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Destination,

    [string] $CacheDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'offstream-ffmpeg-cache')
)

$ErrorActionPreference = 'Stop'

$manifest = Get-Content (Join-Path $PSScriptRoot 'ffmpeg.json') -Raw | ConvertFrom-Json
$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$($manifest.release)/$($manifest.asset)"

New-Item -ItemType Directory -Path $CacheDirectory, $Destination -Force | Out-Null
$zip = Join-Path $CacheDirectory $manifest.asset

# --- Download ---------------------------------------------------------------

# A cached file that no longer matches is treated as absent rather than as an error: a
# half-written download from an interrupted run is the ordinary reason for it, and there is
# nothing to diagnose. A *fresh* download that does not match is the alarming case, below.
if (Test-Path -LiteralPath $zip) {
    if ((Get-FileHash $zip -Algorithm SHA256).Hash -ieq $manifest.sha256) {
        Write-Host "Using cached $($manifest.asset)."
    }
    else {
        Write-Host 'Cached copy does not match its digest; discarding it.'
        Remove-Item -LiteralPath $zip -Force
    }
}

if (-not (Test-Path -LiteralPath $zip)) {
    Write-Host "Downloading $($manifest.asset) (146 MB)..."

    # Invoke-WebRequest's progress bar makes this several times slower in a non-interactive
    # host, which is every host this runs in.
    $previous = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try { Invoke-WebRequest -Uri $url -OutFile $zip }
    finally { $ProgressPreference = $previous }

    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($actual -ne $manifest.sha256) {
        Remove-Item -LiteralPath $zip -Force
        throw @"
ffmpeg download does not match the pinned digest.

  expected  $($manifest.sha256)
  actual    $actual
  from      $url

Nothing was staged. Either the release asset was replaced - GitHub allows it, and it is why
this check exists - or the download was tampered with. Do not update the digest to make this
pass without establishing which.
"@
    }

    Write-Host "Verified $actual."
}

# --- Extract ----------------------------------------------------------------

# Into a scratch folder, then move the two files out: the archive has a versioned root
# folder, so extracting straight to the destination would bury ffmpeg.exe one level deeper
# than FFmpegLocator looks.
$scratch = Join-Path $CacheDirectory 'extract'
if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }

Expand-Archive -LiteralPath $zip -DestinationPath $scratch

$executable = Get-ChildItem -Path $scratch -Filter 'ffmpeg.exe' -Recurse | Select-Object -First 1
$licence = Get-ChildItem -Path $scratch -Filter 'LICENSE.txt' -Recurse | Select-Object -First 1

if (-not $executable) { throw "ffmpeg.exe was not in $($manifest.asset). The asset's layout has changed." }
if (-not $licence) { throw "LICENSE.txt was not in $($manifest.asset), and it cannot ship without one." }

Copy-Item $executable.FullName (Join-Path $Destination 'ffmpeg.exe') -Force
Copy-Item $licence.FullName (Join-Path $Destination 'LICENSE.txt') -Force

@"
ffmpeg $($manifest.version)
$($manifest.licence)

This copy of ffmpeg is redistributed with Offstream, unmodified. Offstream runs it as a
separate process and links no part of it; ffmpeg's licence is LICENSE.txt beside this file
and governs ffmpeg alone.

The source it was built from is FFmpeg commit

  $($manifest.sourceCommit)
  $($manifest.sourceUrl)

and the scripts that built it are

  $($manifest.buildScriptsUrl)

Every Offstream release attaches that source archive as a release asset, so it travels with
the binary rather than depending on a link outliving it.
"@ | Out-File (Join-Path $Destination 'SOURCE.txt') -Encoding utf8

Remove-Item -LiteralPath $scratch -Recurse -Force

$size = [math]::Round((Get-Item (Join-Path $Destination 'ffmpeg.exe')).Length / 1MB)
Write-Host "Staged ffmpeg $($manifest.version) ($size MB) into $Destination." -ForegroundColor Green
