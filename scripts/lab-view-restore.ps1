# 阶段 8 实验台：稳定身份 token + 会话视图恢复（地图 / 选中 / 相机）
#
# 判据（每条都打 PASS/FAIL，证据是真实日志行）：
#   T1 客户端首次连上后 `<profile>\Config\TurboTick\player-token.txt` 生成，内容是 64 位小写十六进制
#   T2 断开重连：文件内容不变；服务端日志出现「认出老玩家 token=…」
#   V1 客户端 1 造出可辨认的视图（选中一个特定小人 + 相机挪到具体坐标），服务端落盘
#   V2 重连后服务端下发 mapId/selected/camera，客户端执行恢复（日志写出恢复到了什么坐标/哪些 id）
#   N1 删掉 token 文件（= 另一个人）→ 服务端「没有记录」，客户端不跳到别人的位置
#   R1 服务端进程重启（新进程、同一 data 目录）→ 从会话文件读回记录，仍然恢复
#
# 历史坑（都是真踩过的）：两段式 profile、GBK 读日志、先杀进程等 4 秒、换构建清 state、
# 私有游戏树必须带 donor 的 steam 三件套（否则静默 exit=1、stdout/stderr 全空）。
param(
    [int]$Port = 30611,
    [string]$Lab = 'E:\dsh\workspace\rimworld\_lab\view',
    [int]$WarmupSeconds = 40,
    [int]$LoadTimeoutSeconds = 240,
    [switch]$SkipBuild,
    [switch]$SkipWarmup,
    [switch]$KeepLab,
    [switch]$ProbeOnly
)

$ErrorActionPreference = 'Stop'
$wt   = 'E:\dsh\workspace\rimworld\TurboTick-headless'
$src  = 'C:\SteamLibrary\steamapps\common\RimWorld'
$steamDonor = 'E:\dsh\workspace\rimworld\_lab\headless\game'
$dn   = 'E:\dsh\workspace\rimworld\tools\dotnet10\dotnet.exe'
$game = "$Lab\game"
$serverProfile = "$Lab\server-profile"
$clientProfile = "$Lab\client-profile"
$logs = "$Lab\logs"
$state = "$Lab\state"
$tokenFile = "$clientProfile\Config\TurboTick\player-token.txt"
$env:DOTNET_ROOT = 'E:\dsh\workspace\rimworld\tools\dotnet10'

$results = [System.Collections.ArrayList]::new()
function Judge([string]$id, [bool]$ok, [string]$what) {
    [void]$results.Add([pscustomobject]@{ Id = $id; Ok = $ok; What = $what })
    $colour = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1}  {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $id, $what) -ForegroundColor $colour
}

function Say($m) { Write-Host "[$(Get-Date -Format HH:mm:ss)] $m" }

function Stop-LabProcesses {
    Get-CimInstance Win32_Process -Filter "Name='RimWorld.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -like "*$Lab*" } |
        ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force; Say "killed $($_.ProcessId)" } catch { } }
}

function Read-GbkText([string]$path, [int]$bytes = 800000) {
    if (-not (Test-Path $path)) { return '' }
    $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    try {
        $len = $fs.Length
        if ($len -gt $bytes) { [void]$fs.Seek($len - $bytes, 'Begin') }
        $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::GetEncoding(936))
        return $sr.ReadToEnd()
    } finally { $fs.Close() }
}

function Select-GbkLines([string]$path, [string]$pattern, [int]$count = 6) {
    $text = Read-GbkText $path
    if (-not $text) { return @() }
    $hits = @($text -split "`r?`n" | Where-Object { $_ -match $pattern })
    if ($hits.Count -le $count) { return $hits }
    return $hits[($hits.Count - $count)..($hits.Count - 1)]
}

function Wait-Marker([string]$path, [string]$pattern, [int]$timeoutSeconds, [int]$after = 0) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $hits = @(Select-GbkLines $path $pattern 2)
        if ($hits.Count -gt $after) { return $hits }
        Start-Sleep -Seconds 2
    }
    return @()
}

# 按「匹配行数增加」判定新事件：日志是追加的，而同一个正则会在不同阶段反复命中，
# 只判『有没有匹配』会把上一阶段的旧行当成本阶段的结果（本轮已出现过一次假 PASS）。
function Count-Marker([string]$path, [string]$pattern) {
    return @(Select-GbkLines $path $pattern 500).Count
}

function Wait-More([string]$path, [string]$pattern, [int]$timeoutSeconds, [int]$before) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $now = Count-Marker $path $pattern
        if ($now -gt $before) { return $now }
        Start-Sleep -Seconds 2
    }
    return (Count-Marker $path $pattern)
}

function Show-Section($title, $lines, $fallback = '（无匹配行）') {    Write-Host ''
    Write-Host "---- $title ----"
    if ($lines -and $lines.Count) { $lines | ForEach-Object { Write-Host $_ } } else { Write-Host $fallback }
}

$modsConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<ModsConfigData>
  <version>1.7.5005</version>
  <activeMods>
    <li>ludeon.rimworld</li>
    <li>brrainz.harmony</li>
    <li>lumidust.turbotick</li>
  </activeMods>
  <knownExpansions />
</ModsConfigData>
"@

Say "0/9 清理（$Lab，端口 $Port）"
Stop-LabProcesses
Start-Sleep -Seconds 3
# 换构建会改 modHash；服务端会（正确地）拒绝旧房间。整棵 state 一起清。
if (Test-Path $state) { Remove-Item $state -Recurse -Force -ErrorAction SilentlyContinue }
foreach ($d in @($logs, $state, $serverProfile, $clientProfile)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }

Say '1/9 构建（并快照 dll，防止并行构建把产物换掉）'
if (-not $SkipBuild) {
    Push-Location $wt
    & $dn build Source\TurboTick.csproj -c Release /p:RimWorldDir=E:\dsh\workspace\rimworld\tools\ref17-5005 -v q
    $code = $LASTEXITCODE
    Pop-Location
    if ($code -ne 0) { throw '构建失败' }
} else { Say '     构建（跳过）' }
$snap = "$Lab\dll"
if (Test-Path $snap) { Remove-Item $snap -Recurse -Force }
New-Item -ItemType Directory -Path $snap -Force | Out-Null
Copy-Item "$wt\1.7\Assemblies\*.dll" $snap -Force
$dllHash = (Get-FileHash "$snap\TurboTick.dll" -Algorithm SHA256).Hash
Say "     dll sha256=$($dllHash.Substring(0,16))  $((Get-Item "$snap\TurboTick.dll").LastWriteTime)"

Say '2/9 私有游戏树'
if (Test-Path $game) { cmd /c "rmdir `"$game`"" 2>$null | Out-Null }
New-Item -ItemType Directory -Path $game -Force | Out-Null
Get-ChildItem $src -Directory | Where-Object Name -ne 'Mods' | ForEach-Object {
    cmd /c "mklink /J `"$game\$($_.Name)`" `"$($_.FullName)`"" 2>$null | Out-Null
}
Get-ChildItem $src -File | ForEach-Object {
    cmd /c "mklink /H `"$game\$($_.Name)`" `"$($_.FullName)`"" 2>$null | Out-Null
}
# 本机安装树里的 steam_api64.dll 是硬链接过来的真文件；只有当某个加载器缺失时才从既有实验树补。
# （全量覆盖会「用自身覆盖自身」失败——两条树的同名文件其实是同一个硬链接。）
foreach ($f in @('steam_api64.dll', 'version.dll', 'Juij_Steam.dll', 'Juij_Steam.json')) {
    $dest = "$game\$f"
    if (-not (Test-Path $dest) -and (Test-Path "$steamDonor\$f")) { Copy-Item "$steamDonor\$f" $dest -Force }
}
New-Item -ItemType Directory -Path "$game\Mods\TurboTick\1.7\Assemblies" -Force | Out-Null
cmd /c "mklink /J `"$game\Mods\Harmony`" `"$src\Mods\Harmony`"" 2>$null | Out-Null
Copy-Item "$wt\About"           "$game\Mods\TurboTick\About"     -Recurse -Force
Copy-Item "$wt\Languages"       "$game\Mods\TurboTick\Languages" -Recurse -Force
Copy-Item "$wt\LoadFolders.xml" "$game\Mods\TurboTick\"          -Force -ErrorAction SilentlyContinue
Copy-Item "$snap\*.dll"         "$game\Mods\TurboTick\1.7\Assemblies\" -Force
Say "     树文件 $((Get-ChildItem $game -File).Count) 个"

Say "3/9 profile（两段式：首启会写只含 Core 的默认 ModsConfig）"
foreach ($p in @($serverProfile, $clientProfile)) {
    New-Item -ItemType Directory -Path "$p\Config" -Force | Out-Null
    $modsConfig | Set-Content "$p\Config\ModsConfig.xml" -Encoding utf8
}
if (-not $SkipWarmup) {
    $env:TURBOTICK_HOST = '127.0.0.1'; $env:TURBOTICK_PORT = "$Port"; $env:TURBOTICK_DATA = "$state"
    foreach ($pair in @(@('server', $serverProfile), @('client', $clientProfile))) {
        $warm = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
            -ArgumentList @('-headless', '-quicktest', "-savedatafolder=$($pair[1])") `
            -RedirectStandardOutput "$logs\warm-$($pair[0])-out.log" -RedirectStandardError "$logs\warm-$($pair[0])-err.log"
        Start-Sleep -Seconds $WarmupSeconds
        try { if (-not $warm.HasExited) { $warm.Kill() } } catch { }
        Start-Sleep -Seconds 3
    }
}
foreach ($p in @($serverProfile, $clientProfile)) {
    New-Item -ItemType Directory -Path "$p\Config" -Force | Out-Null
    $modsConfig | Set-Content "$p\Config\ModsConfig.xml" -Encoding utf8
}
if (Test-Path $tokenFile) { Remove-Item $tokenFile -Force }

Say '4/9 启动权威端'
Remove-Item Env:TURBOTICK_VIEW_PROBE -ErrorAction SilentlyContinue
Remove-Item Env:TURBOTICK_PROBE_BUILDING -ErrorAction SilentlyContinue
Remove-Item Env:TURBOTICK_PROBE_INJURY -ErrorAction SilentlyContinue
Remove-Item Env:TURBOTICK_SCRIPT_OPS -ErrorAction SilentlyContinue
$env:TURBOTICK_HOST = '127.0.0.1'
$env:TURBOTICK_PORT = "$Port"
$env:TURBOTICK_DATA = "$state"
$srvOut = "$logs\server-out.log"; $srvErr = "$logs\server-err.log"
$srv = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
    -ArgumentList @('-headless', '-turbotick-server', '-quicktest', "-savedatafolder=$serverProfile") `
    -RedirectStandardOutput $srvOut -RedirectStandardError $srvErr
Say "     pid=$($srv.Id)"
$status = "$state\headless-status.json"
$deadline = (Get-Date).AddSeconds($LoadTimeoutSeconds)
$playing = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    if ($srv.HasExited) {
        $why = '(无状态文件)'
        if (Test-Path $status) { try { $j = Get-Content $status -Raw | ConvertFrom-Json; $why = "$($j.state) / $($j.failure)" } catch { } }
        throw "权威端退出：$why（见 $srvErr / $srvOut）"
    }
    if (Test-Path $status) {
        try { $j = Get-Content $status -Raw | ConvertFrom-Json } catch { continue }
        if ($j.state -eq 'playing') { $playing = $true; break }
    }
}
if (-not $playing) { throw "权威端 $LoadTimeoutSeconds 秒内没有进入 playing（见 $srvOut）" }
Say '     权威端 playing'

function Start-Client([string]$label, [string]$profile, [string]$outName, [string]$probe) {
    $env:TURBOTICK_HOST = '127.0.0.1'
    $env:TURBOTICK_PORT = "$Port"
    if ($probe) { $env:TURBOTICK_VIEW_PROBE = $probe } else { Remove-Item Env:TURBOTICK_VIEW_PROBE -ErrorAction SilentlyContinue }
    $out = "$logs\$outName-out.log"; $err = "$logs\$outName-err.log"
    Remove-Item $out, $err -ErrorAction SilentlyContinue
    $p = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
        -ArgumentList @('-headless', '-quicktest', "-turbotick-connect=127.0.0.1:$Port", "-savedatafolder=$profile") `
        -RedirectStandardOutput $out -RedirectStandardError $err
    Say "     $label pid=$($p.Id) probe=$probe 日志=$out"
    return @{ Process = $p; Out = $out; Err = $err }
}

function Stop-Client($client, [int]$waitSeconds = 5) {
    try { if (-not $client.Process.HasExited) { $client.Process.Kill() } } catch { }
    Start-Sleep -Seconds $waitSeconds
}

Say '5/9 客户端 1：连上、拿到 token、造一个可辨认的视图'
$probePattern = '视图状态 <-.*selected=\[[0-9]'
$before = Count-Marker $srvOut $probePattern
$c1 = Start-Client '客户端1' $clientProfile 'client1' 'select'
$sel = @(Wait-Marker $c1.Out '视图探针 select' $LoadTimeoutSeconds)
[void](Wait-More $srvOut $probePattern 90 $before)
Show-Section '客户端1 身份/视图' (Select-GbkLines $c1.Out 'player-token|登记身份|带上本地身份|视图探针|WorldReady' 8)
Show-Section '服务端：新玩家与视图落盘' (Select-GbkLines $srvOut '新玩家|视图状态 <-' 6)
$tokenAfterFirst = if (Test-Path $tokenFile) { (Get-Content $tokenFile -Raw).Trim() } else { '' }
Judge 'T1' ($tokenAfterFirst -match '^[0-9a-f]{64}$') "首次连接后 token 文件= $tokenAfterFirst"
$viewLine = ''
$probeLines = @(Select-GbkLines $srvOut $probePattern 200)
if ($probeLines.Count) { $viewLine = $probeLines[-1] }
Judge 'V1' (($sel.Count -gt 0) -and ($viewLine.Length -gt 0)) '客户端造出视图并上报到服务端'
$selIds = ''
if ($viewLine -match 'selected=\[([0-9,]*)\]') { $selIds = $Matches[1] }
$reportMap = ''; $reportCam = ''; $reportSize = ''
if ($viewLine -match 'mapId=(-?\d+)') { $reportMap = $Matches[1] }
if ($viewLine -match 'camera=\(([-\d.]+),([-\d.]+)\)') { $reportCam = "$($Matches[1]),$($Matches[2])" }
if ($viewLine -match 'size=([-\d.]+)') { $reportSize = $Matches[1] }
Say "     服务端记录：mapId=$reportMap selected=[$selIds] camera=($reportCam) size=$reportSize"
if ($ProbeOnly) {
    Show-Section '探针诊断（ProbeOnly）' (Select-GbkLines $c1.Out '视图探针' 8)
    Show-Section '服务端视图行（ProbeOnly）' (Select-GbkLines $srvOut '视图状态 <-|视图恢复 ->' 8)
    Stop-Client $c1
    Stop-LabProcesses
    exit 0
}
Stop-Client $c1

Say '6/9 客户端 2：同 profile 重连（同一个 token、应被认出来并恢复视图）'
$beforeKnown = Count-Marker $srvOut '认出老玩家'
$beforeRestore = Count-Marker $srvOut '视图恢复 ->.*mapId='
$c2 = Start-Client '客户端2' $clientProfile 'client2' 'verify'
$knownAfter = Wait-More $srvOut '认出老玩家' 120 $beforeKnown
$restoreAfter = Wait-More $srvOut '视图恢复 ->.*mapId=' 120 $beforeRestore
# 注意：函数返回单元素数组时 PowerShell 会展开成字符串，取 [-1] 就成了「最后一个字符」。
# 所以这里统一用 @() 兜住再取末尾元素。
$restoreCli = @(Wait-Marker $c2.Out '视图恢复：目标' $LoadTimeoutSeconds)
$verify = @(Wait-Marker $c2.Out '视图探针 verify' 90)
Show-Section '服务端：认出老玩家 + 下发恢复' (Select-GbkLines $srvOut '认出老玩家|视图恢复 ->' 6)
Show-Section '客户端2：执行恢复' (Select-GbkLines $c2.Out '视图恢复：目标|视图探针 verify|WorldReady' 8)
$tokenAfterSecond = if (Test-Path $tokenFile) { (Get-Content $tokenFile -Raw).Trim() } else { '' }
Judge 'T2' (($tokenAfterFirst.Length -gt 0) -and ($tokenAfterFirst -eq $tokenAfterSecond)) '重连后 token 文件内容不变'
Judge 'T2b' ($knownAfter -gt $beforeKnown) '服务端日志出现「认出老玩家 token=…」'
Judge 'V2' ($restoreAfter -gt $beforeRestore) '服务端下发了 mapId/selected/camera'
Judge 'V2b' ($restoreCli.Count -gt 0) '客户端执行了恢复（日志写出恢复到了什么坐标/哪些 id）'
# 「实际」那一行是跳转后当帧读回的值：CameraDriver 下一帧才把位置落到 rootPos，
# 所以拿它做断言会假红。真正的观测量是 2 秒后的 verify 探针。
$probeLine = if ($verify.Count) { [string]$verify[-1] } else { '' }
$appliedIds = ''; $appliedMap = ''; $appliedCam = ''; $appliedSize = ''
if ($probeLine -match '地图=(-?\d+) 选中=\[([0-9,]*)\] 相机=\(([-\d.]+),([-\d.]+)\) size=([-\d.]+)') {
    $appliedMap = $Matches[1]; $appliedIds = $Matches[2]; $appliedCam = "$($Matches[3]),$($Matches[4])"; $appliedSize = $Matches[5]
}
Judge 'V2c' (($selIds.Length -gt 0) -and ($appliedIds -eq $selIds)) "恢复后的选中 id 与离开时一致（$appliedIds vs $selIds）"
Judge 'V2d' (($appliedMap -eq $reportMap) -and ($appliedCam -eq $reportCam)) "恢复后的地图/相机与记录一致（mapId=$appliedMap camera=($appliedCam) vs mapId=$reportMap camera=($reportCam)）"
Judge 'V2e' ([math]::Abs([double]$appliedSize - [double]$reportSize) -lt 0.05) "恢复后的缩放与记录一致（$appliedSize vs $reportSize）：$probeLine"
Stop-Client $c2

Say '7/9 客户端 3：删掉 token（模拟另一个人）→ 空记录，不跳到别人的位置'
$backup = "$Lab\player-token.bak"
Copy-Item $tokenFile $backup -Force
Remove-Item $tokenFile -Force
$beforeNone = Count-Marker $srvOut '没有记录，停在默认视图'
$c3 = Start-Client '客户端3' $clientProfile 'client3' 'verify'
$noneAfter = Wait-More $srvOut '没有记录，停在默认视图' 120 $beforeNone
$verify3 = @(Wait-Marker $c3.Out '视图探针 verify' 90)
$anyRestore = @(Select-GbkLines $c3.Out '视图恢复：目标' 200)
Show-Section '服务端：没有记录' (Select-GbkLines $srvOut '没有记录' 4)
Show-Section '客户端3：停留默认视图' (Select-GbkLines $c3.Out '视图探针 verify' 4)
Judge 'N1' ($noneAfter -gt $beforeNone) '服务端按「没有记录」处理新 token'
Judge 'N2' (($anyRestore.Count -eq 0) -and ($verify3.Count -gt 0)) '客户端 3 没有被带到别人的相机位置'
Stop-Client $c3
Copy-Item $backup $tokenFile -Force

Say '8/9 服务端重启：新进程、同一 data 目录（记录必须从文件读回）'
try { if (-not $srv.HasExited) { $srv.Kill() } } catch { }
Start-Sleep -Seconds 6
$srvOut2 = "$logs\server2-out.log"; $srvErr2 = "$logs\server2-err.log"
Remove-Item $srvOut2, $srvErr2 -ErrorAction SilentlyContinue
# 不带 -quicktest：重启的进程不重新发布世界，房间沿用 state 里的初始世界
# （会话文件按「这一局的初始世界」命名，检查点换了不影响它）。
$srv2 = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
    -ArgumentList @('-headless', '-turbotick-server', "-savedatafolder=$serverProfile") `
    -RedirectStandardOutput $srvOut2 -RedirectStandardError $srvErr2
$listening = Wait-Marker $srvOut2 'listening on' 180
Show-Section '重启后的服务端' (Select-GbkLines $srvOut2 'listening on' 3)
$c4 = Start-Client '客户端4' $clientProfile 'client4' 'verify'
$recognized2 = Wait-More $srvOut2 '认出老玩家' 180 0
$restore2 = Wait-More $srvOut2 '视图恢复 ->.*mapId=' 180 0
$verify4 = @(Wait-Marker $c4.Out '视图探针 verify' 180)
Show-Section '服务端（重启后）：认出老玩家 + 恢复' (Select-GbkLines $srvOut2 '认出老玩家|视图恢复 ->' 6)
Show-Section '客户端4：恢复结果' (Select-GbkLines $c4.Out '视图恢复：目标|视图探针 verify|WorldReady' 8)
Judge 'R1' ($recognized2 -gt 0) '新进程从会话文件认出了老玩家'
Judge 'R2' ($restore2 -gt 0) '新进程从会话文件读回了视图并下发'
$verifyLine4 = if ($verify4.Count) { [string]$verify4[-1] } else { '' }
$applied4 = ''
if ($verifyLine4 -match '选中=\[([0-9,]*)\]') { $applied4 = $Matches[1] }
Judge 'R3' (($selIds.Length -gt 0) -and ($applied4 -eq $selIds)) "重启后客户端仍恢复到原来的选中（$applied4 vs $selIds）：$verifyLine4"
Stop-Client $c4

Say '9/9 会话文件'
Get-ChildItem "$state\session" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "  $($_.Name)  $($_.Length) B"
    Get-Content $_.FullName -Raw | Write-Host
}

Write-Host ''
Write-Host '==== 汇总 ===='
$failed = @($results | Where-Object { -not $_.Ok })
foreach ($r in $results) { Write-Host ("  {0} {1} {2}" -f $(if ($r.Ok) { 'PASS' } else { 'FAIL' }), $r.Id, $r.What) }
Write-Host ("  {0}/{1} 通过" -f ($results.Count - $failed.Count), $results.Count)

Say '收工'
Stop-LabProcesses
Start-Sleep -Seconds 3
if (-not $KeepLab) { Say "日志与 profile 保留在 $Lab" }
if ($failed.Count -gt 0) { exit 1 }
