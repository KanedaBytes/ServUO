<#
.SYNOPSIS
    Starts everything: the shard, the editor bridge, and a browser pointed at the editor.

.DESCRIPTION
    Running this shard for development means three things in two terminals - build and launch the
    server, start the bridge, and open the editor - plus rendering the map tiles the first time.
    This is that, once.

    TWO WINDOWS, NOT BACKGROUND JOBS. The shard's console is where its log goes, and this shard's
    entire build story is that a failed build must be visible rather than swallowed (see build.ps1).
    A job would hide both. Each window stays open on exit so a crash leaves its own evidence.

    Nothing here is required: the two commands it wraps still work on their own, and this script
    starts them exactly as you would.

.PARAMETER NoBrowser
    Start the shard and the bridge, but do not open a browser.

.PARAMETER NoShard
    Start only the bridge. Useful when the shard is already running - the editor reads files and
    drops request tokens, so it works against a shard this script did not start.

.PARAMETER Tiles
    Render the map tiles even if they are already there.

.PARAMETER Port
    Bridge port. Default 8081.

.PARAMETER Debug
    Build and launch the shard in the Debug configuration.

.EXAMPLE
    .\tools\dev.ps1
    .\tools\dev.ps1 -NoShard
    .\tools\dev.ps1 -Tiles -Facet Felucca
#>
# NOTE: no [CmdletBinding()] here on purpose, for the same reason build.ps1 has none - it would
# make -Debug a reserved common parameter and this file would fail to parse.
param(
    [switch]$NoBrowser,
    [switch]$NoShard,
    [switch]$Tiles,
    [int]$Port = 8081,
    [string]$Facet = 'Trammel',
    [switch]$Debug
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$editor = Join-Path $root 'tools\editor'
$tileDir = Join-Path $editor 'tiles'
$url = "http://127.0.0.1:$Port/"

function Start-InWindow($title, $command) {
    # -NoExit so the window survives whatever happens: a build failure, a crash, or a clean exit
    # all leave their last words on screen instead of closing over them.
    Start-Process powershell -ArgumentList @(
        '-NoExit', '-NoProfile', '-Command',
        "`$Host.UI.RawUI.WindowTitle = '$title'; $command"
    ) | Out-Null
}

# ---- tiles -------------------------------------------------------------------------------------
# Rendered once and then left alone. They are derived from the client files, not from anything the
# shard or the editor writes, so they only go stale when the client does.

$haveTiles = (Test-Path $tileDir) -and
    ((Get-ChildItem -Path $tileDir -Filter *.png -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1) -ne $null)

if ($Tiles -or -not $haveTiles) {
    if (-not $haveTiles) {
        Write-Host ""
        Write-Host "No map tiles yet - rendering them once (about six seconds)." -ForegroundColor Cyan
    }

    & (Join-Path $editor 'export-tiles.ps1') -Facet $Facet

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Tile export failed. Nothing started." -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

# ---- the shard ---------------------------------------------------------------------------------

if (-not $NoShard) {
    if (Get-Process ServUO -ErrorAction SilentlyContinue) {
        Write-Host "A shard is already running - leaving it alone." -ForegroundColor Yellow
    } else {
        Write-Host "Starting the shard..." -ForegroundColor Cyan

        $buildArgs = if ($Debug) { '-Debug' } else { '' }
        Start-InWindow 'Goblin Gang - shard' "& '$(Join-Path $root 'build.ps1')' $buildArgs"
    }
}

# ---- the bridge --------------------------------------------------------------------------------

Write-Host "Starting the editor bridge on port $Port..." -ForegroundColor Cyan

Start-InWindow 'Goblin Gang - editor bridge' `
    "Set-Location '$root'; node '$(Join-Path $editor 'bridge.js')' --port $Port"

# ---- wait for the bridge, then open it ---------------------------------------------------------
# The bridge is up in well under a second; the shard takes longer and the editor does not need it
# to draw. So this waits only for the bridge, and the editor's own health panel reports the shard.

$ready = $false

foreach ($attempt in 1..40) {
    Start-Sleep -Milliseconds 250

    try {
        Invoke-WebRequest -Uri "${url}api/status" -UseBasicParsing -TimeoutSec 2 | Out-Null
        $ready = $true
        break
    } catch {
        # Not up yet. The bridge binds 127.0.0.1 only, so this can only ever be our own.
    }
}

Write-Host ""

if (-not $ready) {
    Write-Host "The bridge did not answer on $url after 10s - check its window." -ForegroundColor Red
    exit 1
}

Write-Host "Editor:  $url" -ForegroundColor Green
Write-Host "Client:  127.0.0.1:2594" -ForegroundColor Green
Write-Host ""

if (-not $NoBrowser) {
    Start-Process $url
}
