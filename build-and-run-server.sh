#!/usr/bin/env bash
# Builds the headless dedicated server, then runs it with its log streamed to this terminal.
# Stop it with Ctrl+C.
#
# Usage: ./build-and-run-server.sh [extra args passed to the server]
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVER_DIR="$PROJECT_DIR/Builds/ShipDemoServer"
SERVER_EXE="$SERVER_DIR/ShipDemoServer.exe"
LOG_FILE="$SERVER_DIR/server.log"

"$PROJECT_DIR/build-server.sh"

# A Windows console app launched from Git Bash may not write to this terminal, so log to a
# file and follow it here instead.
rm -f "$LOG_FILE"
echo "Starting $SERVER_EXE (Ctrl+C to stop)"
"$SERVER_EXE" -batchmode -nographics -logFile "$(cygpath -w "$LOG_FILE")" "$@" &
SERVER_PID=$!
trap 'kill "$SERVER_PID" 2>/dev/null || true' INT TERM EXIT

tail -n +1 -F --pid="$SERVER_PID" "$LOG_FILE" 2>/dev/null
wait "$SERVER_PID"
