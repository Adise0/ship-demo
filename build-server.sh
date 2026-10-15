#!/usr/bin/env bash
# Builds the headless dedicated server (Builds/ShipDemoServer/ShipDemoServer.exe).
# If the Unity editor has the project open, the build runs inside it; otherwise in batch mode.
#
# Usage: ./build-server.sh
# Override the editor with UNITY_EXE=/path/to/Unity.exe ./build-server.sh
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ -z "${UNITY_EXE:-}" ]]; then
    VERSION="$(sed -n 's/^m_EditorVersion: //p' "$PROJECT_DIR/ProjectSettings/ProjectVersion.txt" | tr -d '\r')"
    UNITY_EXE="/c/Program Files/Unity/Hub/Editor/$VERSION/Editor/Unity.exe"
fi

if [[ ! -f "$UNITY_EXE" ]]; then
    echo "Unity editor not found at: $UNITY_EXE" >&2
    echo "Set UNITY_EXE to your Unity.exe path." >&2
    exit 1
fi

# Batch mode exits immediately if the editor already has this project open.
EDITOR_PID="$(powershell.exe -NoProfile -Command \
    "(Get-CimInstance Win32_Process -Filter \"Name='Unity.exe'\" | Where-Object { \$_.CommandLine -like '*$(cygpath -w "$PROJECT_DIR")*' -and \$_.CommandLine -notlike '*-batchmode*' } | Select-Object -First 1).ProcessId" \
    | tr -d '\r')"
if [[ -n "$EDITOR_PID" ]]; then
    # Build through the open editor instead (see ShipDemoBuildRequests.cs), streaming its log.
    REQUEST_DIR="$PROJECT_DIR/Temp/ShipDemoBuild"
    REQUEST="$REQUEST_DIR/server.request"
    RESULT="$REQUEST_DIR/server.result"
    mkdir -p "$REQUEST_DIR"
    rm -f "$RESULT"

    echo "Unity editor (PID $EDITOR_PID) has the project open; building through it."
    tail -n 0 -F "$LOCALAPPDATA/Unity/Editor/Editor.log" 2>/dev/null &
    TAIL_PID=$!
    trap 'kill "$TAIL_PID" 2>/dev/null || true; rm -f "$REQUEST"' EXIT

    touch "$REQUEST"
    for ((waited = 0; waited < 60; waited++)); do
        [[ -f "$REQUEST" ]] || break
        sleep 1
    done
    if [[ -f "$REQUEST" ]]; then
        echo "The editor didn't pick up the build request within 60s." >&2
        echo "Make sure it has finished compiling and isn't showing a dialog." >&2
        exit 1
    fi

    until [[ -f "$RESULT" ]]; do sleep 1; done
    BUILD_RESULT="$(cat "$RESULT")"
    if [[ "$BUILD_RESULT" != succeeded ]]; then
        echo "Build $BUILD_RESULT" >&2
        exit 1
    fi
    exit 0
fi

echo "Building dedicated server with $UNITY_EXE"
"$UNITY_EXE" -batchmode -quit \
    -projectPath "$(cygpath -w "$PROJECT_DIR")" \
    -executeMethod ShipDemoBuild.BuildServer \
    -logFile -

echo "Done: $PROJECT_DIR/Builds/ShipDemoServer/ShipDemoServer.exe"
