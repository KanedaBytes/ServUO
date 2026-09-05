#!/usr/bin/env bash
#
# Builds ServUO and launches the shard, aborting if the build fails.
#
# Use this instead of `make`.
#
# ServUO's ScriptCompiler does NOT check the exit code of its boot-time `dotnet build`.
# With Compiler.cfg Dynamic=True it will happily load the previous, stale Scripts.dll after a
# failed compile, so a broken script produces a fully working server with your change silently
# missing. This shard sets Compiler.cfg Dynamic=False and builds here instead, where a non-zero
# exit code stops us before the server ever starts.
#
# Usage:
#   ./build.sh              build + run
#   ./build.sh --no-run     build only
#   ./build.sh --debug      Debug configuration, launches with -debug

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

CONFIG="Release"
RUN=1
RUN_ARGS=()

while [ $# -gt 0 ]; do
    case "$1" in
        --no-run) RUN=0 ;;
        --debug)  CONFIG="Debug"; RUN_ARGS=("-debug") ;;
        -h|--help)
            sed -n '3,18p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            echo "Usage: ./build.sh [--no-run] [--debug]" >&2
            exit 2
            ;;
    esac
    shift
done

echo
echo "Building ServUO ($CONFIG)..."
echo

if ! dotnet build "$ROOT/ServUO.sln" -c "$CONFIG"; then
    echo
    echo "BUILD FAILED. Server NOT started." >&2
    echo "Scripts.dll is unchanged - fix the errors above and build again." >&2
    echo
    exit 1
fi

echo
echo "Build succeeded."

if [ "$RUN" -eq 0 ]; then
    exit 0
fi

EXE="$ROOT/ServUO.exe"
if [ ! -f "$EXE" ]; then
    echo "ServUO.exe not found at $EXE" >&2
    exit 1
fi

echo "Starting shard..."
echo

if command -v mono >/dev/null 2>&1; then
    exec mono "$EXE" ${RUN_ARGS[@]+"${RUN_ARGS[@]}"}
else
    exec "$EXE" ${RUN_ARGS[@]+"${RUN_ARGS[@]}"}
fi
