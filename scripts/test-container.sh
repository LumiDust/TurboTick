#!/usr/bin/env bash
# Offline contract tests: uses only a locally built image, fake game/Harmony/
# SteamCMD, disposable named volumes, and --network none. Never touches live data.
set -euo pipefail
image="${1:-ghcr.io/lumidust/turbotick-headless:0.6.1}"
command -v docker >/dev/null || { echo 'docker is required' >&2; exit 1; }
docker image inspect "$image" >/dev/null
prefix="turbotick-container-test-$$-$(date +%s)"
volumes=("$prefix-game" "$prefix-profile" "$prefix-state" "$prefix-steam")
running="$prefix-running"
cleanup() {
  docker rm -f "$running" >/dev/null 2>&1 || true
  docker volume rm "${volumes[@]}" >/dev/null 2>&1 || true
}
trap cleanup EXIT
for volume in "${volumes[@]}"; do docker volume create "$volume" >/dev/null; done
rw=(-v "${volumes[0]}:/game" -v "${volumes[1]}:/profile" -v "${volumes[2]}:/state")
ro=(-v "${volumes[0]}:/game:ro" -v "${volumes[1]}:/profile" -v "${volumes[2]}:/state")
base=(--network none --read-only --tmpfs /tmp:rw,exec,nosuid,nodev,size=64m,mode=1777)
admin() { docker run --rm -i "${base[@]}" "${rw[@]}" -v "${volumes[3]}:/steam" --user 0 --entrypoint bash "$image" -s; }
expect_exit() {
  local expected="$1" actual
  shift
  set +e
  "$@"
  actual=$?
  set -e
  [[ "$actual" -eq "$expected" ]] || { printf 'FAIL: expected exit %s, got %s\n' "$expected" "$actual" >&2; exit 1; }
}
pass() { printf 'PASS %s\n' "$*"; }

# Harmony/MonoMod loads a generated native helper from the temp directory.
# Docker tmpfs defaults to noexec even when it is writable; cover actual dlopen.
docker run --rm "${base[@]}" --entrypoint python3 "$image" -c 'import ctypes,pathlib,shutil; p=pathlib.Path("/tmp/turbotick-native.so"); shutil.copyfile("/usr/lib/x86_64-linux-gnu/libz.so.1",p); ctypes.CDLL(str(p)); p.unlink()'
pass 'temporary native helpers can be mapped executable for Harmony'

admin <<'FIXTURE'
set -euo pipefail
mkdir -p /game/Data/Core /game/Mods/Harmony/{About,1.7/Assemblies,Languages/Test} /profile/Config /profile/Saves /state /steam/steamcmd
printf '1.7.5000 rev1275\n' > /game/Version.txt
for name in Assembly-CSharp.dll RimWorld.dll Waylith.dll UnityStubs.dll Assets.zip; do printf 'fixture\n' > "/game/$name"; done
cp -a /opt/turbotick/mod/TurboTick /game/Mods/TurboTick
printf '<ModMetaData><packageId>brrainz.harmony</packageId></ModMetaData>\n' > /game/Mods/Harmony/About/About.xml
printf '<loadFolders><v1.7><li>/</li><li>1.7</li></v1.7></loadFolders>\n' > /game/Mods/Harmony/LoadFolders.xml
printf 'fake Harmony 3\n' > /game/Mods/Harmony/1.7/Assemblies/0Harmony.dll
printf 'original complete Harmony directory\n' > /game/Mods/Harmony/Languages/Test/marker
cat > /profile/Config/ModsConfig.xml <<'XML'
<ModsConfigData><version>1.7.5000</version><activeMods><li>ludeon.rimworld</li><li>brrainz.harmony</li><li>lumidust.turbotick</li><li>fixture.custom</li></activeMods><knownExpansions /></ModsConfigData>
XML
printf 'untouched save\n' > /profile/Saves/Keep.rws
sha256sum /profile/Config/ModsConfig.xml /profile/Saves/Keep.rws > /state/preserve.sha256
cat > /game/RimWorld <<'PY'
#!/usr/bin/python3
import datetime, json, os, pathlib, signal, sys, time
state = pathlib.Path('/state')
(state / 'argv.json').write_text(json.dumps({'argv': sys.argv[1:], 'home': os.environ['HOME'], 'xdg': os.environ['XDG_CONFIG_HOME'], 'data': os.environ['TURBOTICK_DATA'], 'cache': pathlib.Path('/steam/steamcmd/steamcmd.sh').exists(), 'steam_user': 'STEAM_USER' in os.environ}))
for arg in sys.argv:
    if arg.startswith('--fixture-exit='):
        sys.exit(int(arg.split('=', 1)[1]))
def stop(sig, frame):
    (state / 'signal').write_text(signal.Signals(sig).name)
    sys.exit(23)
signal.signal(signal.SIGTERM, stop)
while True:
    (state / 'headless-status.json.tmp').write_text(json.dumps({'utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'connected': True, 'state': 'lobby', 'failure': None}))
    (state / 'headless-status.json.tmp').replace(state / 'headless-status.json')
    time.sleep(.1)
PY
chmod +x /game/RimWorld
cat > /steam/steamcmd/steamcmd.sh <<'PY'
#!/usr/bin/python3
import json, pathlib, shutil, sys
cache = pathlib.Path('/steam')
with (cache / 'calls.jsonl').open('a') as output:
    output.write(json.dumps(sys.argv[1:]) + '\n')
if (cache / 'fail-game').exists():
    sys.exit(42)
if '+workshop_download_item' in sys.argv:
    root = cache / 'workshop/steamapps/workshop/content/294100/2009463077'
    if root.exists():
        shutil.rmtree(root)
    shutil.copytree('/game/Mods/Harmony', root)
    (root / 'Languages/Test/marker').write_text('updated complete Harmony directory\n')
    print('Success. Downloaded item 2009463077 to cache')
else:
    print("Success! App '294100' fully installed.")
PY
chmod +x /steam/steamcmd/steamcmd.sh
chown -R 10001:10001 /game /profile /state /steam
FIXTURE

expect_exit 37 docker run --rm "${base[@]}" "${ro[@]}" "$image" run --fixture-exit=37 '--literal=$(touch /profile/evaluated)'
admin <<'CHECK'
python3 - <<'PY'
import json, pathlib
s = json.loads(pathlib.Path('/state/argv.json').read_text())
assert s['argv'] == ['-headless', '-turbotick-server', '-savedatafolder=/profile', '--fixture-exit=37', '--literal=$(touch /profile/evaluated)'], s
assert (s['home'], s['xdg'], s['data'], s['cache'], s['steam_user']) == ('/profile', '/profile/.config', '/state', False, False), s
assert not pathlib.Path('/profile/evaluated').exists()
PY
CHECK
pass 'run argv is literal; fixed HOME/profile/state; no Steam cache/account; game exit code 37'

# Refuse historical or tampered packages before executing the fake game.
admin <<'MUTATE'
python3 - <<'PY'
import pathlib, re
p = pathlib.Path('/game/Mods/TurboTick/About/About.xml')
t = p.read_text(encoding='utf-8')
# Rewrite whatever version the package declares: pinning the literal here meant the mutation became a
# no-op on the next release and the refusal test asserted nothing.
p.write_text(re.sub(r'(<modVersion>)[^<]*(</modVersion>)', r'\g<1>0.0.0-other\g<2>', t, count=1), encoding='utf-8')
PY
MUTATE
expect_exit 1 docker run --rm "${base[@]}" "${ro[@]}" "$image" run --fixture-exit=0
# skip-download must never invoke a SteamCMD, even if one is named in env.
docker run --rm "${base[@]}" "${rw[@]}" -e STEAMCMD=/this-must-never-run "$image" install --skip-download
admin <<'CHECK'
set -euo pipefail
sha256sum -c /state/preserve.sha256
test ! -e /steam/calls.jsonl
printf 'extra old DLL\n' > /game/Mods/TurboTick/1.7/Assemblies/Retired.dll
CHECK
expect_exit 1 docker run --rm "${base[@]}" "${ro[@]}" "$image" run --fixture-exit=0
docker run --rm "${base[@]}" "${rw[@]}" "$image" install --skip-download
pass 'old version and extra payload rejected; skip-download repairs exact Mod without SteamCMD and preserves config/save'

# Default online install preserves an existing complete Harmony directory.
docker run --rm "${base[@]}" "${rw[@]}" -v "${volumes[3]}:/steam" "$image" install --user fixture_account
admin <<'CHECK'
python3 - <<'PY'
import json, pathlib
calls = [json.loads(s) for s in pathlib.Path('/steam/calls.jsonl').read_text().splitlines()]
assert len(calls) == 1 and '+workshop_download_item' not in calls[0], calls
assert calls[0][:2] == ['+@sSteamCmdForcePlatformType', 'linux'], calls
assert pathlib.Path('/game/Mods/Harmony/Languages/Test/marker').read_text().startswith('original')
PY
CHECK
docker run --rm "${base[@]}" "${rw[@]}" -v "${volumes[3]}:/steam" "$image" install --user fixture_account --update-harmony
admin <<'CHECK'
python3 - <<'PY'
import json, pathlib
calls = [json.loads(s) for s in pathlib.Path('/steam/calls.jsonl').read_text().splitlines()]
assert len(calls) == 3 and '+workshop_download_item' in calls[-1], calls
assert pathlib.Path('/game/Mods/Harmony/Languages/Test/marker').read_text().startswith('updated')
PY
sha256sum -c /state/preserve.sha256
rm /profile/Config/ModsConfig.xml
CHECK
docker run --rm "${base[@]}" "${rw[@]}" "$image" install --skip-download
admin <<'CHECK'
python3 - <<'PY'
from pathlib import Path
from xml.etree import ElementTree as ET
root = ET.parse('/profile/Config/ModsConfig.xml').getroot()
assert root.findtext('version') == '1.7.5000'
assert [e.text for e in root.findall('activeMods/li')] == ['ludeon.rimworld', 'brrainz.harmony', 'lumidust.turbotick']
assert Path('/profile/Saves/Keep.rws').read_text() == 'untouched save\n'
PY
CHECK
pass 'fake SteamCMD forces Linux; Harmony preserved by default, complete tree refreshed only explicitly; numeric config version'

# Fail before SteamCMD on credential environment; propagate SteamCMD errors.
expect_exit 1 docker run --rm "${base[@]}" "${rw[@]}" -v "${volumes[3]}:/steam" -e STEAM_PASS=fixture "$image" install --user fixture_account
admin <<'MUTATE'
touch /steam/fail-game
MUTATE
expect_exit 42 docker run --rm "${base[@]}" "${rw[@]}" -v "${volumes[3]}:/steam" "$image" install --user fixture_account
admin <<'MUTATE'
rm /steam/fail-game
MUTATE
pass 'password environment rejected; SteamCMD exit code 42 propagated'

# One direct-exec game owns the deployment lock until exit, and receives SIGTERM.
docker run -d --name "$running" --init --stop-timeout 90 "${base[@]}" "${ro[@]}" "$image" run >/dev/null
ready=0
for ((attempt=0; attempt<50; attempt++)); do
  if docker exec "$running" python3 /opt/turbotick/container.py health /state/headless-status.json >/dev/null 2>&1; then ready=1; break; fi
  sleep .2
done
[[ "$ready" -eq 1 ]] || { docker logs "$running"; echo 'FAIL: fake game did not become healthy' >&2; exit 1; }
expect_exit 1 docker run --rm "${base[@]}" "${ro[@]}" "$image" run --fixture-exit=0
expect_exit 1 docker run --rm "${base[@]}" "${rw[@]}" "$image" install --skip-download
docker exec "$running" python3 /opt/turbotick/container.py health /state/headless-status.json
docker stop --time 90 "$running" >/dev/null
[[ "$(docker inspect --format '{{.State.ExitCode}}' "$running")" -eq 23 ]]
admin <<'CHECK'
test "$(cat /state/signal)" = SIGTERM
CHECK
docker rm "$running" >/dev/null
pass 'run/run and install/run locks exclude each other; fresh healthy status; SIGTERM reaches game and exit code 23 preserved'

# With no live writer, mutate only disposable state to cover health failures.
health_run() { docker run --rm "${base[@]}" "${ro[@]}" --entrypoint python3 "$image" /opt/turbotick/container.py health /state/headless-status.json; }
for kind in disconnected faulted stale future missing; do
  admin <<FIXTURE
python3 - <<'PY'
import datetime, json, pathlib
kind = '$kind'
path = pathlib.Path('/state/headless-status.json')
status = {'utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'connected': True, 'state': 'lobby', 'failure': None}
if kind == 'disconnected': status['connected'] = False
if kind == 'faulted': status['state'] = 'faulted'; status['failure'] = 'fixture fault'
if kind == 'stale': status['utc'] = (datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(seconds=60)).isoformat()
if kind == 'future': status['utc'] = (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(seconds=60)).isoformat()
path.write_text(json.dumps(status))
if kind == 'missing': path.unlink()
PY
FIXTURE
  expect_exit 1 health_run
done
pass 'health rejects disconnected, faulted, stale, future and missing status'

# Missing Harmony and host mount permissions fail loudly, without SteamCMD.
admin <<'MUTATE'
mv /game/Mods/Harmony /game/Harmony-fixture-disabled
chmod 0555 /profile
MUTATE
expect_exit 1 docker run --rm "${base[@]}" "${rw[@]}" "$image" install --skip-download
admin <<'MUTATE'
chmod 0755 /profile
MUTATE
expect_exit 1 docker run --rm "${base[@]}" "${rw[@]}" "$image" install --skip-download
pass 'unwritable mount and missing Harmony fail fast'
printf '\nAll offline container contract tests passed. No real SteamCMD, game, Harmony, or network was used.\n'
