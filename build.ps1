<#
.SYNOPSIS
    Publishes VoidGrab as a self-contained Windows executable.

.DESCRIPTION
    Produces publish\VoidGrab.exe — one file, no .NET runtime required on the
    target machine.

    yt-dlp and ffmpeg are deliberately NOT bundled. The app fetches them into a
    tools\ folder next to itself on first run, which keeps this build small,
    keeps launch instant, and — the real reason — lets yt-dlp be replaced from
    inside the app whenever a site changes how it serves media. A copy sealed
    into the executable would go stale and could not be fixed without a rebuild.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Output = "$PSScriptRoot\publish"
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'src\VoidGrab\VoidGrab.csproj'
if (-not (Test-Path $project)) { throw "Project not found at $project" }

Write-Host "Publishing VoidGrab ($Configuration)..." -ForegroundColor Cyan

dotnet publish $project `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $Output `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $Output 'VoidGrab.exe'
if (-not (Test-Path $exe)) { throw "Publish finished but produced no VoidGrab.exe" }

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ""
Write-Host "Built $exe ($sizeMb MB)" -ForegroundColor Green
Write-Host "yt-dlp and ffmpeg download into tools\ on first run (~190 MB, once)."
Write-Host "Verify a copy with:  .\VoidGrab.exe --selftest"
