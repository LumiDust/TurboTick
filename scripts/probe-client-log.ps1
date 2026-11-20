# 用真实客户端配置连 .29 专服，只为了在副本日志里采集红字计数。
#
# 为什么不用 lab-headless.ps1：它自建私有游戏树，只挂 Harmony + TurboTick 两个 mod，
# 拿不到真实存档里的尸体/世界 pawn，也就复现不了 Corpse 红字。
# 这里用真实游戏根 + 真实 LocalLow profile（ModsConfig 与专服指纹一致），只把界面关掉。
param(
    [string]$Server = '192.168.0.29',
    [int]$Port = 30572,
    [int]$RunSeconds = 150,
    # Replica-side unforbid probe start tick. 0 means "from the first tick after load". Left at -1 the probe stays off,
    # which is the default: it rewrites real objects in the running world.
    [int]$UnforbidStart = -1,
    [string]$Log = "$env:USERPROFILE\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log"
)

$ErrorActionPreference = 'Stop'
$gameRoot = 'C:\SteamLibrary\steamapps\common\RimWorld'
$exe = Join-Path $gameRoot 'RimWorld.exe'

function Say($m) { Write-Host "[$(Get-Date -Format HH:mm:ss)] $m" }

if (Get-Process RimWorld -ErrorAction SilentlyContinue) { throw 'RimWorld.exe 已在运行，先关掉再跑，否则日志会串联。' }

# 上一轮的红字计数作为对照：新日志必须明显下降，而不是「看起来还行」。
$before = @{}
if (Test-Path $Log) {
    $text = Get-Content $Log -Raw -Encoding UTF8
    foreach ($pair in @(
        @('spawned in bugged state', 'spawned in bugged state'),
        @('新建后未落地', '新建后未落地'),
        @('Not all loadIDs which were read were consumed', 'Not all loadIDs'),
        @('Tried to get ThingsOfDef', 'Tried to get ThingsOfDef'))) {
        $before[$pair[1]] = ([regex]::Matches($text, [regex]::Escape($pair[0]))).Count
    }
    Say ("旧日志计数：" + (($before.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' / '))
    Copy-Item $Log "$Log.bak-$(Get-Date -Format HHmmss)" -Force
}

Say "连接 $Server`:$Port（无界面副本）"
# -quicktest is required, not cosmetic: a headless participant only auto-connects once the game is
# Playing, and without a world it sits on the entry screen forever while the log looks healthy.
# Keep the argument list on one physical line: a comment inside a backtick continuation breaks it.
$arguments = @('-headless', '-quicktest', "-turbotick-connect=$Server`:$Port")
if ($UnforbidStart -ge 0) {
    # Read by ForbidProbe from the environment, so it has to be set before the child process starts.
    $env:TURBOTICK_PROBE_UNFORBID = "$UnforbidStart"
    Say "解除探针已开启：TURBOTICK_PROBE_UNFORBID=$UnforbidStart（真实调用 SetForbidden(false)，与 U 键同一条路径）"
} else {
    Remove-Item Env:\TURBOTICK_PROBE_UNFORBID -ErrorAction SilentlyContinue
}
$proc = Start-Process $exe -WorkingDirectory $gameRoot -WindowStyle Hidden -PassThru `
    -ArgumentList $arguments `
    -RedirectStandardOutput "$env:TEMP\replica-out.log" -RedirectStandardError "$env:TEMP\replica-err.log"
Say "pid=$($proc.Id)，观察 $RunSeconds 秒"
Start-Sleep -Seconds $RunSeconds
try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
# 强杀后 .NET 控制台缓冲不会落盘，等一会儿再读，否则读到半截。
Start-Sleep -Seconds 4

$text = if (Test-Path $Log) { Get-Content $Log -Raw -Encoding UTF8 } else { '' }
Write-Host ''
Write-Host '---- 副本日志判定 ----'
foreach ($key in @('spawned in bugged state', '新建后未落地', 'Not all loadIDs which were read were consumed', 'Tried to get ThingsOfDef', '副本创建失败', '重定位失败', '引用残留')) {
    $n = ([regex]::Matches($text, [regex]::Escape($key))).Count
    $label = if ($key -like 'Not all*') { 'Not all loadIDs' } else { $key }
    $old = if ($before.ContainsKey($label)) { $before[$label] } else { $null }
    Write-Host ("  {0,-46} {1,6}{2}" -f $label, $n, $(if ($null -ne $old) { "   (旧 $old)" } else { '' }))
}
Write-Host ''
Write-Host '---- 权威帧（最后两行）----'
Select-String -Path $Log -Pattern '权威帧|新建后未落地|引用残留' -Encoding UTF8 |
    Select-Object -Last 2 | ForEach-Object { $_.Line.Trim() }

Write-Host ''
Write-Host '---- 禁止链路（探针与拒绝原因）----'
Select-String -Path $Log -Pattern '解除探针|禁止探针|未捕获' -Encoding UTF8 |
    Select-Object -Last 6 | ForEach-Object { $_.Line.Trim() }
if (-not (Select-String -Path $Log -Pattern '解除探针' -Encoding UTF8 -Quiet)) {
    Write-Host '  （没有解除探针行：TURBOTICK_PROBE_UNFORBID 未开启，或副本没进 Playing）'
}
