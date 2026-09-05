<#
.SYNOPSIS
    Builds ServUO and launches the shard, aborting if the build fails.

.DESCRIPTION
    Use this instead of _winrelease.bat / _windebug.bat.

    ServUO's ScriptCompiler does NOT check the exit code of its boot-time `dotnet build`.
    With Compiler.cfg Dynamic=True it will happily load the previous, stale Scripts.dll after
    a failed compile, so a broken script produces a fully working server with your change
    silently missing.

    This shard sets Compiler.cfg Dynamic=False and builds here instead, where a non-zero exit
    code stops us before the server ever starts.

.PARAMETER NoRun
    Build only; do not launch the server.

.PARAMETER Debug
    Build the Debug configuration and launch with -debug.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -NoRun
    .\build.ps1 -Debug
#>
# NOTE: no [CmdletBinding()] here on purpose. It would make -Debug a reserved PowerShell
# common parameter, and declaring our own -Debug switch alongside it fails at parse time with
# "A parameter with the name 'Debug' was defined multiple times for the command."
param(
    [switch]$NoRun,
    [switch]$Debug
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if ($Debug) {
    $config = 'Debug'
    $runArgs = @('-debug')
} else {
    $config = 'Release'
    $runArgs = @()
}

Write-Host ""
Write-Host "Building ServUO ($config)..." -ForegroundColor Cyan
Write-Host ""

& dotnet build (Join-Path $root 'ServUO.sln') -c $config
$buildExit = $LASTEXITCODE

if ($buildExit -ne 0) {
    Write-Host ""
    Write-Host "BUILD FAILED (exit $buildExit). Server NOT started." -ForegroundColor Red
    Write-Host "Scripts.dll is unchanged - fix the errors above and build again." -ForegroundColor Red
    Write-Host ""
    exit $buildExit
}

Write-Host ""
Write-Host "Build succeeded." -ForegroundColor Green

if ($NoRun) {
    exit 0
}

$exe = Join-Path $root 'ServUO.exe'
if (-not (Test-Path $exe)) {
    Write-Host "ServUO.exe not found at $exe" -ForegroundColor Red
    exit 1
}

Write-Host "Starting shard..." -ForegroundColor Cyan
Write-Host ""

& $exe @runArgs
exit $LASTEXITCODE
