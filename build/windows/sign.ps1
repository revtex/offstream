<#
.SYNOPSIS
    Authenticode-signs the files it is given, or explains why it did not.

.DESCRIPTION
    Offstream has no code-signing certificate yet (open question 3, deferred 2026-08-14),
    and buying one is procurement rather than engineering. So this script is deliberately
    a no-op when none is configured: it says so on stdout and exits 0, and the release
    still builds.

    That is the whole point of it existing now rather than later. Acquiring a certificate
    becomes "set two secrets" instead of "change the release pipeline", and the shape of
    the signing step gets reviewed while nothing depends on it.

    Configuration is by environment variable, because that is what a GitHub Actions secret
    reaches a script as:

      OFFSTREAM_SIGNING_PFX_BASE64   base64 of a PKCS#12 (.pfx) certificate  [required]
      OFFSTREAM_SIGNING_PASSWORD     its password                            [optional]
      OFFSTREAM_SIGNING_TIMESTAMP_URL  RFC 3161 timestamp server             [optional]

    Timestamping is not optional in effect, only in configuration: without it, every
    signature this produces stops verifying the day the certificate expires, including on
    copies users installed years earlier. The default below is DigiCert's public server.

.NOTES
    Nothing here prints the password, the certificate, or a signtool command line
    containing either. The .pfx is written to a temporary file because signtool takes a
    path and not bytes, and it is removed in a finally block so a failure part-way through
    does not leave a private key on the runner.

.EXAMPLE
    .\sign.ps1 -Path .\publish\Offstream.exe
    .\sign.ps1 -Path (Get-ChildItem .\publish -Filter *.exe)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, ValueFromPipeline)]
    [string[]] $Path,

    # Fail instead of skipping when no certificate is configured. The release workflow
    # leaves this off; a pipeline that is *supposed* to produce signed output turns it on
    # so a missing secret is a red build rather than a quietly unsigned artefact.
    [switch] $Require
)

$ErrorActionPreference = 'Stop'

$files = @($Path | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
if (-not $files) { throw 'sign.ps1 was given no files to sign.' }

$pfxBase64 = $env:OFFSTREAM_SIGNING_PFX_BASE64

if ([string]::IsNullOrWhiteSpace($pfxBase64)) {
    $message = 'No signing certificate is configured (OFFSTREAM_SIGNING_PFX_BASE64 is unset).'

    if ($Require) { throw "$message This build was required to be signed." }

    Write-Host $message -ForegroundColor Yellow
    Write-Host 'Leaving the following unsigned. Windows SmartScreen will warn on first run:' -ForegroundColor Yellow
    $files | ForEach-Object { Write-Host "  $_" }
    exit 0
}

# --- Locate signtool --------------------------------------------------------

# signtool ships in the Windows SDK and is not on PATH by default, including on GitHub's
# windows-latest images. Newest version wins - the SDK installs side by side by version,
# and older ones predate the SHA-256 and RFC 3161 switches used below.
$signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty Source

if (-not $signtool) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'

    if (Test-Path $kits) {
        $signtool = Get-ChildItem -Path $kits -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object -Property FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
}

if (-not $signtool) {
    throw 'A signing certificate is configured but signtool.exe was not found. Install the Windows SDK signing tools.'
}

# --- Sign -------------------------------------------------------------------

$pfx = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName() + '.pfx')
$timestamp = if ($env:OFFSTREAM_SIGNING_TIMESTAMP_URL) { $env:OFFSTREAM_SIGNING_TIMESTAMP_URL }
             else { 'http://timestamp.digicert.com' }

try {
    [System.IO.File]::WriteAllBytes($pfx, [System.Convert]::FromBase64String($pfxBase64))

    $arguments = @(
        'sign'
        '/f', $pfx
        '/fd', 'sha256'          # SHA-1 file digests are rejected outright by current Windows.
        '/tr', $timestamp
        '/td', 'sha256'
        '/d', 'Offstream'        # What the UAC and SmartScreen prompts call it.
    )

    if ($env:OFFSTREAM_SIGNING_PASSWORD) { $arguments += @('/p', $env:OFFSTREAM_SIGNING_PASSWORD) }

    foreach ($file in $files) {
        Write-Host "Signing $file"

        # Passed as one array so the password never appears in a command string this script
        # could log, and so a path with a space needs no quoting of its own.
        & $signtool @arguments $file | Out-Null

        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $file (exit $LASTEXITCODE)." }
    }

    foreach ($file in $files) {
        & $signtool verify /pa /v $file | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "The signature on $file did not verify." }
    }

    Write-Host "Signed and verified $($files.Count) file(s)." -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $pfx) { Remove-Item -LiteralPath $pfx -Force }
}
