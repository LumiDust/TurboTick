# headless 专服实验台：1 个权威（无界面）+ 1 个副本（无界面、脚本操作）
#
# 解决两个历史坑：
#   1. 首次启动游戏会写一份只含 Core 的默认 ModsConfig.xml，导致 mod 没加载 —— 改成两段式预热。
#   2. 清理只按 PID 会漏掉由子进程拉起的实例 —— 改成按命令行前缀兜底。
#
# 只用私有游戏树与私有 profile，不碰真实安装，不碰生产端口 30572/30573。
param(
    [int]$Port = 30592,
    [string]$Lab = 'E:\dsh\workspace\rimworld\_lab\headless',
    [int]$WarmupSeconds = 45,
    [int]$RunSeconds = 120,
    [int]$Replicas = 1,
    # 伤病探针每 120 tick 让一个殖民者受伤，健忘的读数是 `Wait_Downed`。它是健康同步的输入，
    # 但会让「谁在做活」这类读数失真：配方法下发之后小人被打断，`DoBill 0 人` 就成了默认值。
    # 要观察长期工作（配方/队列），用 -NoInjury 关掉它。
    [switch]$NoInjury,
    [switch]$KeepLab,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$wt   = 'E:\dsh\workspace\rimworld\TurboTick-headless'
$src  = 'C:\SteamLibrary\steamapps\common\RimWorld'
$dn   = 'E:\dsh\workspace\rimworld\tools\dotnet10\dotnet.exe'
$game = "$Lab\game"
$env:DOTNET_ROOT = 'E:\dsh\workspace\rimworld\tools\dotnet10'

function Say($m) { Write-Host "[$(Get-Date -Format HH:mm:ss)] $m" }

function Stop-LabProcesses {
    Get-CimInstance Win32_Process -Filter "Name='RimWorld.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and ($_.CommandLine -like "*$Lab*" -or $_.CommandLine -like "*:$Port*") } |
        ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force; Say "killed $($_.ProcessId)" } catch { } }
}

function Read-LogLines($file, $pattern, $count) {
    if (-not (Test-Path $file)) { return @("(缺少 $file)") }
    $fs = [IO.File]::Open($file, 'Open', 'Read', 'ReadWrite')
    # 游戏日志是 GBK(936)；用 UTF-8 读会是乱码。
    $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::GetEncoding(936))
    $hits = New-Object System.Collections.ArrayList
    while ($null -ne ($l = $sr.ReadLine())) { if ($l -match $pattern) { [void]$hits.Add($l.Trim()) } }
    $sr.Close(); $fs.Close()
    if ($hits.Count -eq 0) { return @() }
    if ($hits.Count -le $count) { return $hits }
    return $hits[($hits.Count - $count)..($hits.Count - 1)]
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

Say '0/6 清理上一轮'
# 上一次被强杀的实例会留下 headless.lock / headless-status.json，权威端看到锁就拒绝启动。
# 实测症状：进程起来 5 秒就退，日志停在 Root_Entry.Update，没有任何异常——看起来像崩溃，其实是锁。
# 整个 state 目录一起清：每次重新构建都会改变 modHash，而服务端会（正确地）拒绝加载
# 「持久化房间与当前模组包不一致」的旧房间。只删锁是不够的，server-state.json 才是那份指纹。
if (Test-Path "$Lab\state") { Remove-Item "$Lab\state" -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path "$Lab\state" -Force | Out-Null
Stop-LabProcesses
Start-Sleep -Seconds 2

if (-not $SkipBuild) {
    Say '1/6 构建本线'
    Push-Location $wt
    & $dn build Source\TurboTick.csproj -c Release /p:RimWorldDir=E:\dsh\workspace\rimworld\tools\ref17-5005 -v q
    Pop-Location
    if ($LASTEXITCODE -ne 0) { throw '构建失败' }
}
$dll = "$wt\1.7\Assemblies\TurboTick.dll"
if (-not (Test-Path $dll)) { throw "缺少构建产物：$dll" }
Say ("     dll " + (Get-FileHash $dll -Algorithm SHA256).Hash.Substring(0, 16))

Say '2/6 私有游戏树 + 私有 Mods'
# 上一轮的树里全是 mklink /J 的目录联接和 mklink /H 的硬链接，`cmd /c rmdir` 对非空目录直接报
# "The directory is not empty."；而 `Remove-Item -Recurse` 会**跟着联接走**，把真实游戏安装删掉。
# 所以先按重解析点逐个 rmdir（只摘链接，不进去），剩下的普通文件和目录才交给 Remove-Item。
if (Test-Path $game) {
    Get-ChildItem $game -Force | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            cmd /c "rmdir `"$($_.FullName)`"" 2>$null | Out-Null
        } else {
            Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Remove-Item $game -Recurse -Force -ErrorAction SilentlyContinue
}
foreach ($d in @("$Lab\logs", "$Lab\state", "$Lab\server-profile", "$Lab\client-profile", $game)) {
    New-Item -ItemType Directory -Path $d -Force | Out-Null
}
Get-ChildItem $src -Directory | Where-Object Name -ne 'Mods' | ForEach-Object {
    cmd /c "mklink /J `"$game\$($_.Name)`" `"$($_.FullName)`"" 2>$null | Out-Null
}
Get-ChildItem $src -File | ForEach-Object {
    cmd /c "mklink /H `"$game\$($_.Name)`" `"$($_.FullName)`"" 2>$null | Out-Null
}
New-Item -ItemType Directory -Path "$game\Mods\TurboTick\1.7\Assemblies" -Force | Out-Null
cmd /c "mklink /J `"$game\Mods\Harmony`" `"$src\Mods\Harmony`"" 2>$null | Out-Null
Copy-Item "$wt\About"           "$game\Mods\TurboTick\About"                 -Recurse -Force
Copy-Item "$wt\Languages"       "$game\Mods\TurboTick\Languages"             -Recurse -Force
Copy-Item "$wt\LoadFolders.xml" "$game\Mods\TurboTick\"                      -Force -ErrorAction SilentlyContinue
Copy-Item "$wt\1.7\Assemblies\*.dll" "$game\Mods\TurboTick\1.7\Assemblies\" -Force
Say "     树文件 $((Get-ChildItem $game -File).Count) 个"

Say "3/6 预热 profile（首启会写默认 ModsConfig，所以先跑 $WarmupSeconds 秒再覆盖）"
foreach ($p in @("$Lab\server-profile", "$Lab\client-profile")) {
    # 关键：游戏读的是 <profile>\Config\ModsConfig.xml，不是 profile 根目录。写在根目录等于没写，
    # 游戏会退回「只启用 Core」，于是 mod 不加载、-turbotick-server 那条入口形同不存在。
    New-Item -ItemType Directory -Path "$p\Config" -Force | Out-Null
    $modsConfig | Set-Content "$p\Config\ModsConfig.xml" -Encoding utf8
}
$env:TURBOTICK_HOST = '127.0.0.1'
$env:TURBOTICK_PORT = "$Port"
$env:TURBOTICK_DATA = "$Lab\state"
$env:HOME = "$Lab\server-profile"
$env:XDG_CONFIG_HOME = "$Lab\server-profile\.config"
$env:TURBOTICK_PROBE_BUILDING = '1'
$env:TURBOTICK_PROBE_INJURY = if ($NoInjury) { '0' } else { '1' }
# 蓝图探针：权威端自己造蓝图，副本能不能拿到是唯一要回答的问题。
# 不开它就只能等真人点一下，而这条缺陷恰恰是「真人点了、权威端也有、副本没有」。
$env:TURBOTICK_PROBE_BLUEPRINT = '200'
$env:TURBOTICK_PROBE_BLUEPRINT_GAP = '150'
# 配方探针：实验局没有工作台，`bill-add` 会以 "no workbench with a bill stack on this map" 跳过，
# 于是 `配方接线`/`队列目标` 两个读数恒为 0——那个 0 什么也证明不了。权威端自己摆一张石匠台 +
# 石料 + 一条 Forever 配方，剩下的交给 `WorkGiver_DoBill`，这样被测的正是 giver 填队列的那段代码。
$env:TURBOTICK_PROBE_BILL = '400'
$env:TURBOTICK_SCRIPT_OPS = '200'
$env:TURBOTICK_SCRIPT_OPS_GAP = '25'
$warm = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
    -ArgumentList @('-headless', '-quicktest', "-savedatafolder=$Lab\server-profile") `
    -RedirectStandardOutput "$Lab\logs\warm-out.log" -RedirectStandardError "$Lab\logs\warm-err.log"
Start-Sleep -Seconds $WarmupSeconds
try { if (-not $warm.HasExited) { $warm.Kill() } } catch { }
Start-Sleep -Seconds 3
foreach ($p in @("$Lab\server-profile", "$Lab\client-profile")) {
    # 关键：游戏读的是 <profile>\Config\ModsConfig.xml，不是 profile 根目录。写在根目录等于没写，
    # 游戏会退回「只启用 Core」，于是 mod 不加载、-turbotick-server 那条入口形同不存在。
    New-Item -ItemType Directory -Path "$p\Config" -Force | Out-Null
    $modsConfig | Set-Content "$p\Config\ModsConfig.xml" -Encoding utf8
}
Say '     profile 已就绪'

Say '4/6 启动权威端'
$srv = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
    -ArgumentList @('-headless', '-turbotick-server', '-quicktest', "-savedatafolder=$Lab\server-profile") `
    -RedirectStandardOutput "$Lab\logs\server-out.log" -RedirectStandardError "$Lab\logs\server-err.log"
Say "     pid=$($srv.Id)"
$deadline = (Get-Date).AddSeconds(180)
$playing = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    if ($srv.HasExited) {
        # 退出时先读状态文件：服务端把真正的理由写在这里，而 stdout 常常是空的。
        $why = "(无状态文件)"
        if (Test-Path $status) { try { $j = Get-Content $status -Raw | ConvertFrom-Json; $why = "$($j.state) / $($j.failure)" } catch { } }
        throw "权威端退出：$why（见 $Lab\logs\server-err.log）"
    }
    $status = "$Lab\state\headless-status.json"
    if (Test-Path $status) {
        try { $j = Get-Content $status -Raw | ConvertFrom-Json } catch { continue }
        if ($j.state -eq 'playing') { $playing = $true; break }
    }
}
if (-not $playing) { throw '权威端 180 秒内没有进入 playing' }
Say '     authoritative playing'

Say '5/6 启动副本（脚本跑一串操作）'
$env:TURBOTICK_SCRIPT_OPS = '150'
$env:TURBOTICK_SCRIPT_OPS_GAP = '40'
$cli = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
    -ArgumentList @('-headless', '-quicktest', "-turbotick-connect=127.0.0.1:$Port", "-savedatafolder=$Lab\client-profile") `
    -RedirectStandardOutput "$Lab\logs\client-out.log" -RedirectStandardError "$Lab\logs\client-err.log"
Say "     pid=$($cli.Id)"
# 多个副本：发布前必须知道 2 个以上时带宽/丢弃/CPU 是什么样，只跑过 1 个副本是个未补齐的缺口。
$extra = @()
for ($i = 2; $i -le $Replicas; $i++) {
    $profile = "$Lab\client-profile-$i"
    New-Item -ItemType Directory -Path "$profile\Config" -Force | Out-Null
    $modsConfig | Set-Content "$profile\Config\ModsConfig.xml" -Encoding utf8
    $extra += Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
        -ArgumentList @('-headless', '-quicktest', "-turbotick-connect=127.0.0.1:$Port", "-savedatafolder=$profile") `
        -RedirectStandardOutput "$Lab\logs\client-$i-out.log" -RedirectStandardError "$Lab\logs\client-$i-err.log"
    Say "     副本 $i pid=$($extra[-1].Id)"
}
Say "     观察 $RunSeconds 秒（$Replicas 个副本）"
Start-Sleep -Seconds $RunSeconds

Say '6/6 判定'
# 先把进程停掉再读日志：子进程被强杀时，.NET 控制台的 4 KB 缓冲不会落盘，读到的是半截内容。
# 实测：上一次运行日志停在启动后第 16 秒，而进程还在跑——不是没输出，是没刷出来。
try { if (-not $cli.HasExited) { $cli.Kill() } } catch { }
foreach ($p in $extra) { try { if (-not $p.HasExited) { $p.Kill() } } catch { } }
try { if (-not $srv.HasExited) { $srv.Kill() } } catch { }
Start-Sleep -Seconds 4
$replicaMode = Read-LogLines "$Lab\logs\client-out.log" '权威模式|首个权威帧' 4
$engineTicks = Read-LogLines "$Lab\logs\client-out.log" '引擎tick' 6
$ops         = Read-LogLines "$Lab\logs\client-out.log" 'TurboTick ops' 6
$authority   = Read-LogLines "$Lab\logs\server-out.log" '权威帧|共享殖民地已开始' 4

Write-Host ''
Write-Host '---- 副本模式 ----'
if ($replicaMode.Count) { $replicaMode | ForEach-Object { $_ } } else { Write-Host '（没有找到「权威模式」行：说明它可能仍在自动连接/加载）' }
Write-Host ''
Write-Host '---- 副本引擎 tick（关键：应当始终不变）----'
if ($engineTicks.Count) { $engineTicks | ForEach-Object { $_ } } else { Write-Host '（没有引擎tick采样，见 findings.md 里该探针的开关）' }
Write-Host ''
Write-Host '---- 副本脚本操作 ----'
if ($ops.Count) { $ops | ForEach-Object { $_ } } else { Write-Host '（没有操作记录）' }
Write-Host ''
Write-Host '---- 权威端 ----'
if ($authority.Count) { $authority | ForEach-Object { $_ } } else { Write-Host '（权威端没有输出，见 server-out.log）' }

$values = @()
foreach ($line in $engineTicks) {
    if ($line -match '引擎tick\s*=\s*(\d+)') { $values += [int]$Matches[1] }
}
Write-Host ''
if ($values.Count -ge 2) {
    $distinct = ($values | Select-Object -Unique).Count
    if ($distinct -eq 1) {
        Write-Host "判定：副本引擎 tick 恒为 $($values[0])（$($values.Count) 次采样）→ 真·副本，一步都没跑。" -ForegroundColor Green
    } else {
        Write-Host "判定：副本引擎 tick 在变化（$($values -join ' → ')）→ 它仍在本地模拟，不是副本。" -ForegroundColor Red
    }
} else {
    Write-Host '判定：采样不足，无法判断。' -ForegroundColor Yellow
}

Say '收工'
try { if (-not $cli.HasExited) { $cli.Kill() } } catch { }
foreach ($p in $extra) { try { if (-not $p.HasExited) { $p.Kill() } } catch { } }
try { if (-not $srv.HasExited) { $srv.Kill() } } catch { }
Start-Sleep -Seconds 3
Stop-LabProcesses
if (-not $KeepLab) {
    # 同一个坑的第二处：收尾也按重解析点逐个摘链接，否则 "The directory is not empty." 会盖住判定结果。
    if (Test-Path $game) {
        Get-ChildItem $game -Force | ForEach-Object {
            if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                cmd /c "rmdir `"$($_.FullName)`"" 2>$null | Out-Null
            } else {
                Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        Remove-Item $game -Recurse -Force -ErrorAction SilentlyContinue
    }
    Say "已清理游戏树（日志与 profile 保留在 $Lab）"
} else {
    Say "保留 $Lab 供检查"
}









