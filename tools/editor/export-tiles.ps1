<#
.SYNOPSIS
    Renders the radar tile pyramid the editor draws on, and optionally warms the art cache.

.DESCRIPTION
    Reads the client files through ServUO's own Server.TileMatrix, so the tiles can never disagree
    with what the shard thinks the map is. Output goes to tools/editor/tiles, which is gitignored -
    the tiles are derived data and re-rendering them is this one command.

    Safe to run while the shard is up: MapExport builds its Server.csproj reference into its own
    deps folder rather than the repo root, so it never touches the running ServUO.exe.

    A second facet is another run, not a code change.

.PARAMETER Facet
    Facet to render. Default Trammel.

.PARAMETER Client
    Client directory. Defaults to DataPath.CustomPath in Config/DataPath.cfg.

.PARAMETER Prerender
    A world rectangle "x,y,width,height" to warm the ISOMETRIC art cache over, after the radar
    pyramid. Optional, and never required: art tiles are rendered on demand by the bridge and
    cached forever, so a region becomes art simply by being looked at. This only makes the first
    look at somewhere a pan rather than a short wait at every step.

    There is no whole-facet art export and there cannot be one - Trammel's isometric canvas is
    247,808 pixels square, 61 gigapixels per floor.

    Britain is roughly:  -Prerender 1380,1495,365,345

.EXAMPLE
    .\tools\editor\export-tiles.ps1
    .\tools\editor\export-tiles.ps1 -Facet Felucca
#>
param(
    [string]$Facet = 'Trammel',
    [string]$Client,
    [string]$Prerender
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $root 'tools\MapExport\MapExport.csproj'

Write-Host ""
Write-Host "Building MapExport..." -ForegroundColor Cyan

& dotnet build $project -c Release --nologo -v quiet

if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED (exit $LASTEXITCODE). Tiles NOT rendered." -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = Join-Path $root 'tools\MapExport\bin\Release\MapExport.exe'

$mapArgs = @('--facet', $Facet)

if ($Client) {
    $mapArgs += @('--client', $Client)
}

Write-Host ""

& $exe @mapArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host "TILE EXPORT FAILED (exit $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

if ($Prerender) {
    Write-Host ""
    Write-Host "Warming the isometric art cache over $Prerender..." -ForegroundColor Cyan
    Write-Host ""

    $artArgs = @('--facet', $Facet, '--prerender', $Prerender)

    if ($Client) {
        $artArgs += @('--client', $Client)
    }

    & $exe @artArgs

    if ($LASTEXITCODE -ne 0) {
        Write-Host "PRERENDER FAILED (exit $LASTEXITCODE). The radar tiles are fine." -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

Write-Host ""
Write-Host "Start the editor with:  node tools\editor\bridge.js" -ForegroundColor Green
Write-Host ""
