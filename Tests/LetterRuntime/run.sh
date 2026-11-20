#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
export DOTNET_ROOT='E:/dsh/workspace/rimworld/tools/dotnet10'
export TEMP='E:/dsh/workspace/rimworld/TurboTick-headless/Tests/LetterRuntime/tmp'
export TMP="$TEMP" DOTNET_CLI_HOME="$TEMP/cli" NUGET_PACKAGES="$TEMP/nuget" DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false
mkdir -p "$TEMP"
"$DOTNET_ROOT/dotnet.exe" run --project Tests/LetterRuntime/LetterRuntime.csproj --no-launch-profile -- "$@"
