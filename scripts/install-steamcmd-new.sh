#!/bin/bash
# 把新版 steamcmd 装到挂载卷（而不是镜像层），这样：
#   - 不需要重建镜像（.29 无外网，apt-get 装依赖会失败）
#   - 容器重建后仍然在（挂载卷持久化）
#
# 背景：镜像里那版是 2018 年的 bootstrap，自更新时报
#   "Download failed: http error 0 (client-download.steampowered.com/client/steam_cmd_linux)"
# 而且它**不读 HTTP_PROXY**，在有代理的环境下也用不了。新版（2023+）工作正常。
#
# 前置：把 steamcmd_linux.tar.gz 放到 /tmp/scnew/
set -uo pipefail

CONTAINER="${CONTAINER:-turbotick-server}"
HOST_DIR="${HOST_DIR:-/data/rimworld/srv/steamcmd-new}"
CT_DIR="${CT_DIR:-/data/steamcmd-new}"
SRC="${SRC:-/tmp/scnew}"

if [ ! -f "$SRC/steamcmd.sh" ]; then
    if [ -f /tmp/scanew/steamcmd.sh ]; then
        SRC=/tmp/scanew
        echo "使用已解包的 /tmp/scanew"
    else
        echo "ERROR: 找不到 $SRC/steamcmd.sh（需要先解包 steamcmd_linux.tar.gz）" >&2
        exit 1
    fi
fi

mkdir -p "$HOST_DIR"
cp -a "$SRC"/. "$HOST_DIR"/
chown -R 10001:10001 "$HOST_DIR"

docker exec -u 0 "$CONTAINER" sh -c \
    "chown -R 10001:10001 $CT_DIR 2>/dev/null || true; \
     chmod -R u+rwX,go+rX $CT_DIR 2>/dev/null || true"

echo "=== 校验 ==="
docker exec "$CONTAINER" sh -c "ls -l $CT_DIR/steamcmd.sh $CT_DIR/linux32/steamcmd"
docker exec -w "$CT_DIR" "$CONTAINER" sh -c './steamcmd.sh +quit 2>&1 | grep -iE "Console Client" | head -1'
