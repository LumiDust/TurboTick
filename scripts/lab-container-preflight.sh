#!/bin/bash
# 容器预检/凝聚力 DLC 验收台（在部署机上跑，38 条判据）。
#
#   bash scripts/lab-container-preflight.sh            # 全跑
#   RIG=/data/rimworld/srv bash …                      # 改测试树
#
# 前置：镜像 turbotick-headless:0.7.2 已构建；测试树 $RIG 有 game/mods/saves/state/steamcmd/dlc/backups，
# 且 /tmp/stock.dll 是**官方** com.rlabrecque.steamworks.net.dll（脚本每个场景都会把它还原成基线）。
# 场景：S0 重打补丁+补齐 DLC / S1 already / S2 缺内容硬失败 / S3 未知 DLC / S3b 关开关缺 DLC /
# S3c modIds 解析 / S4 版本漂移 / S5 签名不匹配 / S6 --adopt / S7 正常 playing+备份+expansion=
#!/bin/bash
# 预检场景验收 v3。每个场景都从「Steam 刚更新过」的干净基线开始：官方 DLL + 无 sidecar。
set -u
RIG=/data/rimworld/srv
IMAGE=turbotick-headless:0.7.2
NAME=turbotick-s
DLL=$RIG/game/com.rlabrecque.steamworks.net.dll
PASS=0; FAIL=0
ok()   { PASS=$((PASS+1)); echo "  PASS $*"; }
bad()  { FAIL=$((FAIL+1)); echo "  FAIL $*"; }
check(){ if [ "$1" = 1 ]; then ok "$2"; else bad "$2"; fi; }
rmc()  { docker rm -f $NAME >/dev/null 2>&1 || true; }
reset_state() { rm -rf $RIG/state/* $RIG/state/.config $RIG/backups/*; }
pf()   { python3 -c "import json;d=json.load(open('$RIG/state/preflight.json'));print($1)" 2>/dev/null; }
stock() {  # 回到「官方 DLL + 无 sidecar」
  rmc
  cp -f /tmp/stock.dll $DLL
  rm -f $DLL.cohesion.json $DLL.runtime-backup
  reset_state
}
stage_only() {
  local extra=("$@")
  timeout 180 docker run --rm --name $NAME --entrypoint /opt/turbotick/entrypoint.sh \
    -e TURBOTICK_ADMIN_PASSWORD=rig-secret \
    -v $RIG/game:/data/game -v $RIG/mods:/data/mods -v $RIG/saves:/data/saves \
    -v $RIG/state:/data/state -v $RIG/steamcmd:/data/steamcmd -v $RIG/backups:/data/backups \
    -v ${DLC_MOUNT:-$RIG/dlc}:/opt/cohesion/dlc:ro "${extra[@]}" $IMAGE stage
}

echo "=========== S0: 干净基线 + 全部 DLC → 打补丁、15 位 DLC 5/5、正常起 ==========="
stock
rm -rf $RIG/game/Data/Odyssey      # 制造「Steam 更新后 DLC 目录还在」之外的另一种情况：缺一个
DLC_MOUNT=$RIG/dlc stage_only -e TURBOTICK_SAVE=probe >/tmp/s0.log 2>&1
S0=$?
echo "  stage 退出码=$S0"
grep -q "patched offset" /tmp/s0.log && ok "日志：在游戏自己的程序集上打了补丁" || bad "没有打补丁日志"
grep -q "补齐 Data/Odyssey" /tmp/s0.log && ok "日志：从挂载补齐 Data/Odyssey" || bad "没有补齐日志"
check "$(pf "1 if d['ok'] else 0")" "preflight.ok=true"
check "$(pf "1 if d['repatch']=='reapplied' else 0")" "repatch=reapplied"
check "$(pf "1 if len(d['dlc']['available'])==5 else 0")" "available=5"
check "$(pf "1 if d['dlc']['staged']==['odyssey'] else 0")" "staged=[odyssey]"
check "$(pf "1 if d['save']['backup'] else 0")" "存档已备份"

echo
echo "=========== S1: 补丁已在位（第二次启动）→ repatch=already，不重复写 ==========="
DLC_MOUNT=$RIG/dlc stage_only -e TURBOTICK_SAVE=probe >/tmp/s1.log 2>&1
check "$(pf "1 if d['repatch']=='already' else 0")" "repatch=already"

echo
echo "=========== S2: DLC 目录缺失且无处可补 → 硬失败，游戏不启动 ==========="
stock
rm -rf $RIG/game/Data/Odyssey
mkdir -p /tmp/empty-dlc
DLC_MOUNT=/tmp/empty-dlc stage_only -e TURBOTICK_SAVE=probe >/tmp/s2.log 2>&1
S2=$?
echo "  stage 退出码=$S2"
[ "$S2" = 2 ] && ok "拒绝启动（退出码 2）" || bad "退出码应为 2"
check "$(pf "1 if d['reason']=='dlc-content-missing' else 0")" "reason=dlc-content-missing"
grep -qi "Odyssey" $RIG/state/preflight.json && ok "点名 Odyssey" || bad "没点名"
timeout 60 docker run --rm --name $NAME -e TURBOTICK_ADMIN_PASSWORD=rig-secret -e TURBOTICK_SAVE=probe \
  -v $RIG/game:/data/game -v $RIG/mods:/data/mods -v $RIG/saves:/data/saves -v $RIG/state:/data/state \
  -v $RIG/steamcmd:/data/steamcmd -v $RIG/backups:/data/backups -v /tmp/empty-dlc:/opt/cohesion/dlc:ro \
  $IMAGE >/tmp/s2b.log 2>&1
[ "$?" = 2 ] && ok "完整 run 同样以 2 退出" || bad "run 退出码不对"
grep -q "游戏未启动" /tmp/s2b.log && ok "日志写明游戏未启动" || bad "日志没写明"
[ ! -f $RIG/state/headless-status.json ] && ok "无 headless-status.json" || bad "状态文件存在"

echo
echo "=========== S3: 存档声明未知 DLC → 硬失败并点名 ==========="
stock
mv /tmp/Odyssey.bak $RIG/game/Data/Odyssey 2>/dev/null || true
rm -rf $RIG/game/Data/Odyssey 2>/dev/null || true
python3 - <<'PY'
import pathlib, zipfile
src = pathlib.Path('/data/rimworld/srv/saves/probe.rws').read_bytes().decode('utf-8', 'replace')
if '<knownExpansions' not in src:
    src = src.replace('<meta>', '<meta>\n\t\t<knownExpansions><li>Ludeon.RimWorld.Odyssey</li><li>Ludeon.RimWorld.Future</li></knownExpansions>', 1)
pathlib.Path('/data/rimworld/srv/saves/needs-unknown.rws').write_bytes(src.encode('utf-8'))
PY
# 用 dlc 挂载补齐 Odyssey，让「缺内容」这条不干扰本场景
DLC_MOUNT=$RIG/dlc stage_only -e TURBOTICK_SAVE=needs-unknown >/tmp/s3.log 2>&1
S3=$?
[ "$S3" = 2 ] && ok "拒绝启动（退出码 2）" || bad "退出码 $S3"
check "$(pf "1 if d['reason']=='save-needs-unknown-dlc' else 0")" "reason=save-needs-unknown-dlc"
grep -qi "future" $RIG/state/preflight.json && ok "点名 Future" || bad "没点名"

echo
echo "=========== S3b: 关开关 + 存档需要 Odyssey 而 Odyssey 不在 → 硬失败 ==========="
stock
rm -rf $RIG/game/Data/Odyssey
python3 - <<'PY'
import pathlib
src = pathlib.Path('/data/rimworld/srv/saves/probe.rws').read_bytes().decode('utf-8', 'replace')
if '<knownExpansions' not in src:
    src = src.replace('<meta>', '<meta>\n\t\t<knownExpansions><li>Ludeon.RimWorld.Odyssey</li></knownExpansions>', 1)
pathlib.Path('/data/rimworld/srv/saves/needs-odyssey.rws').write_bytes(src.encode('utf-8'))
PY
DLC_MOUNT=/tmp/empty-dlc stage_only -e TURBOTICK_SAVE=needs-odyssey -e TURBOTICK_COHESION_DLC=off >/tmp/s3b.log 2>&1
S3B=$?
[ "$S3B" = 2 ] && ok "关闭开关时同样拒绝启动" || bad "退出码 $S3B"
check "$(pf "1 if d['reason']=='save-needs-missing-dlc' else 0")" "reason=save-needs-missing-dlc"
grep -qi "Odyssey" $RIG/state/preflight.json && ok "点名 Odyssey" || bad "没点名"
check "$(pf "1 if d['repatch']=='skipped' else 0")" "repatch=skipped"
sha256sum -c <<< "$(sha256sum /tmp/stock.dll | cut -d' ' -f1)  $DLL" >/dev/null 2>&1 && ok "off 时 DLL 未被改动（sha 与官方一致）" || bad "DLL 被改了"

echo
echo "=========== S3c: 真实存档 <modIds> 里的 DLC 能被读出来 ==========="
stock
python3 - <<'PY'
import pathlib
src = pathlib.Path('/data/rimworld/srv/saves/probe.rws').read_bytes().decode('utf-8', 'replace')
src = src.replace('<modIds>', '<modIds><li>ludeon.rimworld.odyssey</li>', 1)
pathlib.Path('/data/rimworld/srv/saves/modids-odyssey.rws').write_bytes(src.encode('utf-8'))
PY
DLC_MOUNT=$RIG/dlc stage_only -e TURBOTICK_SAVE=modids-odyssey >/tmp/s3c.log 2>&1
check "$(pf "1 if d['dlc']['required']==['odyssey'] else 0")" "required=[odyssey]"
check "$(pf "1 if d['save']['dlcReadable'] else 0")" "dlcReadable=true"
check "$(pf "1 if d['ok'] else 0")" "preflight.ok=true（内容齐全时可启动）"

echo
echo "=========== S4: 版本漂移（两趟：先记下 A，再改成 B） ==========="
stock
cp $RIG/game/Version.txt /tmp/Version.keep
DLC_MOUNT=$RIG/dlc stage_only -e TURBOTICK_SAVE=probe >/tmp/s4a.log 2>&1
[ -f $RIG/state/last-version.txt ] && ok "第一趟写下 last-version.txt" || bad "第一趟没写 last-version"
rm -rf $RIG/state/headless-status.json $RIG/state/preflight.json $RIG/state/profile
sed -i 's/1.7.5005/1.7.5006/' $RIG/game/Version.txt
DLC_MOUNT=$RIG/dlc stage_only -e TURBOTICK_SAVE=probe >/tmp/s4.log 2>&1
grep -q "游戏已更新 1.7.5005 rev1082 → 1.7.5006 rev1082" /tmp/s4.log && ok "日志：游戏已更新 A → B" || { bad "没有版本更新行"; head -6 /tmp/s4.log; }
grep -q "1.7.5006" $RIG/state/last-version.txt && ok "last-version.txt 记录新版本" || bad "last-version 缺失"
check "$(pf "1 if d['versionChange'] else 0")" "preflight.versionChange 非空"
cp /tmp/Version.keep $RIG/game/Version.txt

echo
echo "=========== S5: 方法体签名不匹配（模拟上游改了构建）→ 拒绝启动 ==========="
stock
python3 - <<'PY'
import pathlib
p = pathlib.Path('/data/rimworld/srv/game/com.rlabrecque.steamworks.net.dll')
data = bytearray(p.read_bytes())
data[0x2B8:0x2B8+17] = b'\x90' * 17
p.write_bytes(bytes(data))
PY
DLC_MOUNT=$RIG/dlc stage_only -e TURBOTICK_SAVE=probe >/tmp/s5.log 2>&1
S5=$?
[ "$S5" = 2 ] && ok "拒绝启动（更新窗口）" || bad "退出码 $S5"
check "$(pf "1 if d['reason']=='patch-signature-missing' else 0")" "reason=patch-signature-missing"

echo
echo "=========== S6: --adopt 逃生舱：已打过补丁但没有记账 ==========="
stock
cp -f /tmp/stock.dll $DLL
python3 - <<'PY'
import pathlib
# 手工把 0x2B8 处改成 tiny body，模拟「旧镜像打过的 DLL」，不留 sidecar
p = pathlib.Path('/data/rimworld/srv/game/com.rlabrecque.steamworks.net.dll')
data = bytearray(p.read_bytes())
data[0x2B8:0x2B8+3] = bytes([0x0A, 0x17, 0x2A])
p.write_bytes(bytes(data))
PY
DLC_MOUNT=$RIG/dlc stage_only -e TURBOTICK_SAVE=probe >/tmp/s6a.log 2>&1
[ "$?" = 2 ] && ok "无记账时拒绝（不猜偏移）" || bad "应当拒绝"
docker run --rm --entrypoint python3 -v $RIG/game:/data/game $IMAGE /opt/turbotick/scripts/patch-cohesion.py --game /data/game --adopt >/tmp/s6b.log 2>&1
grep -q "adopted" /tmp/s6b.log && ok "--adopt 只补记账" || bad "--adopt 没有生效"
reset_state
DLC_MOUNT=$RIG/dlc stage_only -e TURBOTICK_SAVE=probe >/tmp/s6c.log 2>&1
check "$(pf "1 if d['repatch']=='already' and d['ok'] else 0")" "adopt 之后按 already 通过"

echo
echo "=========== S7: 复原后正常启停（playing + 备份 + expansion=） ==========="
stock
docker run -d --name $NAME -p 30630:30572 -p 30631:30573 \
  -e TURBOTICK_ADMIN_PASSWORD=rig-secret -e TURBOTICK_SAVE=probe \
  -v $RIG/game:/data/game -v $RIG/mods:/data/mods -v $RIG/saves:/data/saves -v $RIG/state:/data/state \
  -v $RIG/steamcmd:/data/steamcmd -v $RIG/backups:/data/backups -v $RIG/dlc:/opt/cohesion/dlc:ro \
  $IMAGE >/dev/null
for i in $(seq 1 20); do
  sleep 6
  s=$(python3 -c "import json;print(json.load(open('$RIG/state/headless-status.json')).get('state'))" 2>/dev/null || echo none)
  [ "$s" = playing ] && break
  [ "$s" = failed ] && break
done
[ "$s" = playing ] && ok "state=playing" || bad "state=$s"
ls $RIG/backups/*.rws >/dev/null 2>&1 && ok "备份目录有 .rws（$(ls $RIG/backups | wc -l) 份）" || bad "没有备份"
docker logs $NAME 2>&1 | grep -q "expansion=Anomaly,Biotech,Ideology,Odyssey,Royalty" && ok "日志 expansion= 五 DLC 全在" || bad "没有 expansion="
docker logs $NAME 2>&1 | grep -q "预检 ok\|preflight ok\|ok · 版本" && ok "日志：preflight ok" || true
rmc

echo
echo "=========== 汇总：$PASS 通过 / $FAIL 失败 ==========="
[ "$FAIL" = 0 ]
