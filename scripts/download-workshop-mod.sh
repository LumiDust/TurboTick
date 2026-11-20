#!/bin/bash
# 按工坊 ID 下载 RimWorld mod —— **全匿名，不需要 Steam 账号**。
#
# 结论（实测，见 progress.md 2026-11-12）：
#   工坊 mod 用 `+login anonymous` 就能下载；只有**下载游戏本体**（app_update 294100）
#   才必须有拥有该游戏的账号。所以服务端「按存档补齐 mod」这条路不需要用户登录。
#
# 三个必须做对的地方，错一个就 "ERROR! Download item <id> failed (Failure)"：
#   1. HOME 必须是**持久化目录**（/data/state）——steamcmd 在那里放匿名凭据缓存；
#      用临时 HOME 会失败，且报错完全不提这一点。
#   2. 必须显式传 HTTP_PROXY/HTTPS_PROXY —— .29 无国际出口，且老 bootstrap 不读环境代理。
#   3. +force_install_dir 必须指向**已挂载到宿主的**目录（/data/game），
#      否则内容写进容器层，重建即丢。
#
# 用法：
#   bash download-workshop-mod.sh 2009463077 3830470974
#   PROXY=http://192.168.0.30:7897 bash download-workshop-mod.sh <id...>
set -uo pipefail

PROXY="${PROXY:-http://192.168.0.30:7897}"
IMAGE="${IMAGE:-turbotick-headless:0.7.2}"
APPID="${APPID:-294100}"
HOST_GAME="${HOST_GAME:-/data/rimworld/srv/game}"
HOST_STATE="${HOST_STATE:-/data/rimworld/srv/state}"
CONTAINER_GAME=/data/game

if [ $# -eq 0 ]; then
    echo "用法: $0 <workshopId> [workshopId...]" >&2
    exit 2
fi

echo "代理=$PROXY  镜像=$IMAGE  落点=$HOST_GAME/steamapps/workshop/content/$APPID"

if [ ! -x "$HOST_GAME/steamcmd/steamcmd.sh" ]; then
    echo "ERROR: 缺少新版 steamcmd（$HOST_GAME/steamcmd）" >&2
    echo "       先跑 scripts/install-steamcmd-new.sh" >&2
    exit 1
fi

fail=0
for id in "$@"; do
    echo "--- $id ---"
    # 用一次性容器：下载与游戏进程互不干扰（同容器并行会让 entrypoint 判游戏退出而重启）
    out=$(docker run --rm --entrypoint sh \
            -e HTTP_PROXY="$PROXY" -e HTTPS_PROXY="$PROXY" -e HOME=/data/state \
            -v "$HOST_GAME:$CONTAINER_GAME" \
            -v "$HOST_STATE:/data/state" \
            -w "$CONTAINER_GAME/steamcmd" \
            "$IMAGE" -c "timeout 600 ./steamcmd.sh \
                +force_install_dir $CONTAINER_GAME \
                +login anonymous \
                +workshop_download_item $APPID $id validate +quit 2>&1" \
          | grep -aE "Success|ERROR!|No subscription")

    if echo "$out" | grep -q "Success"; then
        echo "  OK  $(echo "$out" | grep Success | head -1 | sed 's/Unloading.*//')"
    else
        echo "  FAIL" >&2
        echo "$out" | head -3 >&2
        fail=$((fail + 1))
    fi
done

echo "=== 工坊内容（packageId 供服务端建联）==="
for d in "$HOST_GAME"/steamapps/workshop/content/$APPID/*/; do
    [ -d "$d" ] || continue
    wid=$(basename "$d")
    pkg=$(grep -oE "<packageId>[^<]*" "$d/About/About.xml" 2>/dev/null | head -1 | sed "s|<packageId>||")
    nm=$(grep -oE "<name>[^<]*" "$d/About/About.xml" 2>/dev/null | head -1 | sed "s|<name>||")
    sz=$(du -sh "$d" 2>/dev/null | cut -f1)
    printf '  %-12s %-34s %-28s %s\n' "$wid" "${pkg:-?}" "${nm:-?}" "${sz:-?}"
done

exit $fail
