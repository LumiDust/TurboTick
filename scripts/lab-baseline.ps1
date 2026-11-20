# 基线下发实验台：权威端加载真实存档，客户端 profile **没有任何存档**也要连上并自动加载。
#
# 判据（每轮都打印）：
#   A 客户端连接前 Saves 目录里 0 个 .rws
#   B 日志出现基线下发（分块数 / 字节数 / sha256）
#   C 校验通过 + 开始加载 LoadGame
#   D WorldReady（服务端收到后才开帧）
#   E 收到首帧、64 个角色、toil 一致、引擎 tick 恒定（副本不 tick）
#   F 断开重连再来一遍
#
# 历史坑（照 lab-headless.ps1）：两段式 profile 预热、GBK 读日志、先杀进程等 4 秒、换构建清 state。
param(
    [int]$Port = 30596,
    [string]$Lab = 'E:\dsh\workspace\rimworld\_lab\base',
    [string]$SaveName = 'realtest',
    [string]$SaveSource = "$env:USERPROFILE\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Saves\0b424510df98c64dd5598a7b1c2c6bff7c693c8d25edc824a558e5e7e4cc2393.rws",
    [int]$WarmupSeconds = 40,
    [int]$RunSeconds = 150,
    [int]$LoadTimeoutSeconds = 240,
    [switch]$SkipBuild,
    [switch]$SkipWarmup,
    [switch]$SkipReconnect,
    [switch]$KeepLab
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
$env:DOTNET_ROOT = 'E:\dsh\workspace\rimworld\tools\dotnet10'

function Say($m) { Write-Host "[$(Get-Date -Format HH:mm:ss)] $m" }

function Stop-LabProcesses {
    Get-CimInstance Win32_Process -Filter "Name='RimWorld.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and ($_.CommandLine -like "*$Lab*") } |
        ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force; Say "killed $($_.ProcessId)" } catch { } }
}

function Read-GbkTail([string]$path, [int]$bytes = 600000) {
    if (-not (Test-Path $path)) { return '' }
    $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    try {
        $len = $fs.Length
        if ($len -gt $bytes) { [void]$fs.Seek($len - $bytes, 'Begin') }
        $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::GetEncoding(936))
        return $sr.ReadToEnd()
    } finally { $fs.Close() }
}

function Select-GbkLines([string]$path, [string]$pattern, [int]$count = 8, [switch]$FromAll) {
    if (-not (Test-Path $path)) { return @() }
    $bytes = if ($FromAll) { 64MB } else { 600000 }
    $text = Read-GbkTail $path $bytes
    if (-not $text) { return @() }
    $hits = @($text -split "`r?`n" | Where-Object { $_ -match $pattern })
    if ($hits.Count -le $count) { return $hits }
    return $hits[($hits.Count - $count)..($hits.Count - 1)]
}

function Wait-Marker([string]$path, [string]$pattern, [int]$timeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $hit = Select-GbkLines $path $pattern 2
        if ($hit.Count) { return $hit }
        Start-Sleep -Seconds 2
    }
    return @()
}

function Show-Section($title, $lines, $fallback = '（无匹配行）') {
    Write-Host ''
    Write-Host "---- $title ----"
    if ($lines -and $lines.Count) { $lines | ForEach-Object { if ($_.Length -gt 300) { Write-Host ($_.Substring(0, 300) + ' …') } else { Write-Host $_ } } } else { Write-Host $fallback }
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

Say "0/8 清理（$Lab）"
Stop-LabProcesses
Start-Sleep -Seconds 3
if (Test-Path $state) { Remove-Item $state -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $state, $logs -Force | Out-Null

if (-not $SkipBuild) {
    Say '1/8 构建'
    Push-Location $wt
    & $dn build Source\TurboTick.csproj -c Release /p:RimWorldDir=E:\dsh\workspace\rimworld\tools\ref17-5005 -v q
    $code = $LASTEXITCODE
    Pop-Location
    if ($code -ne 0) { throw '构建失败' }
} else { Say '1/8 构建（跳过）' }
$dll = "$wt\1.7\Assemblies\TurboTick.dll"
if (-not (Test-Path $dll)) { throw "缺少构建产物：$dll" }
Say ("     dll " + (Get-FileHash $dll -Algorithm SHA256).Hash.Substring(0, 16) + "  " + (Get-Item $dll).LastWriteTime)

Say '2/8 私有游戏树'
if (-not (Test-Path "$game\RimWorld.exe")) {
    New-Item -ItemType Directory -Path $game -Force | Out-Null
    Get-ChildItem $src -Directory | Where-Object Name -ne 'Mods' | ForEach-Object {
        cmd /c "mklink /J `"$game\$($_.Name)`" `"$($_.FullName)`"" 2>$null | Out-Null
    }
    Get-ChildItem $src -File | ForEach-Object {
        cmd /c "mklink /H `"$game\$($_.Name)`" `"$($_.FullName)`"" 2>$null | Out-Null
    }
    # 本机 steam_api64.dll 是蒸汽桩，加载器 version.dll / Juij_Steam.dll 只在旧实验树里；
    # 三件套缺一个，进程就会静默 exit=1（stdout/stderr 全空）。
    # 必须先删再拷：上面那轮 `mklink /H` 已经把 steam_api64.dll 硬链到真实安装上，而真实安装的那份
    # 正被玩家自己的 RimWorld 映射着（实测 pid 活着的状态下 Copy-Item -Force 直接
    # "The process cannot access the file ... because it is being used by another process"）——
    # 硬链的两个名字共享同一份数据，所以连「拷到实验树」都会被那个进程挡住。
    foreach ($f in @('steam_api64.dll', 'version.dll', 'Juij_Steam.dll', 'Juij_Steam.json')) {
        if (-not (Test-Path "$steamDonor\$f")) { Say "     缺少 $f（$steamDonor）"; continue }
        Remove-Item "$game\$f" -Force -ErrorAction SilentlyContinue
        Copy-Item "$steamDonor\$f" "$game\$f" -Force
    }
} else { Say '     树已存在，复用' }
New-Item -ItemType Directory -Path "$game\Mods\TurboTick\1.7\Assemblies" -Force | Out-Null
if (-not (Test-Path "$game\Mods\Harmony")) { cmd /c "mklink /J `"$game\Mods\Harmony`" `"$src\Mods\Harmony`"" 2>$null | Out-Null }
Copy-Item "$wt\About"           "$game\Mods\TurboTick\About"     -Recurse -Force
Copy-Item "$wt\Languages"       "$game\Mods\TurboTick\Languages" -Recurse -Force
Copy-Item "$wt\LoadFolders.xml" "$game\Mods\TurboTick\"          -Force -ErrorAction SilentlyContinue
Copy-Item "$wt\1.7\Assemblies\*.dll" "$game\Mods\TurboTick\1.7\Assemblies\" -Force

Say '3/8 profile（两段式）+ 权威存档'
foreach ($p in @($serverProfile, $clientProfile)) { New-Item -ItemType Directory -Path "$p\Config" -Force | Out-Null }
if (-not $SkipWarmup) {
    foreach ($p in @($serverProfile, $clientProfile)) {
        New-Item -ItemType Directory -Path "$p\Config" -Force | Out-Null
        $modsConfig | Set-Content "$p\Config\ModsConfig.xml" -Encoding utf8
    }
    $env:TURBOTICK_HOST = '127.0.0.1'; $env:TURBOTICK_PORT = "$Port"; $env:TURBOTICK_DATA = "$state"
    $warm = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
        -ArgumentList @('-headless', '-quicktest', "-savedatafolder=$serverProfile") `
        -RedirectStandardOutput "$logs\warm-server-out.log" -RedirectStandardError "$logs\warm-server-err.log"
    Start-Sleep -Seconds $WarmupSeconds
    try { if (-not $warm.HasExited) { $warm.Kill() } } catch { }
    Start-Sleep -Seconds 3
    $warm2 = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
        -ArgumentList @('-headless', '-quicktest', "-savedatafolder=$clientProfile") `
        -RedirectStandardOutput "$logs\warm-client-out.log" -RedirectStandardError "$logs\warm-client-err.log"
    Start-Sleep -Seconds $WarmupSeconds
    try { if (-not $warm2.HasExited) { $warm2.Kill() } } catch { }
    Start-Sleep -Seconds 3
}
foreach ($p in @($serverProfile, $clientProfile)) {
    New-Item -ItemType Directory -Path "$p\Config" -Force | Out-Null
    $modsConfig | Set-Content "$p\Config\ModsConfig.xml" -Encoding utf8
}
New-Item -ItemType Directory -Path "$serverProfile\Saves" -Force | Out-Null
Copy-Item $SaveSource "$serverProfile\Saves\$SaveName.rws" -Force
$saveSize = (Get-Item "$serverProfile\Saves\$SaveName.rws").Length
Say ("     权威存档 $SaveName.rws " + [math]::Round($saveSize / 1MB, 2) + ' MB')
# 判据 A：客户端 Saves 必须干净。反复跑了多轮之后这里会留下上一次的副本，必须显式清掉。
if (Test-Path "$clientProfile\Saves") { Remove-Item "$clientProfile\Saves" -Recurse -Force }
Say ("     客户端 Saves 预置存档数 = " + @(Get-ChildItem "$clientProfile\Saves" -Filter *.rws -ErrorAction SilentlyContinue).Count)

Say '4/8 启动权威端（真实存档）'
$env:TURBOTICK_HOST = '127.0.0.1'
$env:TURBOTICK_PORT = "$Port"
$env:TURBOTICK_DATA = "$state"
Remove-Item Env:TURBOTICK_PROBE_BUILDING -ErrorAction SilentlyContinue
Remove-Item Env:TURBOTICK_PROBE_INJURY -ErrorAction SilentlyContinue
Remove-Item Env:TURBOTICK_SCRIPT_OPS -ErrorAction SilentlyContinue
$srvOut = "$logs\server-out.log"; $srvErr = "$logs\server-err.log"
$srv = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
    -ArgumentList @('-headless', '-turbotick-server', "-turbotick-save=$SaveName", "-savedatafolder=$serverProfile") `
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
Show-Section '权威端：世界发布' (Select-GbkLines $srvOut '共享殖民地已开始|initial world|权威帧' 4)

function Start-Client([string]$label, [string]$profile, [string]$outName) {
    $env:TURBOTICK_HOST = '127.0.0.1'
    $env:TURBOTICK_PORT = "$Port"
    $env:TURBOTICK_SCRIPT_OPS = '80'
    $env:TURBOTICK_SCRIPT_OPS_GAP = '40'
    $out = "$logs\$outName-out.log"; $err = "$logs\$outName-err.log"
    Remove-Item $out, $err -ErrorAction SilentlyContinue
    $p = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
        -ArgumentList @('-headless', '-quicktest', "-turbotick-connect=127.0.0.1:$Port", "-savedatafolder=$profile") `
        -RedirectStandardOutput $out -RedirectStandardError $err
    Say "     $label pid=$($p.Id) 日志=$out"
    return @{ Process = $p; Out = $out; Err = $err }
}

function Judge-Client([string]$label, $client) {
    Write-Host ''
    Say "==== 判定 $label ===="
    $baseline = Wait-Marker $client.Out '基线下发|SaveStart|SaveChunk|收到存档|分块' $LoadTimeoutSeconds
    Show-Section "$label 基线下发" $baseline '（没有基线下发行：说明服务端没有推基线，或客户端没认）'
    $verify = Select-GbkLines $client.Out 'check|sha256|校验|分块集齐|字节' 6
    Show-Section "$label 校验" $verify
    $load = Select-GbkLines $client.Out '开始加载|LoadGame|正在载入|加载完成|世界已载入' 6
    Show-Section "$label 加载" $load
    $ready = Select-GbkLines $client.Out 'WorldReady|世界就绪' 4
    Show-Section "$label WorldReady" $ready
    $firstFrame = Select-GbkLines $client.Out '首个权威帧|权威帧 ' 3
    Show-Section "$label 首帧/权威帧" $firstFrame
    $engine = Select-GbkLines $client.Out '引擎tick' 6
    Show-Section "$label 引擎tick（应恒定）" $engine
    $toil = Select-GbkLines $client.Out 'toil 一致' 3
    Show-Section "$label toil" $toil
    $errors = Select-GbkLines $client.Out 'Faulted|载入共享存档失败|基线.*失败|sha256 不符|Error' 6
    Show-Section "$label 错误行" $errors '（无）'
    $values = @()
    foreach ($line in $engine) { if ($line -match '引擎tick\s*=\s*(\d+)') { $values += [int]$Matches[1] } }
    $distinct = if ($values.Count) { ($values | Select-Object -Unique).Count } else { 0 }
    return [pscustomobject]@{
        Baseline = ($baseline.Count -gt 0)
        Verified = ($verify | Where-Object { $_ -match '校验通过|集齐|sha256 一致|sha256=' }).Count -gt 0
        Loaded   = ($load | Where-Object { $_ -match '开始加载|LoadGame' }).Count -gt 0
        Ready    = ($ready.Count -gt 0)
        Frames   = ($firstFrame.Count -gt 0)
        EngineConstant = ($values.Count -ge 2 -and $distinct -eq 1)
        EngineValues = ($values -join ',')
        Toil = ($toil -join ' | ')
    }
}

Say '5/8 启动客户端 1（Saves 为空）'
$before = @(Get-ChildItem "$clientProfile\Saves" -Filter *.rws -ErrorAction SilentlyContinue).Count
$c1 = Start-Client '客户端1' $clientProfile 'client'
$r1 = Judge-Client '客户端1' $c1
Say "     观察 $RunSeconds 秒"
Start-Sleep -Seconds $RunSeconds
$r1 = Judge-Client '客户端1（末尾）' $c1
try { if (-not $c1.Process.HasExited) { $c1.Process.Kill() } } catch { }
Start-Sleep -Seconds 4

$r2 = $null
if (-not $SkipReconnect) {
    Say '6/8 重连（同一 profile，断开后重走一遍）'
    $c2 = Start-Client '客户端2' $clientProfile 'client2'
    $r2 = Judge-Client '客户端2' $c2
    # 引擎 tick 恒定这个判据需要 ≥2 次采样：只观察一半时长时 reconnect 那一趟只有一行，
    # 会得到「采样不足 → 判 False」的假红。所以第二趟也观察满 $RunSeconds。
    Start-Sleep -Seconds $RunSeconds
    $r2 = Judge-Client '客户端2（末尾）' $c2
    try { if (-not $c2.Process.HasExited) { $c2.Process.Kill() } } catch { }
    Start-Sleep -Seconds 4
}

Say '7/8 服务端日志'
Show-Section '服务端：基线/WorldReady/帧' (Select-GbkLines $srvOut '基线|WorldReady|世界就绪|权威帧|catch-up|拒绝|timeout' 12)
Show-Section '服务端：错误' (Select-GbkLines $srvOut '错误|失败|Exception|rejected' 6) '（无）'

Say '8/8 结论'
Write-Host "客户端连接前预置存档数 = $before（必须 0）"
Write-Host "客户端1：基线=$($r1.Baseline) 校验=$($r1.Verified) 加载=$($r1.Loaded) WorldReady=$($r1.Ready) 收帧=$($r1.Frames) 引擎tick恒定=$($r1.EngineConstant) [$($r1.EngineValues)]"
if ($r2) { Write-Host "客户端2：基线=$($r2.Baseline) 校验=$($r2.Verified) 加载=$($r2.Loaded) WorldReady=$($r2.Ready) 收帧=$($r2.Frames) 引擎tick恒定=$($r2.EngineConstant) [$($r2.EngineValues)]" }
$ok = $r1.Baseline -and $r1.Verified -and $r1.Loaded -and $r1.Ready -and $r1.Frames -and $r1.EngineConstant -and $before -eq 0
if ($r2) { $ok = $ok -and $r2.Baseline -and $r2.Ready -and $r2.Frames -and $r2.EngineConstant }
if ($ok) { Write-Host '判定：通过' -ForegroundColor Green } else { Write-Host '判定：未通过' -ForegroundColor Red }

try { if (-not $srv.HasExited) { $srv.Kill() } } catch { }
Start-Sleep -Seconds 3
if (-not $KeepLab) { Say "保留 $Lab（日志与 profile 供复查）" }
