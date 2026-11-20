# 管理台「绑定 / 登录 / 新端点」取证实验台（headless 权威端）
#
# 三趟，互相不能替代：
#   A 趟：-turbotick-admin-bind=0.0.0.0 且**不设** TURBOTICK_ADMIN_PASSWORD
#         → 权威端照常 playing，管理页**拒绝启动**，日志里有 Log.Error 的原因，端口无人监听
#   B 趟：同样绑 0.0.0.0，设密码
#         → 监听在 0.0.0.0；/login 登录页；错密码 401；对密码发 HttpOnly+SameSite=Strict cookie；
#           未带凭据的 /api/status 401；带 cookie 能读；token 头写命令 200；URL 带密码 400；
#           /api/saves、/api/versions、/api/steam/status、/api/server/start 的拒绝路径
#   C 趟：回到默认绑 127.0.0.1、不设密码
#         → 旧行为不能退化：写操作 403 admin-disabled、页面可读
#
# 只用私有树与私有 profile，端口 30596/30597（生产 30572/30573 不碰）。

param(
    [int]$Port = 30598,
    [string]$Lab = 'E:\dsh\workspace\rimworld\_lab\bind',
    [int]$WarmupSeconds = 40,
    [switch]$SkipBuild,
    [switch]$KeepLab
)

$ErrorActionPreference = 'Stop'
$wt  = 'E:\dsh\workspace\rimworld\TurboTick-headless'
$src = 'C:\SteamLibrary\steamapps\common\RimWorld'
$dn  = 'E:\dsh\workspace\rimworld\tools\dotnet10\dotnet.exe'
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
    # .NET 控制台 4 KB 缓冲在强杀时不落盘：先杀再等，才能读到完整日志
    Start-Sleep -Seconds 4
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

$handler = [System.Net.Http.HttpClientHandler]::new()
# 303 必须能被看到：默认 AllowAutoRedirect 会把 /login 的表单登录变成一次 GET / 并返回 200。
$handler.AllowAutoRedirect = $false
$http = [System.Net.Http.HttpClient]::new($handler)
$http.Timeout = [TimeSpan]::FromSeconds(180)
$http.DefaultRequestHeaders.ExpectContinue = $false

function Invoke-Page {
    param([string]$Method, [string]$Path, [hashtable]$Headers = @{}, [string]$Body = $null, [string]$ContentType = 'application/json')
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), ($base + $Path))
    foreach ($key in $Headers.Keys) {
        # Host 是受限头，只能通过属性设置；TryAddWithoutValidation 会静默忽略它。
        if ($key -eq 'Host') { $request.Headers.Host = $Headers[$key] }
        else { [void]$request.Headers.TryAddWithoutValidation($key, $Headers[$key]) }
    }
    if ($null -ne $Body) { $request.Content = [System.Net.Http.StringContent]::new($Body, [Text.Encoding]::UTF8, $ContentType) }
    try {
        $response = $http.SendAsync($request).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $setCookie = ''
        if ($response.Headers.Contains('Set-Cookie')) { $setCookie = ($response.Headers.GetValues('Set-Cookie') -join '; ') }
        return [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $text; SetCookie = $setCookie }
    } catch {
        return [pscustomobject]@{ Status = 0; Body = $_.Exception.Message; SetCookie = '' }
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

function Start-Authority([string]$tag, [string[]]$extra) {
    # 同机别的实验实例（例如 E:\dsh\workspace\rimworld\_lab\base）可能一直开着并占自己的端口；只有**本实验端口**被占才会
    # 让权威端绑定失败。所以这里等的是"本端口空窗"，而不是"机器上没有任何 RimWorld"。
    for ($i = 0; $i -lt 12; $i++) {
        $busy = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
        if ($busy.Count -eq 0) { if ($i -gt 0) { Say "     端口空窗已到（等了 $($i * 5) 秒）" }; break }
        if ($i -eq 0) { Say "     等待端口空窗：$Port 上有 $($busy.Count) 个监听" }
        Start-Sleep -Seconds 5
    }
    $others = @(Get-CimInstance Win32_Process -Filter "Name='RimWorld.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and ($_.CommandLine -notlike "*$Lab*") })
    if ($others.Count -gt 0) { Say "     注意：另有 $($others.Count) 个别的实验实例在跑（不同端口/不同私有树，不影响本趟）" }
    $argList = @('-headless', '-turbotick-server', '-quicktest', "-savedatafolder=$Lab\server-profile",
        "-turbotick-port=$Port", "-turbotick-host=127.0.0.1", "-turbotick-data=$Lab\state") + $extra
    Say ("     args: " + ($argList -join ' '))
    return Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
        -ArgumentList $argList `
        -RedirectStandardOutput "$Lab\logs\$tag-out.log" -RedirectStandardError "$Lab\logs\$tag-err.log"
}

function Listeners([int]$p) {
    @(Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue)
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

Say '0/9 清理上一轮'
Stop-LabProcesses
Reset-State

if (-not $SkipBuild) {
    Say '1/9 构建'
    Push-Location $wt
    & $dn build Source\TurboTick.csproj -c Release /p:RimWorldDir=E:\dsh\workspace\rimworld\tools\ref17-5005 -v q
    Pop-Location
    if ($LASTEXITCODE -ne 0) { throw '构建失败' }
}
$dll = "$wt\1.7\Assemblies\TurboTick.dll"
Say ("     dll " + (Get-FileHash $dll -Algorithm SHA256).Hash.Substring(0, 16) + "  " + (Get-Item $dll).LastWriteTime)

Say '2/9 私有游戏树 + profile'
if (Test-Path $game) { cmd /c "rmdir `"$game`"" 2>$null | Out-Null }
foreach ($d in @("$Lab\logs", "$Lab\state", "$Lab\server-profile", $game)) {
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

# 本机 steam_api64.dll 已被换成蒸汽桩，缺加载器时游戏会静默退出（exit=1、日志全空）。
foreach ($name in @('version.dll', 'Juij_Steam.dll', 'Juij_Steam.json', 'steam_appid.txt')) {
    $target = Join-Path $game $name
    if (Test-Path $target) { continue }
    foreach ($source in @($src, 'E:\dsh\workspace\rimworld\_lab\dlcverify\game', 'E:\dsh\workspace\rimworld\_lab\headless\game', 'E:\dsh\workspace\rimworld\_lab\base\game')) {
        $candidate = Join-Path $source $name
        if (Test-Path $candidate) { Copy-Item $candidate $target -Force; break }
    }
}
$missingEmulator = @('version.dll', 'Juij_Steam.dll') | Where-Object { -not (Test-Path (Join-Path $game $_)) }
if ($missingEmulator.Count -gt 0) { Write-Warning ("缺少蒸汽桩加载器：{0}" -f ($missingEmulator -join ',')) }

Say "3/9 预热 profile（$WarmupSeconds 秒；首启会写默认 ModsConfig）"
New-Item -ItemType Directory -Path "$Lab\server-profile\Config" -Force | Out-Null
$modsConfig | Set-Content "$Lab\server-profile\Config\ModsConfig.xml" -Encoding utf8
$warm = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
    -ArgumentList @('-headless', '-quicktest', "-savedatafolder=$Lab\server-profile") `
    -RedirectStandardOutput "$Lab\logs\warm-out.log" -RedirectStandardError "$Lab\logs\warm-err.log"
Start-Sleep -Seconds $WarmupSeconds
try { if (-not $warm.HasExited) { $warm.Kill() } } catch { }
Start-Sleep -Seconds 4
New-Item -ItemType Directory -Path "$Lab\server-profile\Config" -Force | Out-Null
$modsConfig | Set-Content "$Lab\server-profile\Config\ModsConfig.xml" -Encoding utf8
# 一个假的存档名，用来验证 /api/server/start 的「文件不存在」拒绝路径（不会真的被载入：
# 世界已经在跑，RequestSaveLoad 只会返回 restart-required）。
$saves = "$Lab\server-profile\Saves"
New-Item -ItemType Directory -Path $saves -Force | Out-Null
'placeholder' | Set-Content "$saves\Lab Probe.rws" -Encoding utf8
Remove-Item "$Lab\server-profile\Config\Mod_TurboTick_TurboTickMod.xml" -Force -ErrorAction SilentlyContinue
Say '     profile 就绪'

# ------------------------------------------------------------------ A 趟：绑 0.0.0.0，无密码
Say '4/9 A 趟：-turbotick-admin-bind=0.0.0.0 且没有 TURBOTICK_ADMIN_PASSWORD'
Remove-Item Env:TURBOTICK_ADMIN_PASSWORD -ErrorAction SilentlyContinue
Remove-Item Env:TURBOTICK_ADMIN_PASSWORD_FILE -ErrorAction SilentlyContinue
Reset-State
$serverA = Start-Authority 'a-server' @('-turbotick-admin-bind=0.0.0.0')
Say "     权威端 pid=$($serverA.Id)"
$stateA = Wait-Playing $serverA 180
Say "     state=$($stateA.state) authoritative=$($stateA.authoritative)"
Start-Sleep -Seconds 3
$listenA = Listeners $adminPort
Check 'A: 权威端照常 playing（配置错误不拖垮服务器）' ($stateA.state -eq 'playing') ("state=" + $stateA.state)
Check 'A: 管理页端口没有任何监听' ($listenA.Count -eq 0) ("监听项=" + $listenA.Count + " " + (($listenA | ForEach-Object { $_.LocalAddress }) -join ','))
$refusal = Read-GbkLog "$Lab\logs\a-server-out.log" '拒绝把管理页绑到|TURBOTICK_ADMIN_PASSWORD' 3
Check 'A: 日志里有 Log.Error 说明原因' ($refusal.Count -gt 0) (($refusal -join ' | '))
if ($refusal.Count -gt 0) { [IO.File]::WriteAllText("$Lab\refusal.txt", ($refusal -join "`n"), [Text.UTF8Encoding]::new($false)) }
$probeA = Invoke-Page 'GET' '/api/status'
Check 'A: 端口确实连不上（不是 200）' ($probeA.Status -ne 200) ("HTTP " + $probeA.Status)
Stop-LabProcesses
Say '     A 趟结束（进程已停）'

# ------------------------------------------------------------------ B 趟：绑 0.0.0.0，有密码
Say '5/9 B 趟：绑 0.0.0.0 + TURBOTICK_ADMIN_PASSWORD'
$secret = 'lab-secret-2026'
$env:TURBOTICK_ADMIN_PASSWORD = $secret
Reset-State
$serverB = Start-Authority 'b-server' @('-turbotick-admin-bind=0.0.0.0', '-turbotick-checkpoint-minutes=10')
Say "     权威端 pid=$($serverB.Id)"
$stateB = Wait-Playing $serverB 180
Say "     state=$($stateB.state) authoritative=$($stateB.authoritative)"
Start-Sleep -Seconds 3

$listenB = Listeners $adminPort
Check 'B: 管理页在监听' ($listenB.Count -ge 1) ("LocalAddress=" + (($listenB | ForEach-Object { $_.LocalAddress }) -join ','))
Check 'B: 监听地址是非回环（0.0.0.0 或本机 LAN IP）' `
    (($listenB | Where-Object { $_.LocalAddress -notin @('127.0.0.1', '::1') }).Count -ge 1) `
    ("监听项=" + $listenB.Count)

Say '6/9 B 趟：登录与会话'
$anonStatus = Invoke-Page 'GET' '/api/status'
Check 'B: 未登录读 /api/status 被拒（401）' ($anonStatus.Status -eq 401 -and $anonStatus.Body -match 'token-required|session-expired') ("HTTP " + $anonStatus.Status + " " + $anonStatus.Body)
$loginPage = Invoke-Page 'GET' '/login'
Check 'B: GET /login 200 且是登录表单' ($loginPage.Status -eq 200 -and $loginPage.Body -match 'name="password"' -and $loginPage.Body -match 'action="/login"') ("HTTP " + $loginPage.Status + " bytes=" + $loginPage.Body.Length)
[IO.File]::WriteAllText("$Lab\login.html", $loginPage.Body, [Text.UTF8Encoding]::new($false))
$badLogin = Invoke-Page 'POST' '/login' @{} '{"password":"wrong"}'
Check 'B: 错密码 401' ($badLogin.Status -eq 401 -and $badLogin.Body -match 'password-rejected') ("HTTP " + $badLogin.Status + " " + $badLogin.Body)
$goodLogin = Invoke-Page 'POST' '/login' @{} ('{"password":"' + $secret + '"}')
$cookiePair = ''
if ($goodLogin.SetCookie -match '(turbotick_admin_session=[0-9a-f]+)') { $cookiePair = $Matches[1] }
Check 'B: 对密码 200 且下发 session cookie' ($goodLogin.Status -eq 200 -and $cookiePair.Length -gt 0) ("HTTP " + $goodLogin.Status + " Set-Cookie=" + $goodLogin.SetCookie)
Check 'B: cookie 带 HttpOnly 与 SameSite=Strict' (($goodLogin.SetCookie -match 'HttpOnly') -and ($goodLogin.SetCookie -match 'SameSite=Strict')) $goodLogin.SetCookie
$formLogin = Invoke-Page 'POST' '/login' @{} ('password=' + $secret) 'application/x-www-form-urlencoded'
Check 'B: 表单登录 303 到 /（浏览器路径）' ($formLogin.Status -eq 303) ("HTTP " + $formLogin.Status + " Location 见 Set-Cookie=" + [bool]$formLogin.SetCookie)

$cookieStatus = Invoke-Page 'GET' '/api/status' @{ 'Cookie' = $cookiePair }
$jsonB = $null
try { $jsonB = $cookieStatus.Body | ConvertFrom-Json } catch { }
Check 'B: 带 cookie 读 /api/status 200' ($cookieStatus.Status -eq 200 -and $null -ne $jsonB) ("HTTP " + $cookieStatus.Status)
if ($jsonB) {
    Check 'B: 状态里 state=playing 且 portal.loginRequired=true' ($jsonB.state -eq 'playing' -and $jsonB.portal.loginRequired -eq $true) `
        ("state=$($jsonB.state) bindKind=$($jsonB.portal.bindKind) sessions=$($jsonB.portal.sessions) checkpointMin=$($jsonB.portal.checkpointMinutes)")
    Check 'B: portal 报出绑 0.0.0.0 与检查点分钟数' ($jsonB.portal.bindKind -eq 'External' -and $jsonB.portal.checkpointMinutes -eq 10) `
        ("bindKind=$($jsonB.portal.bindKind) checkpointMinutes=$($jsonB.portal.checkpointMinutes)")
}
[IO.File]::WriteAllText("$wt\docs\admin-status-external.json", $cookieStatus.Body, [Text.UTF8Encoding]::new($false))

Say '7/9 B 趟：写命令与新端点'
$tokenSave = Invoke-Page 'POST' '/api/command' @{ 'X-TurboTick-Token' = $secret } '{"command":"save"}'
Check 'B: token 头写命令 save 200' ($tokenSave.Status -eq 200 -and $tokenSave.Body -match '"ok":true') ("HTTP " + $tokenSave.Status + " " + $tokenSave.Body)
$cookieWriteNoCsrf = Invoke-Page 'POST' '/api/command' @{ 'Cookie' = $cookiePair } '{"command":"save"}'
Check 'B: 光有 cookie 没有 CSRF 头 → 401' ($cookieWriteNoCsrf.Status -eq 401 -and $cookieWriteNoCsrf.Body -match 'csrf-required') ("HTTP " + $cookieWriteNoCsrf.Status + " " + $cookieWriteNoCsrf.Body)
$cookieWrite = Invoke-Page 'POST' '/api/command' @{ 'Cookie' = $cookiePair; 'X-TurboTick-Csrf' = '1' } '{"command":"reload-settings"}'
Check 'B: cookie + CSRF 头写命令 200/503（业务码不是认证失败）' ($cookieWrite.Status -in @(200, 503) -and $cookieWrite.Body -notmatch 'csrf-required|token-required') ("HTTP " + $cookieWrite.Status + " " + $cookieWrite.Body)
$inUrl = Invoke-Page 'POST' '/api/command?token=abc' @{} '{"command":"save"}'
Check 'B: URL 带密码仍是 400 secret-in-url' ($inUrl.Status -eq 400 -and $inUrl.Body -match 'secret-in-url') ("HTTP " + $inUrl.Status + " " + $inUrl.Body)
$badHost = Invoke-Page 'GET' '/api/status' @{ 'Host' = 'evil.example:1234'; 'Cookie' = $cookiePair }
Check 'B: 端口不匹配的 Host 仍被拒 403（防 DNS rebinding 的端口校验）' ($badHost.Status -eq 403 -and $badHost.Body -match 'host-not-allowed') ("HTTP " + $badHost.Status + " " + $badHost.Body)
$ownHost = Invoke-Page 'GET' '/api/status' @{ 'Host' = "127.0.0.1:$adminPort"; 'Cookie' = $cookiePair }
Check 'B: 正常 Host（127.0.0.1:端口）仍可读' ($ownHost.Status -eq 200) ("HTTP " + $ownHost.Status)
$crossOrigin = Invoke-Page 'POST' '/api/command' @{ 'Origin' = 'http://evil.example' } '{"command":"save"}'
Check 'B: 跨源写请求被拒 403' ($crossOrigin.Status -eq 403 -and $crossOrigin.Body -match 'cross-origin') ("HTTP " + $crossOrigin.Status + " " + $crossOrigin.Body)

$saves = Invoke-Page 'GET' '/api/saves' @{ 'Cookie' = $cookiePair }
$savesJson = $null
try { $savesJson = $saves.Body | ConvertFrom-Json } catch { }
Check 'B: GET /api/saves 200 且列出 .rws' ($saves.Status -eq 200 -and $null -ne $savesJson -and $savesJson.count -ge 1) ("HTTP " + $saves.Status + " " + $saves.Body)
$versions = Invoke-Page 'GET' '/api/versions' @{ 'Cookie' = $cookiePair }
Check 'B: GET /api/versions 200 且 1.7 分支为 unstable' ($versions.Status -eq 200 -and $versions.Body -match '"betaBranch": "unstable"') ("HTTP " + $versions.Status + " " + $versions.Body.Substring(0, [Math]::Min(160, $versions.Body.Length)))
$steam = Invoke-Page 'GET' '/api/steam/status' @{ 'Cookie' = $cookiePair }
Check 'B: GET /api/steam/status 200 且 steamcmd 不在本机时 present=false' ($steam.Status -eq 200 -and $steam.Body -match '"present": false') ("HTTP " + $steam.Status + " " + $steam.Body.Substring(0, [Math]::Min(200, $steam.Body.Length)))
$progress = Invoke-Page 'GET' '/api/game/progress' @{ 'Cookie' = $cookiePair }
Check 'B: GET /api/game/progress 200' ($progress.Status -eq 200 -and $progress.Body -match '"ok": true') ("HTTP " + $progress.Status + " " + $progress.Body.Substring(0, [Math]::Min(160, $progress.Body.Length)))

$startMissing = Invoke-Page 'POST' '/api/server/start' @{ 'Cookie' = $cookiePair; 'X-TurboTick-Csrf' = '1' } '{"save":"nope"}'
Check 'B: /api/server/start 不存在的存档 → save-missing' ($startMissing.Status -eq 503 -and $startMissing.Body -match 'save-missing') ("HTTP " + $startMissing.Status + " " + $startMissing.Body)
$startTraversal = Invoke-Page 'POST' '/api/server/start' @{ 'Cookie' = $cookiePair; 'X-TurboTick-Csrf' = '1' } '{"save":"../../escape"}'
Check 'B: /api/server/start 路径穿越被拒（400）' ($startTraversal.Status -eq 400 -and $startTraversal.Body -match 'save-invalid|save-required') ("HTTP " + $startTraversal.Status + " " + $startTraversal.Body)
$startExisting = Invoke-Page 'POST' '/api/server/start' @{ 'Cookie' = $cookiePair; 'X-TurboTick-Csrf' = '1' } '{"save":"Lab Probe"}'
Check 'B: 已在跑别的世界 → restart-required（不热换档）' ($startExisting.Status -eq 503 -and $startExisting.Body -match 'restart-required') ("HTTP " + $startExisting.Status + " " + $startExisting.Body)
$downloadUnknown = Invoke-Page 'POST' '/api/game/download' @{ 'Cookie' = $cookiePair; 'X-TurboTick-Csrf' = '1' } '{"version":"1.8"}'
Check 'B: /api/game/download 未知版本 400' ($downloadUnknown.Status -eq 400 -and $downloadUnknown.Body -match 'unknown-version') ("HTTP " + $downloadUnknown.Status + " " + $downloadUnknown.Body)
$downloadNoLogin = Invoke-Page 'POST' '/api/game/download' @{ 'Cookie' = $cookiePair; 'X-TurboTick-Csrf' = '1' } '{"version":"1.7"}'
Check 'B: /api/game/download 未登录 Steam → 409 download-refused' ($downloadNoLogin.Status -eq 409 -and $downloadNoLogin.Body -match 'download-refused') ("HTTP " + $downloadNoLogin.Status + " " + $downloadNoLogin.Body)
$steamLoginBad = Invoke-Page 'POST' '/api/steam/login' @{ 'Cookie' = $cookiePair; 'X-TurboTick-Csrf' = '1' } '{"username":"no space","password":"x"}'
Check 'B: /api/steam/login 非法账号 409 login-refused' ($steamLoginBad.Status -eq 409 -and $steamLoginBad.Body -match 'login-refused') ("HTTP " + $steamLoginBad.Status + " " + $steamLoginBad.Body)
$steamLoginNoPw = Invoke-Page 'POST' '/api/steam/login' @{ 'Cookie' = $cookiePair; 'X-TurboTick-Csrf' = '1' } '{"username":"someone"}'
Check 'B: /api/steam/login 缺密码 400' ($steamLoginNoPw.Status -eq 400 -and $steamLoginNoPw.Body -match 'password-required') ("HTTP " + $steamLoginNoPw.Status + " " + $steamLoginNoPw.Body)

Say '8/9 B 趟：注销与优雅退出'
$logout = Invoke-Page 'POST' '/logout' @{ 'Cookie' = $cookiePair }
Check 'B: /logout 200 且清 cookie' ($logout.Status -eq 200 -and $logout.SetCookie -match 'Max-Age=0') ("HTTP " + $logout.Status)
$afterLogout = Invoke-Page 'GET' '/api/status' @{ 'Cookie' = $cookiePair }
Check 'B: 注销后旧 cookie 失效（401）' ($afterLogout.Status -eq 401) ("HTTP " + $afterLogout.Status + " " + $afterLogout.Body)
$quit = Invoke-Page 'POST' '/api/command' @{ 'X-TurboTick-Token' = $secret } '{"command":"quit"}'
Check 'B: 优雅退出命令 200' ($quit.Status -eq 200 -and $quit.Body -match 'stopping') ("HTTP " + $quit.Status + " " + $quit.Body)
$exited = $serverB.WaitForExit(90000)
Check 'B: 进程自行退出（exit 0）' ($exited -and $serverB.ExitCode -eq 0) ("exited=$exited exit=$($serverB.ExitCode)")
if (Test-Path $statusFile) {
    $stopped = Get-Content $statusFile -Raw | ConvertFrom-Json
    Check 'B: headless-status.json 记 state=stopped' ($stopped.state -eq 'stopped') ("state=" + $stopped.state)
}
Stop-LabProcesses

# ------------------------------------------------------------------ C 趟：回环默认，无密码
Say '9/9 C 趟：默认绑 127.0.0.1、无密码（旧行为不能退化）'
Remove-Item Env:TURBOTICK_ADMIN_PASSWORD -ErrorAction SilentlyContinue
Reset-State
$serverC = Start-Authority 'c-server' @()
Say "     权威端 pid=$($serverC.Id)"
$stateC = Wait-Playing $serverC 180
Start-Sleep -Seconds 3
$listenC = Listeners $adminPort
Check 'C: 默认仍只绑回环' (($listenC | Where-Object { $_.LocalAddress -notin @('127.0.0.1', '::1') }).Count -eq 0) ("监听项=" + $listenC.Count + " " + (($listenC | ForEach-Object { $_.LocalAddress }) -join ','))
$readC = Invoke-Page 'GET' '/api/status'
Check 'C: 无密码时状态页仍可读（只读模式）' ($readC.Status -eq 200) ("HTTP " + $readC.Status)
$writeC = Invoke-Page 'POST' '/api/command' @{} '{"command":"save"}'
Check 'C: 无密码写操作 403 admin-disabled（未退化）' ($writeC.Status -eq 403 -and $writeC.Body -match 'admin-disabled') ("HTTP " + $writeC.Status + " " + $writeC.Body)
$loginC = Invoke-Page 'POST' '/login' @{} '{"password":"whatever"}'
Check 'C: 回环 + 无密码时 /login 拒绝（admin-disabled）' ($loginC.Status -eq 403 -and $loginC.Body -match 'admin-disabled') ("HTTP " + $loginC.Status)
Stop-LabProcesses

# ------------------------------------------------------------------ 汇总
$pass = @($checks | Where-Object Ok).Count
$fail = @($checks | Where-Object { -not $_.Ok }).Count
Write-Host ''
Say ("判定：{0}/{1} 通过，{2} 失败" -f $pass, $checks.Count, $fail)
if ($fail -gt 0) { $checks | Where-Object { -not $_.Ok } | ForEach-Object { Write-Host ("  FAIL " + $_.Name + " " + $_.Detail) -ForegroundColor Red } }
if (-not $KeepLab) { Say "私有树保留在 $Lab（-KeepLab 关闭清理）" }
exit $(if ($fail -eq 0) { 0 } else { 1 })
