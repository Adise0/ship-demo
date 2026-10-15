#!/usr/bin/env bash
# Builds the headless dedicated server (Builds/ShipDemoServer/ShipDemoServer.exe).
# Close the project in the Unity editor first: batch mode can't open a project that's already open.
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

echo "Building dedicated server with $UNITY_EXE"
"$UNITY_EXE" -batchmode -quit \
    -projectPath "$(cygpath -w "$PROJECT_DIR")" \
    -executeMethod ShipDemoBuild.BuildServer \
    -logFile -

echo "Done: $PROJECT_DIR/Builds/ShipDemoServer/ShipDemoServer.exe"
