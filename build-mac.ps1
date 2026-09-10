<#
.SYNOPSIS
    Builds the macOS .app bundles for VoidGrab.

.DESCRIPTION
    Publishes the Avalonia head for Apple silicon and Intel, then packages each
    into a VoidGrab.app bundle inside a .tar.gz.

    A tarball, not a zip, and written by a Python script rather than Compress-Archive:
    NTFS has no executable bit and a zip written on Windows carries no Unix mode,
    so an .app packed that way arrives on a Mac with a non-executable binary and
    refuses to launch. scripts/package-macos.py sets each entry's mode explicitly.

    These builds are UNSIGNED. Signing and notarising need an Apple Developer ID
    and a Mac, so on first launch Gatekeeper will quarantine the app. The README
    documents the one command that clears it.

.EXAMPLE
    .\build-mac.ps1
    .\build-mac.ps1 -Runtime osx-arm64
#>
[CmdletBinding()]
param(
    [ValidateSet('both', 'osx-arm64', 'osx-x64')]
    [string]$Runtime = 'both',

    [string]$Version = '1.0.0',

    [string]$Output = "$PSScriptRoot\artifacts"
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'src\VoidGrab.Desktop\VoidGrab.Desktop.csproj'
if (-not (Test-Path $project)) { throw "Project not found at $project" }

$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) { throw "python is required to assemble the .app bundle (it sets Unix file modes)" }

$runtimes = if ($Runtime -eq 'both') { @('osx-arm64', 'osx-x64') } else { @($Runtime) }

foreach ($rid in $runtimes) {
    Write-Host ""
    Write-Host "Publishing $rid..." -ForegroundColor Cyan

    $publishDir = Join-Path $Output $rid

    dotnet publish $project `
        --configuration Release `
        --runtime $rid `
        --self-contained true `
        --output $publishDir `
        --nologo `
        -v quiet

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid (exit $LASTEXITCODE)" }

    Write-Host "Packaging $rid..." -ForegroundColor Cyan
    & python (Join-Path $PSScriptRoot 'scripts\package-macos.py') $publishDir --version $Version
    if ($LASTEXITCODE -ne 0) { throw "Packaging failed for $rid (exit $LASTEXITCODE)" }
}

Write-Host ""
Write-Host "Done. Hand a Mac user the tarball for their chip:" -ForegroundColor Green
Get-ChildItem (Join-Path $Output 'VoidGrab-macos-*.tar.gz') -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host ("  {0}  ({1:N0} MB)" -f $_.Name, ($_.Length / 1MB)) }

Write-Host ""
Write-Host "  Apple silicon (M1-M4) -> arm64      Intel -> x64" -ForegroundColor DarkGray
Write-Host "  These are unsigned; see the README for the Gatekeeper step." -ForegroundColor DarkGray
