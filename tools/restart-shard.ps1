<#
.SYNOPSIS
    Builds and starts the shard, in its own window. dev.ps1's shard half, on its own.

.DESCRIPTION
    The editor's Admin panel restarts the shard by saving, stopping it, and then running this.
    It is a separate script rather than something the bridge spawns directly for two reasons:

    IT MUST GO THROUGH build.ps1. This shard's entire build story is that a failed build has to be
    visible rather than swallowed - ScriptCompiler.Compile never checks the dotnet exit code and
    will happily boot a stale Scripts.dll with your change silently absent (CLAUDE.md section 2).
    build.ps1 is the thing that refuses to launch on a failed build, so a restart that started
    ServUO.exe directly would be the one path back around the hole the build scripts exist to close.

    AND THE BRIDGE PASSES IT NOTHING. One known script, no caller-supplied arguments, so there is
    no command line to steer. The bridge's only other child process (the art renderer) works the
    same way.

    A window, not a job, for dev.ps1's reason: the shard's console is where its log goes, -NoExit
    keeps it on screen, and a crash leaves its own evidence.

.PARAMETER Debug
    Build and launch the Debug configuration.

.EXAMPLE
    .\tools\restart-shard.ps1
#>
# NOTE: no [CmdletBinding()], for build.ps1's and dev.ps1's reason - it would make -Debug a
# reserved common parameter and this file would fail to parse.
param(
    [switch]$Debug
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

if (Get-Process ServUO -ErrorAction SilentlyContinue) {
    Write-Host "A shard is already running - refusing to start a second one." -ForegroundColor Red
    Write-Host "Scripts.dll is locked while it is loaded, so the build would fail anyway."
    exit 1
}

$buildArgs = if ($Debug) { '-Debug' } else { '' }

# -NoExit so the window survives whatever happens: a build failure, a crash, or a clean exit all
# leave their last words on screen instead of closing over them.
Start-Process powershell -ArgumentList @(
    '-NoExit', '-NoProfile', '-Command',
    "`$Host.UI.RawUI.WindowTitle = 'Goblin Gang - shard'; & '$(Join-Path $root 'build.ps1')' $buildArgs"
) | Out-Null

Write-Host "Shard build and launch started in its own window." -ForegroundColor Green
