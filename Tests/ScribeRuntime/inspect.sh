#!/usr/bin/env bash
set -euo pipefail
export DOTNET_ROOT='E:/dsh/workspace/rimworld/tools/dotnet10'
export TEMP='E:/dsh/workspace/rimworld/TurboTick-headless/Tests/ScribeRuntime/tmp'
export TMP="$TEMP" DOTNET_CLI_HOME="$TEMP" DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
mkdir -p "$TEMP" 'Tests/ScribeRuntime/inspection'
for type in Verse.ScribeLoader Verse.CrossRefHandler Verse.LoadedObjectDirectory Verse.PostLoadIniter RimWorld.Planet.Settlement RimWorld.Planet.WorldObject Verse.Game Verse.DebugLoadIDsSavingErrorsChecker Verse.LoadIDsWantedBank Verse.Scribe_Deep Verse.Scribe_References RimWorld.Planet.MapParent; do
  "$DOTNET_ROOT/dotnet.exe" --roll-forward Major 'E:/dsh/workspace/tools/rea/vendor/ilspycmd/tools/net8.0/any/ilspycmd.dll' -t "$type" 'C:/SteamLibrary/steamapps/common/RimWorld/Assembly-CSharp.dll' > "Tests/ScribeRuntime/inspection/$type.cs"
done
