# 管理台验收脚本（阶段 8）
#
# 两条互补的检查，都需要对着**运行中的**容器：
#   1. HTTP 契约：登录 → /api/status（authenticated）→ 上传/删除/改 mod 的拒绝与成功
#   2. 页面渲染：把服务端真实返回的 HTML + 真实 /api/status 交给 `admin-page-render.test.js`，
#      验证「登录后按钮可用、黄条消失」这条 bug 真的修好了（含未登录的反向对照）
#
# 用法（Windows，已装 node）：
#   .\scripts\check-admin-page.ps1 -Base http://192.168.0.29:30573 -Password xxxxxxxx
#
# 只读部分不需要密码；写操作部分需要。

param(
    [string]$Base = 'http://192.168.0.29:30573',
    [string]$Password = $env:TURBOTICK_ADMIN_PASSWORD,
    [string]$Work = (Join-Path $env:TEMP 'turbotick-admin-check'),
    [switch]$SkipWrites
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $Work -Force | Out-Null
$checks = New-Object System.Collections.ArrayList
function Check($name, $ok, $detail) {
    [void]$checks.Add([pscustomobject]@{ Name = $name; Ok = [bool]$ok; Detail = $detail })
    $colour = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  {0} {1}  {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail) -ForegroundColor $colour
}
function Say($m) { Write-Host "[$(Get-Date -Format HH:mm:ss)] $m" }

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$csrf = @{ 'X-TurboTick-Csrf' = '1' }
$json = @{ 'X-TurboTick-Csrf' = '1'; 'Content-Type' = 'application/json' }

function Get-Or($path) {
    $r = Invoke-WebRequest -Uri "$Base$path" -WebSession $session -TimeoutSec 60 -SkipHttpErrorCheck
    [pscustomobject]@{ Status = [int]$r.StatusCode; Body = $r.Content }
}
function Post-Json($path, $object) {
    $r = Invoke-WebRequest -Uri "$Base$path" -Method Post -WebSession $session -Headers $json `
        -Body ($object | ConvertTo-Json -Compress) -TimeoutSec 120 -SkipHttpErrorCheck
    [pscustomobject]@{ Status = [int]$r.StatusCode; Body = $r.Content }
}

Say '1) 未登录：页面可读，写操作与状态都被拒'
$page = Get-Or '/'
Check 'GET / 200' ($page.Status -eq 200) ("HTTP " + $page.Status + " bytes=" + $page.Body.Length)
Check '页面含全部分区（存档 / DLC / mod / 进程 / 日志）' `
    ($page.Body -match 'id="view-saves"' -and $page.Body -match 'id="view-dlc"' -and $page.Body -match 'id="view-mods"' `
        -and $page.Body -match 'id="view-logs"' -and $page.Body -match '游戏进程' -and $page.Body -match '关闭服务') ''
$anon = Get-Or '/api/status'
Check '未登录 /api/status 401（外部绑定）' ($anon.Status -eq 401) ("HTTP " + $anon.Status)

Say '2) 登录'
$login = Invoke-WebRequest -Uri "$Base/login" -Method Post -WebSession $session -TimeoutSec 30 `
    -Body @{ password = $Password } -ContentType 'application/x-www-form-urlencoded' -MaximumRedirection 5
Check 'POST /login 303 并拿到会话' ($session.Cookies.Count -gt 0) ("cookies=" + $session.Cookies.Count)
$status = (Get-Or '/api/status').Body | ConvertFrom-Json
Check '登录后 /api/status 200' ($true) ''
Check 'process 摘要存在（portal 是独立进程）' `
    ($null -ne $status.process -and $status.process.state -ne 'unknown') ("state=" + $status.process.state + " pid=" + $status.process.pid)
Check 'control.authenticated = true' ($status.control.authenticated -eq $true) ("authenticated=" + $status.control.authenticated)
Check 'DLC 段存在且 5/5' ($status.dlc -and $status.dlc.summary -match '5/5') ($status.dlc.summary)
Check '每个 DLC 都报 onDisk 与 steamAppId' `
    (@($status.dlc.expansions | Where-Object { $_.onDisk -eq $true -and $_.steamAppId }).Count -ge 5) `
    (($status.dlc.expansions | ForEach-Object { "$($_.name):$($_.status)/disk=$($_.onDisk)/app=$($_.steamAppId)" }) -join ' ')

Say '3) 页面渲染（真实 HTML + 真实 status）'
$page.Body | Set-Content "$Work\page.html" -Encoding utf8
$status | ConvertTo-Json -Depth 12 | Set-Content "$Work\status.json" -Encoding utf8
$node = (Get-Command node -ErrorAction SilentlyContinue).Source
if ($node) {
    $harness = Join-Path $PSScriptRoot 'tests/admin-page-render.test.js'
    $out = & $node $harness "$Work\page.html" "$Work\status.json" 2>&1
    $out | ForEach-Object { Write-Host "    $_" }
    Check '页面渲染断言全过（含未登录反向对照）' ($LASTEXITCODE -eq 0) ("node exit=" + $LASTEXITCODE)
} else { Write-Host '  SKIP 没有 node，跳过渲染断言' -ForegroundColor Yellow }

Say '4) mod 列表'
$mods = (Get-Or '/api/mods').Body | ConvertFrom-Json
Check 'GET /api/mods 有配置路径与已装列表' ($mods.configPath -and $mods.installedCount -gt 0) `
    ("path=" + $mods.configPath + " installed=" + $mods.installedCount + " active=" + $mods.activeCount)
Check 'knownExpansions 非空（改 mod 时会被保留）' (@($mods.knownExpansions).Count -ge 1) `
    ("knownExpansions=" + (@($mods.knownExpansions).Count))

if ($SkipWrites) { Say '跳过写操作' } else {
    Say '5) 存档上传：拒绝非存档、接受真存档、哈希对账'
    $bad = Join-Path $Work 'not-a-save.rws'
    [IO.File]::WriteAllText($bad, 'this is not xml', [Text.UTF8Encoding]::new($false))
    $r = Invoke-WebRequest -Uri "$Base/api/saves/upload" -Method Post -WebSession $session -Headers $csrf `
        -ContentType 'application/octet-stream' -Body ([IO.File]::ReadAllBytes($bad)) -TimeoutSec 120 -SkipHttpErrorCheck
    Check '非存档被 400 not-a-save' ([int]$r.StatusCode -eq 400 -and $r.Content -match 'not-a-save') ("HTTP " + [int]$r.StatusCode)

    $good = Join-Path $Work 'probe.rws'
    [IO.File]::WriteAllText($good, "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n<savegame>`n</savegame>`n", [Text.UTF8Encoding]::new($false))
    $localSha = (Get-FileHash $good -Algorithm SHA256).Hash.ToLower()
    $r = Invoke-WebRequest -Uri "$Base/api/saves/upload" -Method Post -WebSession $session -Headers $csrf `
        -ContentType 'application/octet-stream' -Body ([IO.File]::ReadAllBytes($good)) -TimeoutSec 120 -SkipHttpErrorCheck
    $uploaded = $r.Content | ConvertFrom-Json
    Check '小存档上传 200' ([int]$r.StatusCode -eq 200) ("HTTP " + [int]$r.StatusCode)
    Check '上传前后 sha256 一致' ($uploaded.sha256 -eq $localSha -and $uploaded.sha256OnDisk -eq $localSha) `
        ("local=" + $localSha.Substring(0, 16) + " server=" + $uploaded.sha256.Substring(0, 16))

    Say '6) 存档删除：穿越拒绝 / 不存在 404 / 本人上传的可删'
    $bad = Post-Json '/api/saves/delete' @{ name = '../../../etc/passwd' }
    Check '路径穿越被 400' ($bad.Status -eq 400 -and $bad.Body -match 'invalid-name') ("HTTP " + $bad.Status)
    $missing = Post-Json '/api/saves/delete' @{ name = 'no-such-save-xyz' }
    Check '不存在 404' ($missing.Status -eq 404) ("HTTP " + $missing.Status)
    $own = Post-Json '/api/saves/delete' @{ name = $uploaded.name }
    Check '删掉刚上传的那份 200' ($own.Status -eq 200) ("HTTP " + $own.Status)

    Say '7) mod 写盘：只加不改顺序，并保留 knownExpansions'
    $before = (Get-Or '/api/mods').Body | ConvertFrom-Json
    $applied = Post-Json '/api/mods/apply' @{ active = @($before.active) }
    Check '用同一份列表回写 200' ($applied.Status -eq 200) ("HTTP " + $applied.Status)
    $after = (Get-Or '/api/mods').Body | ConvertFrom-Json
    Check 'activeMods 不变' ((($applied.Body | ConvertFrom-Json).active) -join ',' -eq ($before.active -join ',')) ''
    Check 'knownExpansions 仍保留' (@($after.knownExpansions).Count -eq @($before.knownExpansions).Count) `
        ("before=" + @($before.knownExpansions).Count + " after=" + @($after.knownExpansions).Count)
}

$failed = @($checks | Where-Object { -not $_.Ok })
Write-Host ''
Write-Host '================ 汇总 ================'
foreach ($c in $checks) {
    Write-Host ("{0}  {1}  {2}" -f $(if ($c.Ok) { 'PASS' } else { 'FAIL' }), $c.Name, $c.Detail) `
        -ForegroundColor $(if ($c.Ok) { 'Green' } else { 'Red' })
}
Write-Host ("通过 {0}/{1}" -f ($checks.Count - $failed.Count), $checks.Count)
if ($failed.Count -gt 0) { exit 1 }
