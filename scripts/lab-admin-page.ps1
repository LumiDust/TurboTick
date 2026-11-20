# 本地管理页取证实验台（headless 权威端）
#
# 两趟，互相不能替代：
#   A 趟：不设 TURBOTICK_ADMIN_PASSWORD → 页面可读，所有写操作必须被拒（403 admin-disabled）
#   B 趟：设密码 → 错 token 401，对 token 能保存/重读设置/退出；期间挂一个副本，
#          全程按页面节奏轮询 /api/status，副本的「引擎tick」必须仍然恒定（页不能影响模拟）
#
# 只用私有树与私有 profile，端口 30594/30595（生产 30572/30573 不碰）。

param(
    [int]$Port = 30594,
    [string]$Lab = 'E:\dsh\workspace\rimworld\_lab\admin',
    [int]$WarmupSeconds = 40,
    [int]$RunSeconds = 90,
    [switch]$SkipBuild,
    [switch]$KeepLab
)

$ErrorActionPreference = 'Stop'
$wt   = 'E:\dsh\workspace\rimworld\TurboTick-headless'
$src  = 'C:\SteamLibrary\steamapps\common\RimWorld'
$dn   = 'E:\dsh\workspace\rimworld\tools\dotnet10\dotnet.exe'
$game = "$Lab\game"
$env:DOTNET_ROOT = 'E:\dsh\workspace\rimworld\tools\dotnet10'
$adminPort = $Port + 1
$base = "http://127.0.0.1:$adminPort"
$statusFile = "$Lab\state\headless-status.json"
$checks = New-Object System.Collections.ArrayList

function Say($m) { Write-Host "[$(Get-Date -Format HH:mm:ss)] $m" }
function Check($name, $ok, $detail) {
    [void]$checks.Add([pscustomobject]@{ Name = $name; Ok = [bool]$ok; Detail = $detail })
    $colour = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  {0} {1} {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail) -ForegroundColor $colour
}

function Stop-LabProcesses {
    Get-CimInstance Win32_Process -Filter "Name='RimWorld.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and ($_.CommandLine -like "*$Lab*" -or $_.CommandLine -like "*:$Port*") } |
        ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force; Say "  killed $($_.ProcessId)" } catch { } }
    Start-Sleep -Seconds 2
}

function Reset-State {
    if (Test-Path "$Lab\state") { Remove-Item "$Lab\state" -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Path "$Lab\state" -Force | Out-Null
}

function Read-GbkLog($file, $pattern, $count) {
    if (-not (Test-Path $file)) { return @() }
    $fs = [IO.File]::Open($file, 'Open', 'Read', 'ReadWrite')
    $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::GetEncoding(936))
    $hits = New-Object System.Collections.ArrayList
    while ($null -ne ($l = $sr.ReadLine())) { if ($l -match $pattern) { [void]$hits.Add($l.Trim()) } }
    $sr.Close(); $fs.Close()
    if ($hits.Count -eq 0) { return @() }
    if ($hits.Count -le $count) { return @($hits.ToArray()) }
    return @($hits.ToArray()[($hits.Count - $count)..($hits.Count - 1)])
}

$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromSeconds(150)

function Invoke-Page {
    param([string]$Method, [string]$Path, [hashtable]$Headers = @{}, [string]$Body = $null)
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), ($base + $Path))
    foreach ($key in $Headers.Keys) { [void]$request.Headers.TryAddWithoutValidation($key, $Headers[$key]) }
    if ($null -ne $Body) { $request.Content = [System.Net.Http.StringContent]::new($Body, [Text.Encoding]::UTF8, 'application/json') }
    try {
        $response = $http.SendAsync($request).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $text }
    } catch {
        return [pscustomobject]@{ Status = 0; Body = $_.Exception.Message }
    }
}

function Wait-Playing($process, [int]$seconds = 180) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        if ($process.HasExited) {
            $why = '(无状态文件)'
            if (Test-Path $statusFile) { try { $j = Get-Content $statusFile -Raw | ConvertFrom-Json; $why = "$($j.state) / $($j.failure)" } catch { } }
            throw "权威端退出：$why"
        }
        if (Test-Path $statusFile) {
            try { $j = Get-Content $statusFile -Raw | ConvertFrom-Json } catch { continue }
            if ($j.state -eq 'playing') { return $j }
        }
    }
    throw "权威端 $seconds 秒内没有进入 playing"
}

function Json-Field($body, [string]$name) {
    try { return ($body | ConvertFrom-Json).$name } catch { return $null }
}

function Start-Authority([string]$tag) {
    # 同一台机器上别的实验实例会与本进程抢 Player.log 与蒸汽桩，抢到的那个静默退出。
    # 起之前先等一个空窗（最多 150 秒），并把等待写进日志——否则"起不来"会被误读成代码问题。
    for ($i = 0; $i -lt 30; $i++) {
        $others = @(Get-CimInstance Win32_Process -Filter "Name='RimWorld.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -and ($_.CommandLine -notlike "*$Lab*") })
        if ($others.Count -eq 0) { if ($i -gt 0) { Say "     空窗已到（等了 $($i * 5) 秒）" }; break }
        if ($i -eq 0) { Say "     等待空窗：另有 $($others.Count) 个 RimWorld 实例在跑" }
        Start-Sleep -Seconds 5
    }
    return Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
        -ArgumentList @('-headless', '-turbotick-server', '-quicktest', "-savedatafolder=$Lab\server-profile",
            "-turbotick-port=$Port", "-turbotick-host=127.0.0.1", "-turbotick-data=$Lab\state") `
        -RedirectStandardOutput "$Lab\logs\$tag-out.log" -RedirectStandardError "$Lab\logs\$tag-err.log"
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

Say '0/8 清理上一轮'
Stop-LabProcesses
Reset-State

if (-not $SkipBuild) {
    Say '1/8 构建'
    Push-Location $wt
    & $dn build Source\TurboTick.csproj -c Release /p:RimWorldDir=E:\dsh\workspace\rimworld\tools\ref17-5005 -v q
    Pop-Location
    if ($LASTEXITCODE -ne 0) { throw '构建失败' }
}
$dll = "$wt\1.7\Assemblies\TurboTick.dll"
Say ("     dll " + (Get-FileHash $dll -Algorithm SHA256).Hash.Substring(0, 16) + "  " + (Get-Item $dll).LastWriteTime)

Say '2/8 私有游戏树 + profile'
if (Test-Path $game) { cmd /c "rmdir `"$game`"" 2>$null | Out-Null }
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
Copy-Item "$wt\About" "$game\Mods\TurboTick\About" -Recurse -Force
Copy-Item "$wt\Languages" "$game\Mods\TurboTick\Languages" -Recurse -Force
Copy-Item "$wt\LoadFolders.xml" "$game\Mods\TurboTick\" -Force -ErrorAction SilentlyContinue
Copy-Item "$wt\1.7\Assemblies\*.dll" "$game\Mods\TurboTick\1.7\Assemblies\" -Force

# 本机安装的 steam_api64.dll 已被替换成蒸汽桩（5.7 MB），而加载器 version.dll / Juij_Steam.dll
# 只在别人的实验树里存在。缺了它们游戏会**静默退出 exit=1、stdout/stderr 全空**，
# 与「mod 启动失败」完全无法区分——这是本轮真实踩到的坑，所以显式补齐并记录来源。
$emulatorSources = @($src, 'E:\dsh\workspace\rimworld\_lab\dlcverify\game', 'E:\dsh\workspace\rimworld\_lab\headless\game')
foreach ($name in @('version.dll', 'Juij_Steam.dll', 'Juij_Steam.json', 'steam_appid.txt')) {
    $target = Join-Path $game $name
    if (Test-Path $target) { continue }
    foreach ($source in $emulatorSources) {
        $candidate = Join-Path $source $name
        if (Test-Path $candidate) { Copy-Item $candidate $target -Force; break }
    }
}
$missingEmulator = @('version.dll', 'Juij_Steam.dll') | Where-Object { -not (Test-Path (Join-Path $game $_)) }
if ($missingEmulator.Count -gt 0) {
    Write-Warning ("缺少蒸汽桩加载器：{0}；游戏会静默退出，请先补到 {1}" -f ($missingEmulator -join ','), $game)
}

Say "3/8 预热 profile（$WarmupSeconds 秒；首启会写默认 ModsConfig）"
foreach ($p in @("$Lab\server-profile", "$Lab\client-profile")) {
    New-Item -ItemType Directory -Path "$p\Config" -Force | Out-Null
    $modsConfig | Set-Content "$p\Config\ModsConfig.xml" -Encoding utf8
}
$warm = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
    -ArgumentList @('-headless', '-quicktest', "-savedatafolder=$Lab\server-profile") `
    -RedirectStandardOutput "$Lab\logs\warm-out.log" -RedirectStandardError "$Lab\logs\warm-err.log"
Start-Sleep -Seconds $WarmupSeconds
try { if (-not $warm.HasExited) { $warm.Kill() } } catch { }
Start-Sleep -Seconds 3
foreach ($p in @("$Lab\server-profile", "$Lab\client-profile")) {
    New-Item -ItemType Directory -Path "$p\Config" -Force | Out-Null
    $modsConfig | Set-Content "$p\Config\ModsConfig.xml" -Encoding utf8
}
Say '     profile 就绪'
# 上一轮写过的设置文件会被游戏在启动时读进来，于是本轮"改成 False"就变成"没有变化"——一次假阴性。
# 每轮从缺省设置开始，写入才会有差异。
$settingsPath = "$Lab\server-profile\Config\Mod_TurboTick_TurboTickMod.xml"
Remove-Item $settingsPath -Force -ErrorAction SilentlyContinue

# ------------------------------------------------------------------ A 趟：没有密码
Say '4/8 A 趟：未设置 TURBOTICK_ADMIN_PASSWORD'
Remove-Item Env:TURBOTICK_ADMIN_PASSWORD -ErrorAction SilentlyContinue
Remove-Item Env:TURBOTICK_ADMIN_PASSWORD_FILE -ErrorAction SilentlyContinue
Reset-State
$serverA = Start-Authority 'a-server'
Say "     权威端 pid=$($serverA.Id)"
$stateA = Wait-Playing $serverA 180
Say "     state=$($stateA.state) authoritative=$($stateA.authoritative)"

Start-Sleep -Seconds 4
$listen = @(Get-NetTCPConnection -LocalPort $adminPort -State Listen -ErrorAction SilentlyContinue)
Check '管理页端口在监听' ($listen.Count -ge 1) ("LocalAddress=" + (($listen | ForEach-Object { $_.LocalAddress }) -join ','))
Check '只绑回环（没有 0.0.0.0）' (($listen | Where-Object { $_.LocalAddress -notin @('127.0.0.1', '::1') }).Count -eq 0) `
    ("监听项=" + $listen.Count)

$statusA = Invoke-Page 'GET' '/api/status'
Check 'GET /api/status 200' ($statusA.Status -eq 200) ("HTTP " + $statusA.Status)
$jsonA = $null
try { $jsonA = $statusA.Body | ConvertFrom-Json } catch { }
Check '状态 JSON 可解析且 ok=true' ($null -ne $jsonA -and $jsonA.ok -eq $true) ''
if ($jsonA) {
    Check '状态里有 state/tick/wire/health/log' `
        ($null -ne $jsonA.state -and $null -ne $jsonA.tick -and $null -ne $jsonA.wire -and $null -ne $jsonA.health -and $jsonA.log.Count -gt 0) `
        ("state=$($jsonA.state) tick=$($jsonA.tick) log=$($jsonA.log.Count) comp=$($jsonA.health.comp)")
    Check '未设密码 → writeEnabled=false' ($jsonA.control.writeEnabled -eq $false) ("mode=" + $jsonA.control.mode)
}
$dir = "$wt\docs"
New-Item -ItemType Directory -Path $dir -Force | Out-Null
[IO.File]::WriteAllText("$dir\admin-status-readonly.json", $statusA.Body, [Text.UTF8Encoding]::new($false))

$page = Invoke-Page 'GET' '/'
Check 'GET / 200 且是 HTML' ($page.Status -eq 200 -and $page.Body -match '<!DOCTYPE html>') ("HTTP " + $page.Status + " bytes=" + $page.Body.Length)
Check '页面自包含（无外部 http 资源）' (-not ($page.Body -match '(src|href)\s*=\s*["'']https?://')) ''
Check '页面说明只读模式' ($page.Body -match 'TURBOTICK_ADMIN_PASSWORD') ''
[IO.File]::WriteAllText("$dir\admin-page-preview.html", $page.Body, [Text.UTF8Encoding]::new($false))

$noToken = Invoke-Page 'POST' '/api/command' @{} '{"command":"save"}'
Check '无 token 写操作被拒（403 admin-disabled）' ($noToken.Status -eq 403 -and $noToken.Body -match 'admin-disabled') ("HTTP " + $noToken.Status + " " + $noToken.Body)
$bogus = Invoke-Page 'POST' '/api/command' @{ 'X-TurboTick-Token' = 'whatever' } '{"command":"quit"}'
Check '任意 token 写操作同样被拒' ($bogus.Status -eq 403) ("HTTP " + $bogus.Status)
$inUrl = Invoke-Page 'POST' '/api/command?token=abc' @{} '{"command":"save"}'
Check 'URL 里带 token 被拒（不解析密码）' ($inUrl.Status -eq 400 -and $inUrl.Body -match 'secret-in-url') ("HTTP " + $inUrl.Status)
$badHost = Invoke-Page 'GET' '/api/status' @{ 'Host' = 'evil.example' }
Check '非回环 Host 被拒（DNS rebinding 防护）' ($badHost.Status -eq 403 -and $badHost.Body -match 'host-not-allowed') ("HTTP " + $badHost.Status)
$noRoute = Invoke-Page 'GET' '/nope'
Check '未知路径 404' ($noRoute.Status -eq 404) ("HTTP " + $noRoute.Status)
$wrongMethod = Invoke-Page 'GET' '/api/command'
Check 'GET /api/command 405' ($wrongMethod.Status -eq 405) ("HTTP " + $wrongMethod.Status)

Stop-LabProcesses
Say '     A 趟结束（进程已停）'

# ------------------------------------------------------------------ B 趟：有密码
Say '5/8 B 趟：设置 TURBOTICK_ADMIN_PASSWORD'
$secret = 'lab-secret-2026'
$env:TURBOTICK_ADMIN_PASSWORD = $secret
Reset-State
$serverB = Start-Authority 'b-server'
Say "     权威端 pid=$($serverB.Id)"
$stateB = Wait-Playing $serverB 180
Say "     state=$($stateB.state) authoritative=$($stateB.authoritative)"

Say '6/8 挂一个副本（页在轮询的同时它必须不 tick）'
$client = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
    -ArgumentList @('-headless', '-quicktest', "-turbotick-connect=127.0.0.1:$Port", "-savedatafolder=$Lab\client-profile") `
    -RedirectStandardOutput "$Lab\logs\b-client-out.log" -RedirectStandardError "$Lab\logs\b-client-err.log"
Say "     副本 pid=$($client.Id)"

# 按页面节奏轮询：每 1.5 秒一次，共 20 次（30 秒）
Say '     按页面节奏轮询 /api/status（每 1.5s × 20）'
$polls = 0; $lastStatus = $null
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 1500
    $r = Invoke-Page 'GET' '/api/status'
    if ($r.Status -eq 200) { $polls++; $lastStatus = $r }
}
Check '页面节奏轮询 20/20 成功' ($polls -eq 20) ("成功 $polls/20")
$jsonB = $null
try { $jsonB = $lastStatus.Body | ConvertFrom-Json } catch { }
Check '有密码 → writeEnabled=true' ($null -ne $jsonB -and $jsonB.control.writeEnabled -eq $true) ''
Check '状态能看到副本与上行字节' ($null -ne $jsonB -and $jsonB.replicas -ge 1 -and $jsonB.wire.outBytes -gt 0) `
    ("replicas=$($jsonB.replicas) players=$($jsonB.players) out=$($jsonB.wire.outBytes)B in=$($jsonB.wire.inBytes)B fps=$($jsonB.fps) tps=$($jsonB.tps)")
# 常驻模拟端与服务端同进程；它读自己那一份帧是 loopback 内的读，只统计服务端侧读才不会被它污染
# （实测污染前 in=63 MB，修正后 inFrames 明显小于 outFrames）。累计值包含一次性世界传输，故只看是否双向有读数。
Check '上下行都有读数（累计，含初始世界传输）' ($jsonB.wire.inBytes -gt 0 -and $jsonB.wire.outBytes -gt 0) `
    ("in=$($jsonB.wire.inBytes)B/$($jsonB.wire.inFrames)帧 out=$($jsonB.wire.outBytes)B/$($jsonB.wire.outFrames)帧")

$wrong = Invoke-Page 'POST' '/api/command' @{ 'X-TurboTick-Token' = 'wrong-secret' } '{"command":"save"}'
Check '错误 token → 401' ($wrong.Status -eq 401 -and $wrong.Body -match 'token-rejected') ("HTTP " + $wrong.Status)

Say '7/8 用正确 token 执行 save / reload-settings'
$save = Invoke-Page 'POST' '/api/command' @{ 'X-TurboTick-Token' = $secret } '{"command":"save"}'
Check 'save 成功（检查点）' ($save.Status -eq 200 -and $save.Body -match '"ok":true') ("HTTP " + $save.Status + " " + $save.Body)

# 只读页面之外，还要证明 reload-settings 真的读了盘：写一份设置文件，改一个热更新项。
$settingsPath = "$Lab\server-profile\Config\Mod_TurboTick_TurboTickMod.xml"
$settingsXml = @'
<?xml version="1.0" encoding="utf-8"?>
<SettingsBlock>
	<ModSettings Class="TurboTick.TurboTickSettings">
		<showPlayerCursors>False</showPlayerCursors>
		<showHud>False</showHud>
	</ModSettings>
</SettingsBlock>
'@
[IO.File]::WriteAllText($settingsPath, $settingsXml, [Text.UTF8Encoding]::new($false))
$reload = Invoke-Page 'POST' '/api/command' @{ 'X-TurboTick-Token' = $secret } '{"command":"reload-settings"}'
Check 'reload-settings 成功且报告了变化的字段' ($reload.Status -eq 200 -and (Json-Field $reload.Body 'detail') -match 'showPlayerCursors') ("HTTP " + $reload.Status + " " + (Json-Field $reload.Body 'detail'))
$reloadAgain = Invoke-Page 'POST' '/api/command' @{ 'X-TurboTick-Token' = $secret } '{"command":"reload-settings"}'
Check '重读幂等（第二次无变化）' ($reloadAgain.Status -eq 200 -and (Json-Field $reloadAgain.Body 'detail') -match '没有变化') ("HTTP " + $reloadAgain.Status + " " + (Json-Field $reloadAgain.Body 'detail'))
Check 'autoRecover 不被热更新覆盖（权威端启动时钉死）' ((Json-Field $reload.Body 'detail') -notmatch 'autoRecover') ''

$bad = Invoke-Page 'POST' '/api/command' @{ 'X-TurboTick-Token' = $secret } '{"command":"rm -rf"}'
Check '未知命令 400' ($bad.Status -eq 400 -and $bad.Body -match 'unsupported-command') ("HTTP " + $bad.Status)

$statusB = Invoke-Page 'GET' '/api/status'
[IO.File]::WriteAllText("$dir\admin-status.json", $statusB.Body, [Text.UTF8Encoding]::new($false))
$jsonB2 = $statusB.Body | ConvertFrom-Json
Say ("     状态：" + $jsonB2.state + " tick=" + $jsonB2.tick + " replicas=" + $jsonB2.replicas `
    + " out=" + $jsonB2.wire.outBytes + "B in=" + $jsonB2.wire.inBytes + "B fps=" + $jsonB2.fps + " tps=" + $jsonB2.tps)

Say "     观察剩余 $RunSeconds 秒（复制页面的轮询）"
$remain = $RunSeconds
while ($remain -gt 0) {
    Start-Sleep -Seconds 3
    [void](Invoke-Page 'GET' '/api/status')
    $remain -= 3
}

Say '8/8 读副本日志（先杀进程等 4 秒：.NET 控制台缓冲）'
try { if (-not $client.HasExited) { $client.Kill() } } catch { }
Start-Sleep -Seconds 4
$engineTicks = Read-GbkLog "$Lab\logs\b-client-out.log" '引擎tick' 8
$values = @()
foreach ($line in $engineTicks) { if ($line -match '引擎tick\s*=\s*(\d+)') { $values += [int]$Matches[1] } }
$distinct = ($values | Select-Object -Unique)
Check '副本进入权威模式' ((Read-GbkLog "$Lab\logs\b-client-out.log" '权威模式|首个权威帧' 3).Count -gt 0) ''
Check '副本引擎 tick 恒定（页与命令没有让它开始模拟）' ($values.Count -ge 2 -and $distinct.Count -eq 1) `
    ($(if ($values.Count) { "tick=$($values[0])，采样 $($values.Count) 次" } else { '没有引擎tick采样' }))
Check '权威端帧仍在广播' ((Read-GbkLog "$Lab\logs\b-server-out.log" '权威帧' 2).Count -gt 0) ''

$quit = Invoke-Page 'POST' '/api/command' @{ 'X-TurboTick-Token' = $secret } '{"command":"quit"}'
Check 'quit 返回 200 且开始优雅退出' ($quit.Status -eq 200 -and $quit.Body -match 'stopping') ("HTTP " + $quit.Status + " " + $quit.Body)
$exited = $false
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    if ($serverB.HasExited) { $exited = $true; break }
}
Check '权威端自行退出（不是被杀）' $exited ''
if ($exited) { Check '退出码 0' ($serverB.ExitCode -eq 0) ("exit=" + $serverB.ExitCode) }
$finalState = $null
if (Test-Path $statusFile) { try { $finalState = Get-Content $statusFile -Raw | ConvertFrom-Json } catch { } }
Check '状态文件记为 stopped' ($null -ne $finalState -and $finalState.state -eq 'stopped') `
    ($(if ($finalState) { "state=" + $finalState.state + " failure=" + $finalState.failure } else { '无状态文件' }))
$quitLog = Read-GbkLog "$Lab\logs\b-server-out.log" '收到退出请求|stopped' 4
Check '日志记录了退出请求' ($quitLog.Count -gt 0) (($quitLog | Select-Object -First 1) -join ' ')

Stop-LabProcesses
Say '收工'
Write-Host ''
Write-Host '================ 汇总 ================'
$failedChecks = @($checks | Where-Object { -not $_.Ok })
foreach ($c in $checks) {
    Write-Host ("{0}  {1}  {2}" -f $(if ($c.Ok) { 'PASS' } else { 'FAIL' }), $c.Name, $c.Detail) `
        -ForegroundColor $(if ($c.Ok) { 'Green' } else { 'Red' })
}
Write-Host ("通过 {0}/{1}" -f ($checks.Count - $failedChecks.Count), $checks.Count)
if (-not $KeepLab) {
    cmd /c "rmdir `"$game`"" 2>$null | Out-Null
    Say "已清理游戏树（日志与 profile 保留在 $Lab）"
}
if ($failedChecks.Count -gt 0) { exit 1 }
