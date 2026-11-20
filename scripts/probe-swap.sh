#!/usr/bin/env bash
# Proves (or disproves) the portal's whole reason to exist: a world swap must not take the page down.
#
# What it measures, not asserts:
#   - every HTTP status the page returns while a swap is happening, sampled 2x/second
#   - whether the login cookie still works afterwards (a session store that lives in the game process would
#     lose it, and the operator would be bounced to the login page in the middle of an operation)
#   - the longest run of failed samples, which is the real "how long was the page unusable" number
#
# Usage: probe-swap.sh <cookie-jar> <save-name> [port]
set -uo pipefail

JAR="${1:?cookie jar}"
SAVE="${2:?save name}"
PORT="${3:-30573}"
BASE="http://127.0.0.1:$PORT"
OUT=/tmp/swap-probe.txt
: >"$OUT"

probe() {
  # %{http_code} only: the body is 51 KB and irrelevant to availability.
  curl -s -o /dev/null -m 3 -b "$JAR" -w '%{http_code}' "$BASE/api/status" 2>/dev/null || echo "000"
}

echo "[probe] 采样开始（基线 4 秒）"
for _ in 1 2 3 4 5 6 7 8; do
  printf '%s %s\n' "$(date +%H:%M:%S.%3N)" "$(probe)" >>"$OUT"
  sleep 0.5
done

echo "[probe] 触发换档 → $SAVE"
curl -s -m 180 -b "$JAR" -H 'X-TurboTick-Csrf: 1' -H 'Content-Type: application/json' \
  -X POST --data "{\"save\":\"$SAVE\"}" "$BASE/api/server/start" >/tmp/swap-trigger.json 2>&1
echo "[probe] 触发返回：$(head -c 300 /tmp/swap-trigger.json)"

echo "[probe] 换档窗口采样 120 秒"
for _ in $(seq 1 240); do
  printf '%s %s\n' "$(date +%H:%M:%S.%3N)" "$(probe)" >>"$OUT"
  sleep 0.5
done

echo "[probe] 会话是否还在：$(curl -s -o /dev/null -m 5 -b "$JAR" -w '%{http_code}' "$BASE/api/status")（200 = 登录未被换档打断）"
echo "[probe] 最终状态："
curl -s -m 5 -b "$JAR" "$BASE/api/status" | tr ',' '\n' | grep -E '"(state|tick|save|gameTick|joinedPeers)"' | tr -d ' '
