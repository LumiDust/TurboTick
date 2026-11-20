#!/bin/bash
# 端到端验证「未下载的真实 mod 会被匿名下载」
# 前提：/data/rimworld/srv/game/steamapps/workshop/content/294100/3822832387 已被删除
set -uo pipefail

P=$(cat /data/rimworld/admin-password.txt)
BASE=http://127.0.0.1:30573
CJ=$(mktemp); trap 'rm -f "$CJ"' EXIT

echo "=== 登录 ==="
curl -s -o /dev/null -c "$CJ" -X POST -d "password=$P" "$BASE/login"

WS=3822832387
PKG=mrk.researchcommission

echo "=== 构造一个需要 $PKG ($WS) 的存档 ==="
# 用库里已有的真实存档做母本，改 meta 加一项
SRC=$(ls -S /data/rimworld/srv/saves/*.rws 2>/dev/null | head -1)
if [ -z "$SRC" ]; then
    echo "ERROR: saves 里没有存档做母本" >&2
    exit 1
fi
TMP=/tmp/wsdl-test.rws
python3 - "$SRC" "$TMP" "$PKG" "$WS" <<'PY'
import sys, re
src, dst, pkg, ws = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
raw = open(src, 'rb').read()
# 在 </meta> 前插入一条（三张表都要加，按索引对齐）
def add(block, tag, value):
    global raw
    pat = '<%s>(.*?)</%s>' % (tag, tag)
    m = re.search(pat.encode(), raw, re.S)
    if not m:
        raise SystemExit('缺少 <%s>' % tag)
    seg = m.group(0)
    new = seg.replace(b'</' + tag.encode() + b'>',
                      b'\t\t\t<li>' + value.encode() + b'</li>\n\t\t</' + tag.encode() + b'>')
    raw = raw.replace(seg, new, 1)
add(raw, 'modIds', pkg)
add(raw, 'modSteamIds', ws)
add(raw, 'modNames', 'Research Commission')
open(dst, 'wb').write(raw)
print('已生成', dst, len(raw), 'bytes')
PY

echo "=== 上传 ==="
UP=$(curl -s -b "$CJ" -H 'X-TurboTick-Csrf: 1' \
     -H 'Content-Type: application/octet-stream' --data-binary "@$TMP" \
     "$BASE/api/saves/upload")
echo "  $UP" | head -c 300; echo
NAME=$(echo "$UP" | python3 -c "import json,sys;print(json.load(sys.stdin).get('name',''))")
echo "  存档名=$NAME"

echo "=== dry-run（应该是 Downloadable）==="
curl -s -b "$CJ" "$BASE/api/saves/mods?save=$NAME" | python3 -c "
import json,sys
d=json.load(sys.stdin)
for m in d.get('mods',[]):
    if m.get('packageId')=='$PKG':
        print('  disposition=',m.get('disposition'),'workshopId=',m.get('workshopId'))
" 2>&1 | head -3

echo "=== apply（应真的匿名下载）==="
T0=$(date +%s)
R=$(curl -s -b "$CJ" -H 'X-TurboTick-Csrf: 1' -H 'Content-Type: application/json' \
    -d "{\"save\":\"$NAME\"}" "$BASE/api/saves/mods/apply")
T1=$(date +%s)
echo "  耗时 $((T1-T0))s"
echo "  $R" | head -c 500; echo

echo "=== 缓存是否恢复 ==="
if [ -d "/data/rimworld/srv/game/steamapps/workshop/content/294100/$WS" ]; then
    echo "  ✅ 已下载 $(du -sh /data/rimworld/srv/game/steamapps/workshop/content/294100/$WS | cut -f1)"
else
    echo "  ❌ 未下载"
fi

echo "=== mods 目录是否已链入 ==="
docker exec turbotick-server sh -c "ls -ld /data/game/Mods/$PKG 2>/dev/null || echo '  未链入'"
