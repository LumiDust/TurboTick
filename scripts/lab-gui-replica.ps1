# GUI 版副本：权威 headless + 一个**带窗口**的副本，用来肉眼/截图确认小人到底动没动。
# 只读截图，不判定玩法；判定仍由 lab-headless.ps1 的引擎 tick 自检负责。
#
# 两个曾经的坑（2026-11-14）：
# 1. 不重定向输出 ⇒ logs 目录是空的，只有 Player.log 里那点 WARN 可看，副本的
#    「toil 一致/工作同步」等判据行全丢了。现在和 lab-headless.ps1 一样重定向。
# 2. 截全屏 ⇒ 抓到的是桌面上当时前台别的游戏窗口，不是 RimWorld。现在按主窗口
#    矩形裁图，并用 SetProcessDPIAware 保证 2560x1440@150% 缩放下坐标不被 DWM 缩放。
param(
    [int]$Port = 30593,
    [string]$Lab = 'E:\dsh\workspace\rimworld\_lab\gui',
    [int]$ShotAfter = 45
)

$ErrorActionPreference = 'Stop'
$wt   = 'E:\dsh\workspace\rimworld\TurboTick-headless'
$src  = 'C:\SteamLibrary\steamapps\common\RimWorld'
$game = "$Lab\game"
$env:DOTNET_ROOT = 'E:\dsh\workspace\rimworld\tools\dotnet10'

function Say($m) { Write-Host "[$(Get-Date -Format HH:mm:ss)] $m" }

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

Get-CimInstance Win32_Process -Filter "Name='RimWorld.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and ($_.CommandLine -like "*$Lab*" -or $_.CommandLine -like "*:$Port*") } |
    ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch { } }
Start-Sleep -Seconds 2

Say '清理上一轮'
# 与 lab-headless.ps1 同一条教训：重新构建会改变 modHash，而服务端会（正确地）拒绝加载旧房间。
# 不清理 state，权威端会 failed 退出，副本只能显示 "0 players / connected pending"——看起来像连不上，其实是没房间。
if (Test-Path "$Lab\state") { Remove-Item "$Lab\state" -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path "$Lab\state" -Force | Out-Null

Say '游戏树'
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
Copy-Item "$wt\About"           "$game\Mods\TurboTick\About"                 -Recurse -Force
Copy-Item "$wt\Languages"       "$game\Mods\TurboTick\Languages"             -Recurse -Force
Copy-Item "$wt\LoadFolders.xml" "$game\Mods\TurboTick\"                      -Force -ErrorAction SilentlyContinue
Copy-Item "$wt\1.7\Assemblies\*.dll" "$game\Mods\TurboTick\1.7\Assemblies\" -Force

Say '两段式 profile'
foreach ($p in @("$Lab\server-profile", "$Lab\client-profile")) {
    New-Item -ItemType Directory -Path "$p\Config" -Force | Out-Null
    $modsConfig | Set-Content "$p\Config\ModsConfig.xml" -Encoding utf8
}
$env:TURBOTICK_HOST = '127.0.0.1'
$env:TURBOTICK_PORT = "$Port"
$env:TURBOTICK_DATA = "$Lab\state"
$env:HOME = "$Lab\server-profile"
$env:XDG_CONFIG_HOME = "$Lab\server-profile\.config"
$env:TURBOTICK_SCRIPT_OPS = ''
$warm = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
    -ArgumentList @('-headless', '-quicktest', "-savedatafolder=$Lab\server-profile") `
    -RedirectStandardOutput "$Lab\logs\warm-out.log" -RedirectStandardError "$Lab\logs\warm-err.log"
Start-Sleep -Seconds 40
try { if (-not $warm.HasExited) { $warm.Kill() } } catch { }
Start-Sleep -Seconds 3
foreach ($p in @("$Lab\server-profile", "$Lab\client-profile")) {
    $modsConfig | Set-Content "$p\Config\ModsConfig.xml" -Encoding utf8
}

Say '权威端'
$srv = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
    -ArgumentList @('-headless', '-turbotick-server', '-quicktest', "-savedatafolder=$Lab\server-profile") `
    -RedirectStandardOutput "$Lab\logs\server-out.log" -RedirectStandardError "$Lab\logs\server-err.log"
$deadline = (Get-Date).AddSeconds(180)
$playing = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    $status = "$Lab\state\headless-status.json"
    if (Test-Path $status) {
        try { $j = Get-Content $status -Raw | ConvertFrom-Json } catch { continue }
        if ($j.state -eq 'playing') { $playing = $true; break }
        if ($j.state -eq 'failed') { throw "权威端失败：$($j.failure)" }
    }
}
if (-not $playing) { throw '权威端 180 秒内没有进入 playing' }
Say "权威端 playing pid=$($srv.Id)"

Say 'GUI 副本（带窗口）'
$cli = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -PassThru `
    -ArgumentList @('-quicktest', "-turbotick-connect=127.0.0.1:$Port", "-savedatafolder=$Lab\client-profile") `
    -RedirectStandardOutput "$Lab\logs\client-out.log" -RedirectStandardError "$Lab\logs\client-err.log"
Say "pid=$($cli.Id)，等 $ShotAfter 秒后截图"

Start-Sleep -Seconds $ShotAfter

# 按窗口矩形截图，不截整个桌面：桌面当时很可能前台是别的游戏，上一版就是这么抓到无关画面的。
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[void][Win]::SetProcessDPIAware()

function Shot($proc, $path) {
    $proc.Refresh()
    $h = $proc.MainWindowHandle
    if ($h -eq [IntPtr]::Zero) {
        # 窗口还没建好（或进程已退出）：这一次不产出图片，而不是产出一张桌面照。
        Say "跳过截图：pid=$($proc.Id) 没有主窗口"
        return $false
    }
    [void][Win]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 400
    $r = New-Object Win+RECT
    if (-not [Win]::GetWindowRect($h, [ref]$r)) { Say '跳过截图：GetWindowRect 失败'; return $false }
    $w = $r.Right - $r.Left
    $ht = $r.Bottom - $r.Top
    if ($w -le 0 -or $ht -le 0) { Say "跳过截图：窗口矩形为 $w x $ht"; return $false }
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $ht))
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Say "截图 $path（$w x $ht）"
    return $true
}

$null = Shot $cli "$Lab\shot-$(Get-Date -Format HHmmss).png"
Start-Sleep -Seconds 8
$null = Shot $cli "$Lab\shot-$(Get-Date -Format HHmmss)-b.png"

try { if (-not $cli.HasExited) { $cli.Kill() } } catch { }
try { if (-not $srv.HasExited) { $srv.Kill() } } catch { }
Start-Sleep -Seconds 4
Get-CimInstance Win32_Process -Filter "Name='RimWorld.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine -like "*$Lab*" } |
    ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch { } }

# 判定行：读重定向下来的副本日志，没有它就等于什么都没测。
$log = "$Lab\logs\client-out.log"
if (Test-Path $log) {
    $lines = Get-Content -Encoding Default $log | Select-String -Pattern 'toil 一致|引擎tick|副本跳过命令|权威帧'
    Say "副本日志 $($lines.Count) 行关键读数，最后一条："
    if ($lines.Count) { Write-Host ('    ' + $lines[-1].Line.Substring(0, [Math]::Min(400, $lines[-1].Line.Length))) }
} else {
    Say "副本日志缺失：$log"
}
Say '收工（游戏树保留，便于复查）'
