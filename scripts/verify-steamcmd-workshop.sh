#!/bin/bash
# steamcmd workshop 下载能力：验证 + 配置落位
# 在 .29 上运行。
set -uo pipefail

CT=turbotick-server
PROXY="${1:-http://192.168.0.30:7897}"
WID=2009463077   # Harmony，仅用于验证

echo "=== 1. 镜像内 steamcmd 版本 ==="
docker exec -w /opt/steamcmd "$CT" sh -c './steamcmd.sh +quit 2>&1 | grep -i "Steam Console Client" | head -1'

echo "=== 2. 匿名登录（经代理）==="
docker exec -w /opt/steamcmd -e HTTP_PROXY="$PROXY" -e HTTPS_PROXY="$PROXY" "$CT" \
    sh -c 'timeout 180 ./steamcmd.sh +login anonymous +quit 2>&1 | grep -E "Connecting anonymously|OK$" | head -3'

echo "=== 3. 下载落点：默认会写到 \$HOME/.local/share/Steam ==="
docker exec "$CT" sh -c 'echo "HOME=$HOME"; ls -d "$HOME/.local/share/Steam" 2>/dev/null && du -sh "$HOME/.local/share/Steam"'

echo "=== 4. 用 +force_install_dir 把工坊内容固定到 /data/mods-workshop ==="
docker exec -w /opt/steamcmd -e HTTP_PROXY="$PROXY" -e HTTPS_PROXY="$PROXY" "$CT" sh -c "
    mkdir -p /data/mods-workshop
    timeout 300 ./steamcmd.sh \
        +force_install_dir /data/mods-workshop \
        +login anonymous \
        +workshop_download_item 294100 $WID validate \
        +quit 2>&1 | grep -E 'Downloading item|Success|ERROR' | head -4
"

echo "=== 5. 结果 ==="
docker exec "$CT" sh -c "du -sh /data/mods-workshop 2>/dev/null; find /data/mods-workshop -maxdepth 4 -name About -type d 2>/dev/null | head -3"
