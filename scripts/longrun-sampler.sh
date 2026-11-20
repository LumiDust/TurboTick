#!/usr/bin/env bash
# Long-run sampler: 5 replicas + authority. Appends, never truncates.
# `sed` delimiter is `|` because several patterns contain `/` (e.g. "次/失败 3 次"); using the default
# delimiter silently produced empty columns in an earlier version.
L=/data/rimworld/lab
OUT="$L/logs/$OUTFILE"
# 权威日志不能用固定名：那一轮结束后 grep 会一直读到同一份旧数据，于是 tick 卡死、看起来像"权威停摆"，
# 实际是采样器在读尸体。实测踩过：long2 在 15:11:54 退出，之后 4 次采样的 tick 全是 321602。
SERVERLOG="${SERVERLOG:-server-out-long2.log}"
pick() { printf '%s' "$2" | sed -n "s|.*$1.*|\1|p" | head -1; }

echo "=== start $(date +%H:%M:%S) label=$LABEL samples=$SAMPLES gap=$GAP ===" >> "$OUT"
for i in $(seq 1 "$SAMPLES"); do
  printf -- '--- sample %s at %s ---\n' "$i" "$(date +%H:%M:%S)" >> "$OUT"
  for r in 1 2 3 4 5; do
    raw=$(grep -a '引擎tick' "$L/logs/client$r-out-$LABEL.log" 2>/dev/null | tail -1 | tr -d '\000' | sed 's/\x1b\[[0-9;]*m//g')
    if [ -z "$raw" ]; then echo "client$r: (无帧日志)" >> "$OUT"; continue; fi
    et=$(pick '引擎tick=\([0-9]*\)' "$raw")
    ft=$(pick '帧tick=\([0-9]*\)' "$raw")
    wk=$(pick '工作类 \([0-9]*\)' "$raw")
    idle=$(pick '空闲类 \([0-9]*\)' "$raw")
    tok=$(pick 'toil 一致 \([0-9]*\)' "$raw")
    beh=$(pick '落后 \([0-9]*\)' "$raw")
    push=$(pick 'toil 推送 \([0-9]*\)' "$raw")
    clamp=$(pick '次/截断 \([0-9]*\) 次' "$raw")
    sfail=$(pick '起步失败 \([0-9]*\) 次' "$raw")
    js=$(pick '工作同步 \([0-9]*\) 次' "$raw")
    bill=$(pick '配方接线 \([0-9]*\) 次' "$raw")
    q=$(pick '队列目标 \([0-9]*\) 个' "$raw")
    drop=$(pick '超预算丢弃 \([0-9]*\)' "$raw")
    scribe=$(pick '失败 \([0-9]*\)，失去地图' "$raw")
    idf=$(pick '自检失败 \([0-9]*\)' "$raw")
    drift=$(pick '最差 \([0-9.]*\) 格' "$raw")
    frames=$(pick '累计 \([0-9]*\) 帧' "$raw")
    echo "client$r: etick=$et ftick=$ft work=$wk idle=$idle toil_ok=$tok behind=$beh push=$push clamp=$clamp startfail=$sfail jobsync=$js bill=$bill q=$q drop=$drop scribe=$scribe idfail=$idf drift=$drift frames=$frames" >> "$OUT"
  done
  ar=$(grep -a '权威帧' "$L/logs/$SERVERLOG" 2>/dev/null | tail -1 | tr -d '\000' | sed 's/\x1b\[[0-9;]*m//g')
  echo "authority: tick=$(pick '权威帧 tick=\([0-9]*\)' "$ar") pawns=$(pick '：\([0-9]*\) 个角色' "$ar") kb=$(pick '一帧约 \([0-9.]*\) KB' "$ar")" >> "$OUT"
  echo "rss: $(ps -eo rss,args | grep '[l]ab/game/RimWorld' | awk '{s+=$1; n++} END{printf "procs=%d total_mb=%d avg_mb=%d", n, s/1024, s/1024/n}')" >> "$OUT"
  echo >> "$OUT"
  [ "$i" -lt "$SAMPLES" ] && sleep "$GAP"
done
echo "=== done $(date +%H:%M:%S) ===" >> "$OUT"
