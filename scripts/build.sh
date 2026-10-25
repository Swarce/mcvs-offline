#!/usr/bin/env bash
# Builds everything and runs the tests:
#   1. McvsOffline.sln (net8: server library, standalone host, patcher, tests)
#   2. server/McvsOffline.Server.Embedded.csproj against the game's own BCL (MCVS_REFS)
#   3. dotnet test (with the embedded-DLL reference check made mandatory when step 2 ran)
#
# Environment:
#   CONFIG          Debug | Release (default Release)
#   MCVS_REFS       assemblies/ folder of the player's MCVS 1.17.32 APK (default /home/user/mcvs-work/extracted/assemblies)
#   MCVS_DATAPACK   optional on-device Data folder; enables the real-data-pack decoder test
#   MCVS_STRICT=1   fail instead of skipping the embedded build when MCVS_REFS is missing
set -euo pipefail
cd "$(dirname "$0")/.."

if ! command -v dotnet >/dev/null 2>&1; then
  for d in "${DOTNET_ROOT:-}" "$HOME/.dotnet" /root/.dotnet; do
    if [ -n "$d" ] && [ -x "$d/dotnet" ]; then export DOTNET_ROOT="$d" PATH="$PATH:$d:$d/tools"; break; fi
  done
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

CONFIG="${CONFIG:-Release}"
MCVS_REFS="${MCVS_REFS:-/home/user/mcvs-work/extracted/assemblies}"
if [ -z "${MCVS_DATAPACK:-}" ] && [ -d /home/user/mcvs-work/datapack/1.17.32/ETC/files/Data ]; then
  export MCVS_DATAPACK=/home/user/mcvs-work/datapack/1.17.32/ETC/files/Data
fi

echo "==> dotnet build McvsOffline.sln ($CONFIG)"
dotnet build McvsOffline.sln -c "$CONFIG"

if [ -f "$MCVS_REFS/mscorlib.dll" ]; then
  echo "==> embedded build against $MCVS_REFS"
  dotnet build server/McvsOffline.Server.Embedded.csproj -c "$CONFIG" -p:MCVS_REFS="$MCVS_REFS"
  export MCVS_REQUIRE_EMBEDDED=1
else
  echo "warning: MCVS_REFS='$MCVS_REFS' has no mscorlib.dll; skipping the embedded build" >&2
  if [ "${MCVS_STRICT:-0}" = "1" ]; then exit 1; fi
fi

echo "==> dotnet test"
dotnet test McvsOffline.sln -c "$CONFIG" --no-build

config_dir="$(echo "$CONFIG" | tr '[:upper:]' '[:lower:]')"
if [ "${MCVS_REQUIRE_EMBEDDED:-0}" = "1" ]; then
  echo "embedded server DLL: artifacts/bin/McvsOffline.Server.Embedded/$config_dir/McvsOffline.Server.dll"
fi
