#!/usr/bin/env bash
# Builds the headless dedicated server, then runs it in this terminal. Stop it with Ctrl+C.
# Close the project in the Unity editor first: batch mode can't open a project that's already open.
#
# Usage: ./build-and-run-server.sh [extra args passed to the server]
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVER_EXE="$PROJECT_DIR/Builds/ShipDemoServer/ShipDemoServer.exe"

"$PROJECT_DIR/build-server.sh"

echo "Starting $SERVER_EXE (Ctrl+C to stop)"
exec "$SERVER_EXE" -logFile - "$@"
