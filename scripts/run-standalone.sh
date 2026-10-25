#!/usr/bin/env bash
# Builds and runs the standalone Federation host. Arguments are passed through, e.g.
#   scripts/run-standalone.sh --port 51780 --bind 0.0.0.0 --lan --log-level debug
#
# Environment:
#   CONFIG          Debug | Release (default Debug)
#   MCVS_DATA       data directory (default ./data)
#   MCVS_DATAPACK   the game's on-device Data folder (default /home/user/mcvs-work/datapack/1.17.32/ETC/files/Data if present)
#
# Dev loop with a stock APK on a USB device: adb reverse tcp:51780 tcp:51780, then push args.txt (ARCHITECTURE §2.3).
set -euo pipefail
cd "$(dirname "$0")/.."

if ! command -v dotnet >/dev/null 2>&1; then
  for d in "${DOTNET_ROOT:-}" "$HOME/.dotnet" /root/.dotnet; do
    if [ -n "$d" ] && [ -x "$d/dotnet" ]; then export DOTNET_ROOT="$d" PATH="$PATH:$d:$d/tools"; break; fi
  done
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

CONFIG="${CONFIG:-Debug}"
config_dir="$(echo "$CONFIG" | tr '[:upper:]' '[:lower:]')"
DATAPACK="${MCVS_DATAPACK:-/home/user/mcvs-work/datapack/1.17.32/ETC/files/Data}"

dotnet build hosts/standalone/McvsOffline.Standalone.csproj -c "$CONFIG" -v:quiet -nologo

args=(--data "${MCVS_DATA:-./data}")
if [ -d "$DATAPACK" ]; then args+=(--datapack "$DATAPACK"); fi

# exec so Ctrl+C / SIGTERM reach the server directly (it shuts down cleanly).
exec dotnet "artifacts/bin/McvsOffline.Standalone/$config_dir/McvsOffline.Standalone.dll" "${args[@]}" "$@"
