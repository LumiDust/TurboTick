#!/usr/bin/env bash
set -euo pipefail
readonly ROOT=/opt/turbotick GAME=/game PROFILE_DIR=/profile STATE=/state
log() { printf '[container] %s\n' "$*"; }
die() { printf '[container] error: %s\n' "$*" >&2; exit 1; }
helper() { python3 "$ROOT/container.py" "$@"; }
value() { [[ $# -ge 2 && -n "$2" ]] || die "option $1 requires a value"; }
usage() {
  cat <<'EOF'
Usage: entrypoint.sh [run [GAME_ARG ...] | install [OPTIONS] | check-update | watch-updates [SECONDS]]
  run                       Offline preflight, then exec /game/RimWorld.
  install --user ACCOUNT    Install/update game with native SteamCMD interaction.
  install --skip-download   Use existing game + Harmony; never access /steam.
  check-update              Ask Steam for app 294100's build id; write /state/game-update.json.
  watch-updates [SECONDS]   check-update in a loop (default TURBOTICK_UPDATE_CHECK_SECONDS or 3600).
Install options:
  --user ACCOUNT           Or STEAM_USER; otherwise read account on stdin.
  --branch NAME            Or TURBOTICK_GAME_BRANCH (default unstable).
  --expected-version V     Or TURBOTICK_EXPECTED_GAME_VERSION (default 1.7).
  --update-harmony         Re-download official workshop item 2009463077.
  --no-validate            Omit game SteamCMD validate, keep local checks.
Passwords and Guard codes are accepted only by native SteamCMD prompts.
Mounts: /game, /profile, /state; /steam is needed only for online install.
EOF
}
mode="${1:-run}"
[[ $# -eq 0 ]] || shift
case "$mode" in --help|-h|help) usage; exit 0 ;; run|install|check-update|watch-updates|portal|game) ;; *) die "unknown stage '$mode'; use install, run, portal, game, check-update or watch-updates" ;; esac
# The container can be the supervisor of the game (portal mode) or the launcher (inline). The flag only
# decides which process owns the child; nothing else in this script changes.
if [[ "$mode" == run && "${TURBOTICK_SUPERVISOR:-inline}" == portal ]]; then mode=portal; fi
export HOME="$PROFILE_DIR" XDG_CONFIG_HOME="$PROFILE_DIR/.config" TURBOTICK_DATA="$STATE"

# SteamCMD bootstrap: the image carries an immutable seed, the /steam volume carries the copy that may
# self-update. install and check-update both need it, and neither may touch the deployment lock.
prepare_steamcmd() {
  mkdir -p /steam/home /steam/home/.config /steam/steamcmd
  if [[ ! -f /steam/steamcmd/steamcmd.sh ]]; then
    cp -a /opt/steamcmd-seed/. /steam/steamcmd/
    chmod -R u+w /steam/steamcmd
    chmod u+x /steam/steamcmd/steamcmd.sh /steam/steamcmd/linux32/steamcmd
  fi
}

# Runs without the deployment lock: a running server must still be able to learn that Steam has a new build.
check_update() {
  mkdir -p "$STATE"
  prepare_steamcmd
  local transcript code=0
  transcript="$(mktemp)"
  # Bounded on purpose: with no route to Steam the client sits in TCP retries for minutes, and a check that
  # never returns leaves no status file at all - which reads exactly like "nothing to report".
  timeout "${TURBOTICK_UPDATE_TIMEOUT_SECONDS:-120}" \
    env HOME=/steam/home XDG_CONFIG_HOME=/steam/home/.config "$ROOT/steamcmd.sh" \
      +login anonymous +app_info_update 1 +app_info_print 294100 +quit >"$transcript" 2>&1 || code=$?
  if [[ "$code" -eq 124 ]]; then
    rm -f -- "$transcript"
    helper update-failed "$STATE" "SteamCMD did not answer within ${TURBOTICK_UPDATE_TIMEOUT_SECONDS:-120}s" "$GAME"
    log 'SteamCMD timed out; recorded as unavailable in /state/game-update.json'
    return 1
  fi
  if [[ "$code" -ne 0 ]]; then
    rm -f -- "$transcript"
    helper update-failed "$STATE" "SteamCMD app_info_print exited $code" "$GAME"
    log 'SteamCMD app_info_print failed; recorded as unavailable in /state/game-update.json'
    return 1
  fi
  if grep -Eiq 'ERROR!|Login Failure|Invalid Password|Account Logon Denied|Failed to load' "$transcript"; then
    rm -f -- "$transcript"
    helper update-failed "$STATE" 'SteamCMD reported an error while reading app 294100' "$GAME"
    log 'SteamCMD reported an error while reading app 294100; recorded as unavailable'
    return 1
  fi
  helper update-status "$GAME" "$transcript" "$STATE" "${TURBOTICK_GAME_BRANCH:-unstable}"
  rm -f -- "$transcript"
}

# Both `run` (inline) and `game` (child of the portal) end here: the preflight decides, then the game owns the
# deployment lock and the process. A world swap is a process swap, so only the parent differs between shapes.
start_game() {
  helper writable "$PROFILE_DIR" "$STATE"
  # FD 9 survives exec, so the actual game owns this lock for its entire lifetime.
  exec 9>"$STATE/deployment.lock"
  flock -n 9 || die 'deployment.lock is held by another install/run; stop that instance before changing game/profile/state'
  mkdir -p "$XDG_CONFIG_HOME"
  for arg in "$@"; do
    case "${arg,,}" in
      -savedatafolder*|-turbotick-data*) die 'profile/state paths are fixed at /profile and /state' ;;
    esac
  done
  helper preflight "$GAME" "$PROFILE_DIR" "$ROOT/payload.sha256.json"
  # A previous process's status must not make the next process appear healthy.
  rm -f "$STATE/headless-status.json"
  cd "$GAME"
  export LD_LIBRARY_PATH="$GAME"
  log 'starting native RimWorld; SIGTERM is delivered directly to the game (allow 90 seconds to checkpoint)'
  exec "$GAME/RimWorld" -headless -turbotick-server "-savedatafolder=$PROFILE_DIR" "$@"
}

if [[ "$mode" == portal ]]; then
  [[ -n "${TURBOTICK_ADMIN_PASSWORD:-}${TURBOTICK_ADMIN_PASSWORD_FILE:-}" ]] \
    || die 'portal mode needs TURBOTICK_ADMIN_PASSWORD or TURBOTICK_ADMIN_PASSWORD_FILE'
  portal="${TURBOTICK_PORTAL_EXE:-}"
  if [[ -z "$portal" ]]; then
    # Two shapes exist: the image bakes it beside the entrypoint, a mounted deployment drops it under portal/.
    if [[ -x /opt/turbotick/TurboTick.Portal ]]; then portal=/opt/turbotick/TurboTick.Portal
    else portal=/opt/turbotick/portal/TurboTick.Portal; fi
  fi
  [[ -x "$portal" ]] || die "portal mode needs an executable management page at $portal (set TURBOTICK_PORTAL_EXE or ship TurboTick.Portal)"
  # The game this portal starts must leave the admin port to the portal.
  exec env TURBOTICK_ADMIN_INPROCESS=off "$portal" \
    --port "${TURBOTICK_ADMIN_PORT:-30573}" \
    --bind "${TURBOTICK_ADMIN_BIND:-0.0.0.0}" \
    --data "$STATE" \
    --game "$GAME" \
    --saves "$PROFILE_DIR/Saves" \
    --mods "$GAME/Mods" \
    --config "$PROFILE_DIR/Config/ModsConfig.xml" \
    --backups "${TURBOTICK_BACKUP_DIR:-$STATE/Backups}" \
    --checkpoint-minutes "${TURBOTICK_CHECKPOINT_MINUTES:-10}" \
    --steam-root /steam \
    --steamcmd "$ROOT/steamcmd.sh" \
    --game-exe "$0" \
    --game-arg game
fi
if [[ "$mode" == game ]]; then
  start_game "$@"
fi

if [[ "$mode" == check-update ]]; then
  check_update
  exit 0
fi
if [[ "$mode" == watch-updates ]]; then
  interval="${1:-${TURBOTICK_UPDATE_CHECK_SECONDS:-3600}}"
  [[ "$interval" =~ ^[0-9]+$ ]] && (( interval >= 60 )) || die 'watch-updates interval must be a whole number of seconds, at least 60'
  log "watching Steam app 294100 for new builds every ${interval}s; status is /state/game-update.json"
  while :; do
    check_update || log 'update check failed; the previous /state/game-update.json is kept'
    sleep "$interval"
  done
fi

helper writable "$PROFILE_DIR" "$STATE"
# FD 9 survives exec, so the actual game owns this lock for its entire lifetime.
# flock --close or a background supervisor would release it too early.
exec 9>"$STATE/deployment.lock"
flock -n 9 || die 'deployment.lock is held by another install/run; stop that instance before changing game/profile/state'
mkdir -p "$XDG_CONFIG_HOME"
if [[ "$mode" == run ]]; then
  start_game "$@"
fi

skip=0 update_harmony=0 validate=1
account="${STEAM_USER:-}"
branch="${TURBOTICK_GAME_BRANCH:-unstable}"
expected="${TURBOTICK_EXPECTED_GAME_VERSION:-1.7}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --user) value "$@"; account="$2"; shift 2 ;;
    --branch) value "$@"; branch="$2"; shift 2 ;;
    --expected-version) value "$@"; expected="$2"; shift 2 ;;
    --skip-download) skip=1; shift ;;
    --update-harmony) update_harmony=1; shift ;;
    --no-validate) validate=0; shift ;;
    --help|-h) usage; exit 0 ;;
    *) die "unknown install option: $1" ;;
  esac
done
[[ "$skip" -eq 0 || "$update_harmony" -eq 0 ]] || die '--skip-download and --update-harmony conflict'
[[ -z "${STEAM_PASS:-}${STEAM_GUARD:-}" ]] || die 'unset STEAM_PASS and STEAM_GUARD; use native SteamCMD password/Guard prompts'
export TURBOTICK_EXPECTED_GAME_VERSION="$expected"
helper writable "$GAME"
helper verify "$ROOT/mod/TurboTick" "$ROOT/payload.sha256.json"
if [[ "$skip" -eq 0 ]]; then
  helper writable /steam
  mkdir -p /steam/home /steam/home/.config /steam/steamcmd /steam/workshop
  if [[ ! -f /steam/steamcmd/steamcmd.sh ]]; then
    cp -a /opt/steamcmd-seed/. /steam/steamcmd/
    chmod -R u+w /steam/steamcmd
    chmod u+x /steam/steamcmd/steamcmd.sh /steam/steamcmd/linux32/steamcmd
  fi
  if [[ -z "$account" ]]; then
    [[ -t 0 ]] || die 'Steam account missing; use an interactive terminal or --user ACCOUNT / STEAM_USER'
    read -r -p 'Steam account (password/Guard are prompted by SteamCMD): ' account
  fi
  [[ "$account" =~ ^[a-zA-Z0-9_@.-]+$ ]] || die 'invalid Steam account name'
fi
install_args=(--profile client-headless --dir "$GAME" --executable RimWorld --branch "$branch" --expected-version "$expected" --launch-arg -headless --launch-arg -turbotick-server)
if [[ "$skip" -eq 1 ]]; then
  install_args+=(--skip-download)
else
  install_args+=(--steamcmd "$ROOT/steamcmd.sh" --user "$account")
fi
[[ "$validate" -eq 1 ]] || install_args+=(--no-validate)
# Remove inherited standalone-installer options; only this stage's literal argv
# controls installation. Authentication HOME/cache exists only in this process.
env -u PROFILE -u APP_ID -u BRANCH -u LAUNCH_ARGS -u INSTALL_DIR -u MOD_SOURCE -u MODS_CONFIG -u STEAM_PASS -u STEAM_GUARD \
  HOME=/steam/home XDG_CONFIG_HOME=/steam/home/.config \
  bash "$ROOT/install-server.sh" "${install_args[@]}"

if [[ "$update_harmony" -eq 1 ]]; then
  existing_harmony="$(helper harmony-target "$GAME")"
else
  existing_harmony="$(helper harmony "$GAME")"
fi
if [[ -n "$existing_harmony" && "$update_harmony" -eq 0 ]]; then
  log 'preserving installed Harmony (use --update-harmony explicitly to refresh it)'
elif [[ "$skip" -eq 1 ]]; then
  die '--skip-download requires an existing valid Harmony 1.7 Mod; no SteamCMD was invoked'
else
  transcript="$(mktemp)"
  trap 'rm -f -- "$transcript"' EXIT
  log 'downloading official Harmony workshop 2009463077 into the Steam cache'
  # Remove only this cached item so an old copy at the other historical cache
  # location can never be mistaken for the just-validated download.
  rm -rf /steam/workshop/steamapps/workshop/content/294100/2009463077 /steam/steamcmd/steamapps/workshop/content/294100/2009463077
  set +e
  HOME=/steam/home XDG_CONFIG_HOME=/steam/home/.config "$ROOT/steamcmd.sh" \
    +force_install_dir /steam/workshop +login "$account" \
    +workshop_download_item 294100 2009463077 validate +quit 2>&1 | tee "$transcript"
  results=("${PIPESTATUS[@]}")
  set -e
  [[ "${results[0]}" -eq 0 ]] || { log "SteamCMD workshop exited with ${results[0]}"; exit "${results[0]}"; }
  [[ "${results[1]}" -eq 0 ]] || die 'SteamCMD workshop output capture failed'
  if grep -Eiq 'ERROR!|FAILED \(|Login Failure|Invalid Password|Account Logon Denied' "$transcript"; then die 'SteamCMD reported a workshop/authentication error'; fi
  grep -Eiq "Success\. Downloaded item 2009463077|Success!.*2009463077" "$transcript" || die 'SteamCMD did not confirm Harmony workshop download success'
  # Valve has used both force_install_dir and the SteamCMD root for workshop
  # storage. Accept only the specific official item under this mounted cache.
  harmony_source=''
  for candidate in /steam/workshop/steamapps/workshop/content/294100/2009463077 /steam/steamcmd/steamapps/workshop/content/294100/2009463077; do
    if [[ -f "$candidate/About/About.xml" ]]; then harmony_source="$candidate"; break; fi
  done
  [[ -n "$harmony_source" ]] || die 'Harmony download succeeded but its complete Mod directory is missing from the Steam cache'
  helper install-harmony "$harmony_source" "${existing_harmony:-$GAME/Mods/Harmony}"
fi
helper install-mod "$ROOT/mod/TurboTick" "$GAME/Mods/TurboTick" "$ROOT/payload.sha256.json"
helper profile "$GAME" "$PROFILE_DIR"
helper preflight "$GAME" "$PROFILE_DIR" "$ROOT/payload.sha256.json"
log 'install complete; existing profile configs and saves retained; no game process started'
