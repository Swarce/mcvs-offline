#!/usr/bin/env bash
# Builds everything and runs the tests:
#   1. McvsOffline.sln (net8: server library, standalone host, patcher, tests)
#   2. server/McvsOffline.Server.Embedded.csproj against the game's own BCL (MCVS_REFS)
#   3. dotnet test (McvsOffline.Tests + McvsOffline.Embed.Tests). When step 2 ran, the embedded-DLL reference check
#      and the real-client conformance tests (which load Gameloft.OnlinePlatform.dll from MCVS_REFS) are mandatory.
#
# Environment:
#   CONFIG          Debug | Release (default Release)
#   MCVS_REFS       assemblies/ folder of the player's MCVS 1.17.32 APK (default /home/user/mcvs-work/extracted/assemblies);
#                   exported, so the tests load the same assemblies
#   MCVS_DATAPACK   optional on-device Data folder; enables the real-data-pack tests
#   MCVS_APK        optional original APK; enables the patcher's real-APK tests (default /home/user/mcvs-work/apk/mcvs.apk)
#   MCVS_STRICT=1   fail instead of skipping the embedded build when MCVS_REFS is missing
#   MCVS_REQUIRE_REAL_CLIENT=0  let the real-client tests skip even though MCVS_REFS has the game's assemblies
#   MCVS_PC_REFS    the Steam install folder of the PC build (default /home/user/mcvs-work/pc/install when it exists);
#                   exported, so the solution build compiles the PC server DLL with the game-log bridge and the PC tests
#                   read the real PC files
#   MCVS_REQUIRE_PC=0  let the PC real-file tests skip even though MCVS_PC_REFS has the game's assemblies
#   (step 1 also builds the PC server DLL: artifacts/bin/McvsOffline.Server.Pc/<config>/McvsOffline.Server.dll)
set -euo pipefail
cd "$(dirname "$0")/.."

if ! command -v dotnet >/dev/null 2>&1; then
  for d in "${DOTNET_ROOT:-}" "$HOME/.dotnet" /root/.dotnet; do
    if [ -n "$d" ] && [ -x "$d/dotnet" ]; then export DOTNET_ROOT="$d" PATH="$PATH:$d:$d/tools"; break; fi
  done
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

CONFIG="${CONFIG:-Release}"
export MCVS_REFS="${MCVS_REFS:-/home/user/mcvs-work/extracted/assemblies}"
if [ -z "${MCVS_APK:-}" ] && [ -f /home/user/mcvs-work/apk/mcvs.apk ]; then
  export MCVS_APK=/home/user/mcvs-work/apk/mcvs.apk
fi
if [ -z "${MCVS_DATAPACK:-}" ] && [ -d /home/user/mcvs-work/datapack/1.17.32/ETC/files/Data ]; then
  export MCVS_DATAPACK=/home/user/mcvs-work/datapack/1.17.32/ETC/files/Data
fi
# The Steam (PC) install, read only (docs/design/pc-port.md §3.7): Gameloft.Logs.dll for the PC server DLL (MCVS_GAMELOG)
# and the real PC files for every PC test. With its Gameloft.Engine.dll present those tests are mandatory.
if [ -z "${MCVS_PC_REFS:-}" ] && [ -d /home/user/mcvs-work/pc/install ]; then
  export MCVS_PC_REFS=/home/user/mcvs-work/pc/install
fi
if [ -n "${MCVS_PC_REFS:-}" ] && [ -f "$MCVS_PC_REFS/Gameloft.Engine.dll" ]; then
  export MCVS_REQUIRE_PC="${MCVS_REQUIRE_PC:-1}"
fi

echo "==> dotnet build McvsOffline.sln ($CONFIG)"
dotnet build McvsOffline.sln -c "$CONFIG"

if [ -f "$MCVS_REFS/mscorlib.dll" ]; then
  echo "==> embedded build against $MCVS_REFS"
  dotnet build server/McvsOffline.Server.Embedded.csproj -c "$CONFIG" -p:MCVS_REFS="$MCVS_REFS"
  export MCVS_REQUIRE_EMBEDDED=1
  if [ -f "$MCVS_REFS/Gameloft.OnlinePlatform.dll" ]; then
    export MCVS_REQUIRE_REAL_CLIENT="${MCVS_REQUIRE_REAL_CLIENT:-1}"
  fi
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
echo "PC server DLL: artifacts/bin/McvsOffline.Server.Pc/$config_dir/McvsOffline.Server.dll"
