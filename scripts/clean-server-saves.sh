#!/bin/bash
# 清空服务端的测试存档与备份（走 API，验证接口同时清理）
set -u
P=$(cat /data/rimworld/admin-password.txt)
CJ=$(mktemp)
trap 'rm -f "$CJ"' EXIT

code=$(curl -s -o /dev/null -w '%{http_code}' -c "$CJ" -X POST -d "password=$P" http://127.0.0.1:30573/login)
echo "login=$code"

echo "=== 现有存档 ==="
ls -la /data/rimworld/srv/saves/*.rws 2>/dev/null | awk '{print $5, $9}'

echo "=== 逐个删除 ==="
for f in /data/rimworld/srv/saves/*.rws; do
    [ -e "$f" ] || continue
    n=$(basename "$f" .rws)
    r=$(curl -s -b "$CJ" -H 'X-TurboTick-Csrf: 1' -X POST \
        -H 'Content-Type: application/json' \
        -d "{\"name\":\"$n\"}" \
        http://127.0.0.1:30573/api/saves/delete)
    echo "  $n -> $r"
done

echo "=== 删除后剩余 ==="
ls /data/rimworld/srv/saves/*.rws 2>/dev/null | wc -l

echo "=== 清空 backups ==="
rm -rf /data/rimworld/srv/backups/*
ls /data/rimworld/srv/backups 2>/dev/null | wc -l

echo "=== 服务端状态 ==="
python3 -c "import json;d=json.load(open('/data/rimworld/srv/state/headless-status.json'));print('state=',d.get('state'),'failure=',str(d.get('failure'))[:60])" 2>/dev/null
