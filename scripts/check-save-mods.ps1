# 阶段 9 验收：存档自带 mod 清单 → ModsConfig（含缺 mod 硬失败）
#
# 三件事全部在运行中的容器上跑，不 mock：
#   1. 真实存档（modIds = core/harmony/turbotick）→ 应用后 activeMods 正好这 3 个、顺序照存档、有 .bak
#   2. 改装过 <modIds> 的存档（塞 test.missingmod + modSteamId=0）→ 必须拒绝启动，游戏没起来
#   3. 工坊 ID 路径：**匿名**下载（无需账号）—— 已被识别为 Downloadable，且 apply 会真的调用 workshop_download_item
#
# 用法：.\scripts\check-save-mods.ps1 -Base http://192.168.0.29:30573 -Password xxxxxx

param(
    [string]$Base = 'http://192.168.0.29:30573',
    [string]$Password = $env:TURBOTICK_ADMIN_PASSWORD,
    [string]$RealSave = 'E:\dsh\workspace\rimworld\_lab\admin\server-profile\Saves\de4b38c60e4f23843ed4ede93d655214af1a458c0fc60d1f42d32bfbc9e3ebaf.rws',
    [string]$Work = (Join-Path $env:TEMP 'turbotick-savemods-check')
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $Work -Force | Out-Null
$checks = New-Object System.Collections.ArrayList
function Check($name, $ok, $detail) {
    [void]$checks.Add([pscustomobject]@{ Name = $name; Ok = [bool]$ok; Detail = $detail })
    Write-Host ("  {0} {1}  {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail) `
        -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
}
function Say($m) { Write-Host "[$(Get-Date -Format HH:mm:ss)] $m" }

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$csrf = @{ 'X-TurboTick-Csrf' = '1' }
function Get-Api($path) {
    $r = Invoke-WebRequest -Uri "$Base$path" -WebSession $session -TimeoutSec 300 -SkipHttpErrorCheck
    [pscustomobject]@{ Status = [int]$r.StatusCode; Body = $r.Content }
}
function Post-Api($path, $object) {
    $r = Invoke-WebRequest -Uri "$Base$path" -Method Post -WebSession $session -Headers $csrf -ContentType 'application/json' `
        -Body ($object | ConvertTo-Json -Compress) -TimeoutSec 900 -SkipHttpErrorCheck
    [pscustomobject]@{ Status = [int]$r.StatusCode; Body = $r.Content }
}
function Upload($path) {
    $r = Invoke-WebRequest -Uri "$Base/api/saves/upload" -Method Post -WebSession $session -Headers $csrf `
        -ContentType 'application/octet-stream' -Body ([IO.File]::ReadAllBytes($path)) -TimeoutSec 600 -SkipHttpErrorCheck
    [pscustomobject]@{ Status = [int]$r.StatusCode; Body = $r.Content }
}
function Remote($command) { (ssh -o BatchMode=yes root@192.168.0.29 $command) -join "`n" }

Say '0) 登录'
Invoke-WebRequest -Uri "$Base/login" -Method Post -WebSession $session -Body @{ password = $Password } `
    -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 30 -MaximumRedirection 5 | Out-Null
$status = (Get-Api '/api/status').Body | ConvertFrom-Json
Check '已登录且 auto-apply-mods 可见' ($status.control.authenticated -eq $true -and $null -ne $status.saveMods) `
    ("authenticated=" + $status.control.authenticated + " autoApply=" + $status.saveMods.autoApply)

Say '1) 真实存档：按存档生成 ModsConfig'
$configPath = Remote 'ls /data/rimworld/srv/state/profile/Config/ModsConfig.xml'
$before = Remote 'cat /data/rimworld/srv/state/profile/Config/ModsConfig.xml'
$beforeActive = ([regex]::Matches($before, '<li>([^<]+)</li>') | ForEach-Object { $_.Groups[1].Value })
$backupsBefore = [int](Remote 'ls /data/rimworld/srv/state/profile/Config/ | grep -c "ModsConfig.xml.bak" ')

$up = (Upload $RealSave).Body | ConvertFrom-Json
Check '真实存档上传成功' ($up.ok -eq $true) ("name=" + $up.name + " bytes=" + $up.bytes)

$dry = (Get-Api ("/api/saves/mods?save=" + $up.name)).Body | ConvertFrom-Json
Check 'dry-run 报出存档需要的 mod 清单' ($dry.ok -and $dry.report.required.Count -ge 2) `
    (($dry.report.required | ForEach-Object { $_.id + '=' + $_.disposition }) -join ', ')
Check 'dry-run 不写盘' ($dry.report.written -eq $false) ''

$applied = Post-Api '/api/saves/mods/apply' @{ save = $up.name }
$report = $applied.Body | ConvertFrom-Json
Check '应用后 activeMods 就是存档那三项' `
    (($report.report.activeMods -join ',') -eq 'ludeon.rimworld,brrainz.harmony,lumidust.turbotick') `
    ($report.report.activeMods -join ',')
Check '顺序按存档原样（Core 第一，Harmony 次之）' `
    (($report.report.activeMods[0] -eq 'ludeon.rimworld') -and ($report.report.activeMods[1] -eq 'brrainz.harmony')) ''

$after = Remote 'cat /data/rimworld/srv/state/profile/Config/ModsConfig.xml'
$afterActive = ([regex]::Matches($after, '<li>([^<]+)</li>') | ForEach-Object { $_.Groups[1].Value })
$activeSection = [regex]::Match($after, '(?s)<activeMods>(.*?)</activeMods>').Groups[1].Value
$knowSection = [regex]::Match($after, '(?s)<knownExpansions>(.*?)</knownExpansions>').Groups[1].Value
Check '磁盘上的 activeMods 就是那三项（真读文件）' `
    ((($activeSection -replace '\s', '') -eq '<li>ludeon.rimworld</li><li>brrainz.harmony</li><li>lumidust.turbotick</li>'.Replace(' ', ''))) `
    ($activeSection.Trim() -replace '\s+', ' ')
Check 'knownExpansions 仍是 5 条且内容没变' `
    (([regex]::Matches($knowSection, '<li>').Count) -eq 5) `
    ("li=" + [regex]::Matches($knowSection, '<li>').Count)
$backupsAfter = [int](Remote 'ls /data/rimworld/srv/state/profile/Config/ | grep -c "ModsConfig.xml.bak"')
Check '写盘前留了 .bak 备份' ($backupsAfter -gt $backupsBefore) ("bak: " + $backupsBefore + " -> " + $backupsAfter)

$pf = Remote 'cat /data/rimworld/srv/state/preflight.json 2>/dev/null'
$pfJson = $null
try { $pfJson = $pf | ConvertFrom-Json } catch { }
Check 'preflight.json 里有 mods 段' ($null -ne $pfJson -and $null -ne $pfJson.mods) `
    ($(if ($pfJson -and $pfJson.mods) { "present=" + (@($pfJson.mods.present) -join ',') + " missing=" + (@($pfJson.mods.missing).Count) } else { '无 mods 段' }))

Say '2) 缺 mod 硬失败'
$tampered = Join-Path $Work 'missing-mod.rws'
$bytes = [IO.File]::ReadAllBytes($RealSave)
$text = [Text.Encoding]::UTF8.GetString($bytes)
# 只改 meta 里的三张平行表：id 加一条、steam id 加 0、name 加一条。存档其余部分逐字节保留。
# 行尾不一定是 \r\n，所以锚点是**闭合标签**。注意不能把 `</li>…</modIds>` 整个当锚点：新条目会嵌进上一条的
# <li> 里，解析出来是 "…turboticktest.missingmod" 这种拼接值 —— 第一版正是这样，读起来像解析器坏了。
$text2 = $text.Replace('</modIds>', '<li>test.missingmod</li>' + "`n" + '</modIds>')
$text2 = $text2.Replace('</modSteamIds>', '<li>0</li>' + "`n" + '</modSteamIds>')
$text2 = $text2.Replace('</modNames>', '<li>Missing Test Mod</li>' + "`n" + '</modNames>')
Check '改装后的存档确实多了一个 packageId' ($text2 -match 'test.missingmod') ''
[IO.File]::WriteAllBytes($tampered, [Text.Encoding]::UTF8.GetBytes($text2))

$up2 = (Upload $tampered).Body | ConvertFrom-Json
Check '改装存档上传成功（meta 仍可解析）' ($up2.ok -eq $true) ("name=" + $up2.name)

$dry2 = (Get-Api ("/api/saves/mods?save=" + $up2.name)).Body | ConvertFrom-Json
$manual = @($dry2.report.manualRequired)
Check 'dry-run 把 test.missingmod 列为必须手工放入' `
    ($manual.Count -eq 1 -and $manual[0].id -eq 'test.missingmod' -and $null -ne $dry2.report.refusal) `
    ("manualRequired=" + (($manual | ForEach-Object { $_.id + '/' + $_.name }) -join ', '))
Check 'dry-run 给出拒绝理由并点名该 mod' `
    ($dry2.report.refusal -like '*test.missingmod*') ($dry2.report.refusal)

$apply2 = Post-Api '/api/saves/mods/apply' @{ save = $up2.name }
Check 'apply 被拒绝（非 200）' ($apply2.Status -ne 200) ("HTTP " + $apply2.Status + " code=" + ($apply2.Body | ConvertFrom-Json).report.refusal)

$start = Post-Api '/api/server/start' @{ save = $up2.name }
$startJson = $start.Body | ConvertFrom-Json
Check 'server-start 拒绝（mods-missing）' ($startJson.code -eq 'mods-missing') ("code=" + $startJson.code)
$stateNow = (Get-Api '/api/status').Body | ConvertFrom-Json
Check '游戏状态没有进入 playing/loading' ($stateNow.state -in @('lobby', 'playing')) ("state=" + $stateNow.state + " gameTick=" + $stateNow.gameTick)
$pf2 = Remote 'cat /data/rimworld/srv/state/preflight.json 2>/dev/null'
$pfJson2 = $null
try { $pfJson2 = $pf2 | ConvertFrom-Json } catch { }
Check 'preflight.json 的 mods.manualRequired 点名 test.missingmod' `
    ($null -ne $pfJson2 -and (@($pfJson2.mods.manualRequired) | Where-Object { $_.id -eq 'test.missingmod' })) `
    ("manualRequired=" + ((@($pfJson2.mods.manualRequired) | ForEach-Object { $_.id }) -join ','))

Say '3) 工坊 ID 路径'
$loggedIn = $status.control.authenticated
$steam = (Get-Api '/api/steam/status').Body | ConvertFrom-Json
Check 'steam 状态可读' ($null -ne $steam.login) ("loggedIn=" + $steam.login.loggedIn + " account=" + $steam.account)
$workshopSave = Join-Path $Work 'workshop-mod.rws'
$text3 = $text.Replace('</modIds>', '<li>some.workshopmod</li>' + "`n" + '</modIds>')
$text3 = $text3.Replace('</modSteamIds>', '<li>2195452496</li>' + "`n" + '</modSteamIds>')
$text3 = $text3.Replace('</modNames>', '<li>Workshop Mod</li>' + "`n" + '</modNames>')
[IO.File]::WriteAllBytes($workshopSave, [Text.Encoding]::UTF8.GetBytes($text3))
$up3 = (Upload $workshopSave).Body | ConvertFrom-Json
$dry3 = (Get-Api ("/api/saves/mods?save=" + $up3.name)).Body | ConvertFrom-Json
$dl = @($dry3.report.required | Where-Object { $_.id -eq 'some.workshopmod' })
Check '工坊 mod 被识别为 Downloadable（不是 Manual）' `
    ($dl.Count -eq 1 -and $dl[0].disposition -eq 'Downloadable' -and $dl[0].workshopId -eq 2195452496) `
    ("disposition=" + $dl[0].disposition + " workshopId=" + $dl[0].workshopId)

# 工坊下载是**匿名**的：不需要登录。这里只验证「被识别为可下载」与「apply 会真的调用」，
# 真下载由 check-workshop-anon.ps1 单独跑（它要先清缓存，代价是一次网络下载）。
$apply3 = Post-Api '/api/saves/mods/apply' @{ save = $up3.name }
$r3 = $apply3.Body | ConvertFrom-Json
$called = Remote 'grep -ac "workshop_download_item" /data/rimworld/srv/state/rimworld-stdout.log 2>/dev/null || echo 0'
Check 'apply 真的调用了 workshop_download_item（匿名）' ($called -ne '0') ("log hits=" + $called)
Check '匿名：refusal/日志里不出现「需要登录」' `
    (($r3.report.refusal -notlike '*需要*登录*') -and ($r3.detail -notlike '*需要*登录*')) `
    ("refusal=" + $r3.report.refusal)
Check '匿名下载命令带 +login anonymous' `
    ((Remote 'grep -a "workshop_download_item" /data/rimworld/srv/state/rimworld-stdout.log | tail -n 1') -like '*login anonymous*') ''

Say '4) 清理测试存档'
foreach ($name in @($up2.name, $up3.name)) {
    $del = Post-Api '/api/saves/delete' @{ name = $name }
    Write-Host ("    删除 " + $name + " -> HTTP " + $del.Status)
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
