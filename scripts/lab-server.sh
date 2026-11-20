#!/usr/bin/env bash
# TurboTick 五副本实验室：在 .29 上搭一套独立游戏树 + 自己的 Mods，跑 1 权威 + N 副本。
#
# 为什么不用符号链接拼树：RimWorld 用 `readlink -f` 解析自己的可执行文件，`GenFilePaths.ExecutableDir`
# 拿到的是**真实路径**，于是符号链接树会悄悄加载生产目录下的 `Mods/`（那是空的）。症状是 `TurboTick`
# 一个 patch 都不加载、状态文件根本不生成，而日志里没有任何报错。用 `cp -al` 硬链接复制（1.6 GB 的树
# 只多占几 MB inode），只把 `Mods/` 换成自己的。
#
# 另一个坑：首次启动会用 Core+DLC 默认值**重写** `ModsConfig.xml`，所以配置必须在第一次启动之后再写一遍。
#
# 用法：
#   lab-server.sh setup             搭树 + 传 mod（MOD_TGZ 指向本地 tar.gz）
#   lab-server.sh up [N]            起权威 + N 个副本（默认 5），等 playing
#   lab-server.sh down              停掉实验室的所有进程
#   lab-server.sh status            打印每个副本的关键读数
#   lab-server.sh sample FILE N GAP 采样 N 次、间隔 GAP 秒，写进 FILE（追加）
set -uo pipefail

L=${LAB:-/data/rimworld/lab}
PROD=${PROD:-/data/rimworld/srv}
PORT=${PORT:-30640}
MOD_TGZ=${MOD_TGZ:-/tmp/tt-mod.tgz}

mods_config() {
  cat <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<ModsConfigData>
  <version>1.7.5005 rev1082</version>
  <activeMods>
    <li>ludeon.rimworld</li>
    <li>ludeon.rimworld.royalty</li>
    <li>ludeon.rimworld.ideology</li>
    <li>ludeon.rimworld.biotech</li>
    <li>ludeon.rimworld.anomaly</li>
    <li>ludeon.rimworld.odyssey</li>
    <li>brrainz.harmony</li>
    <li>lumidust.turbotick</li>
  </activeMods>
  <knownExpansions>
    <li>ludeon.rimworld.royalty</li>
    <li>ludeon.rimworld.ideology</li>
    <li>ludeon.rimworld.biotech</li>
    <li>ludeon.rimworld.anomaly</li>
    <li>ludeon.rimworld.odyssey</li>
  </knownExpansions>
</ModsConfigData>
XML
}

case "${1:-}" in
setup)
  mkdir -p "$L/logs" "$L/state" "$L/profiles"
  rm -rf "$L/game"
  cp -al "$PROD/game" "$L/game"
  rm -rf "$L/game/Mods"; mkdir -p "$L/game/Mods"
  ln -s "$PROD/mods/Harmony" "$L/game/Mods/Harmony"
  mkdir -p "$L/mods/TurboTick"
  tar xzf "$MOD_TGZ" -C "$L/mods/TurboTick"
  ln -sfn "$L/mods/TurboTick" "$L/game/Mods/TurboTick"
  echo "tree ready: $(readlink -f "$L/game/RimWorld")"
  ;;

up)
  N=${2:-5}
  mkdir -p "$L/profiles/server/Config"
  mods_config > "$L/profiles/server/Config/ModsConfig.xml"
  pkill -f "$L/game/RimWorld" 2>/dev/null || true
  sleep 4
  rm -f "$L/state/headless-status.json"
  setsid "$L/game/RimWorld" -headless -turbotick-server -quicktest \
    -savedatafolder="$L/profiles/server" -turbotick-data="$L/state" \
    -turbotick-port="$PORT" -turbotick-admin-port=0 \
    > "$L/logs/server-out-lab.log" 2> "$L/logs/server-err-lab.log" &
  for _ in $(seq 1 180); do
    grep -q 'playing' "$L/state/headless-status.json" 2>/dev/null && break
    sleep 1
  done
  echo "authority: $(grep -o '"state": *"[a-z]*"' "$L/state/headless-status.json" 2>/dev/null | head -1)"
  for r in $(seq 1 "$N"); do
    mkdir -p "$L/profiles/client$r/Config"
    mods_config > "$L/profiles/client$r/Config/ModsConfig.xml"
    setsid "$L/game/RimWorld" -headless -quicktest -turbotick-connect="127.0.0.1:$PORT" \
      -savedatafolder="$L/profiles/client$r" \
      > "$L/logs/client$r-out-lab.log" 2> "$L/logs/client$r-err-lab.log" &
    sleep 2
  done
  echo "started $N replicas"
  ;;

down)
  pkill -f "$L/game/RimWorld" 2>/dev/null || true
  sleep 3
  echo "stopped: $(pgrep -fc "$L/game/RimWorld" || echo 0) remaining"
  ;;

status)
  for r in $(seq 1 "${2:-5}"); do
    raw=$(grep -a '引擎tick' "$L/logs/client$r-out-lab.log" 2>/dev/null | tail -1 | tr -d '\000' | sed 's/\x1b\[[0-9;]*m//g')
    [ -z "$raw" ] && { echo "client$r: (无帧日志)"; continue; }
    echo "client$r: $(printf '%s' "$raw" | grep -o '引擎tick=[0-9]* 帧tick=[0-9]*') | $(printf '%s' "$raw" | grep -o 'toil 一致 [0-9]*/落后 [0-9]*（其中本端没有driver [0-9]*）/超前 [0-9]*/未知 [0-9]*') | $(printf '%s' "$raw" | grep -o 'toil 推送 [0-9]* 次/失败 [0-9]* 次/截断 [0-9]* 次/起步失败 [0-9]* 次')"
  done
  ;;

sample)
  OUT=${2:-$L/logs/samples.txt} N=${3:-12} GAP=${4:-600}
  pick() { printf '%s' "$2" | sed -n "s|.*$1.*|\1|p" | head -1; }
  echo "=== start $(date +%H:%M:%S) samples=$N gap=$GAP ===" >> "$OUT"
  for i in $(seq 1 "$N"); do
    echo "--- sample $i at $(date +%H:%M:%S) ---" >> "$OUT"
    for r in 1 2 3 4 5; do
      raw=$(grep -a '引擎tick' "$L/logs/client$r-out-lab.log" 2>/dev/null | tail -1 | tr -d '\000' | sed 's/\x1b\[[0-9;]*m//g')
      [ -z "$raw" ] && { echo "client$r: (无帧日志)" >> "$OUT"; continue; }
      echo "client$r: etick=$(pick '引擎tick=\([0-9]*\)' "$raw") ftick=$(pick '帧tick=\([0-9]*\)' "$raw") toil_ok=$(pick 'toil 一致 \([0-9]*\)' "$raw") behind=$(pick '落后 \([0-9]*\)' "$raw") clamp=$(pick '次/截断 \([0-9]*\) 次' "$raw") startfail=$(pick '起步失败 \([0-9]*\) 次' "$raw") drop=$(pick '超预算丢弃 \([0-9]*\)' "$raw") scribe=$(pick '失败 \([0-9]*\)，失去地图' "$raw") idfail=$(pick '自检失败 \([0-9]*\)' "$raw") drift=$(pick '最差 \([0-9.]*\) 格' "$raw") frames=$(pick '累计 \([0-9]*\) 帧' "$raw")" >> "$OUT"
    done
    echo "rss: $(ps -eo rss,args | grep "[l]ab/game/RimWorld" | awk '{s+=$1; n++} END{printf "procs=%d total_mb=%d", n, s/1024}')" >> "$OUT"
    echo >> "$OUT"
    [ "$i" -lt "$N" ] && sleep "$GAP"
  done
  ;;

*)
  sed -n '2,25p' "$0"
  exit 2
  ;;
esac
