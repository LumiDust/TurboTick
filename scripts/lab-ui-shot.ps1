# UI 截图：直接看多人游戏窗口长什么样。
#
# 与 lab-gui-replica.ps1 的区别：那个脚本启动完整联机会话并截图游戏内画面，本脚本**只**打开
# 多人游戏窗口（标题界面即可），截图后退出。用途是 UI 改版后的目视核对——布局、字号、间距、
# 按钮对齐这些没有断言能代替眼睛。
#
# 三个坑（都是 lab-gui-replica.ps1 里踩过的，这里照抄处理）：
# 1. 不重定向输出 ⇒ logs 目录为空，游戏自己启动失败也看不出来。
# 2. 截全屏 ⇒ 抓到桌面当时前台的其他窗口。按 MainWindowHandle 的矩形裁图。
# 3. 游戏是 2560x1440@150%，不调用 SetProcessDPIAware 会让坐标被 DWM 缩放，裁出来的图错位。
param(
    [string]$Lab = 'E:\dsh\workspace\rimworld\_lab\ui',
    [int]$ShotAfter = 40,
    [int]$KeepOpen = 0,
    # 界面语言。默认跟你的客户端一致（简体中文）；测英文回退时传 English。
    [string]$Language = 'ChineseSimplified (简体中文)',
    # 截图哪个窗口：multiplayer（连接页/房间页）或 modcheck（模组校验表演示）。
    [string]$Window = 'multiplayer',
    # 连到真实专服（host:port）。空 = 只开窗口不连。
    # 用途：验证「握手被拒时是否真的弹出模组校验表」——这是唯一没法靠单元测试断言的一步，
    # 因为窗口挂在 WindowStack 上，只有跑起来才看得见。
    [string]$Connect = ''
)

$ErrorActionPreference = 'Stop'
$src = 'C:\SteamLibrary\steamapps\common\RimWorld'
$wt = 'E:\dsh\workspace\rimworld\TurboTick-headless'
$game = $src
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
    Where-Object { $_.CommandLine -and $_.CommandLine -like "*$Lab*" } |
    ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch { } }
Start-Sleep -Seconds 2

Say 'profile'
# 不建游戏树。原来是把真实安装目录用 292 个 junction + hardlink 复制成一份 `$Lab\game`，现在不需要：
#   * `-savedatafolder` 决定 `Config/ModsConfig.xml` 的位置（`GenFilePaths.ModsConfigFilePath`），
#     所以「本机只加载 Core + Harmony + TurboTick」这件事只要写 `$Lab\profile\Config\ModsConfig.xml` 就成立；
#   * mod 本身（`Mods/`）从**真实安装目录**读，而那正是 `deploy-mod.ps1` 的交付目标，本来就是最新的。
# 原来的游戏树还有一个从没被发现的坑：hardlink 不能跨盘符，工作区搬到 E 盘后 `mklink /H` 全失败
# （`The system cannot move the file to a different disk drive.`），而 junction 虽然能跨盘，符号链接又
# 需要管理员权限——这条路整个是错的，不是参数没调好。

foreach ($d in @("$Lab\logs", "$Lab\profile")) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
if (-not (Test-Path "$src\RimWorld.exe")) { throw "找不到游戏：$src\RimWorld.exe" }
foreach ($need in @('Harmony', 'TurboTick')) {
    if (-not (Test-Path "$src\Mods\$need")) { throw "真实安装目录缺少 Mods\$need，先跑 deploy-mod.ps1" }
}

Say '两段式 profile'
foreach ($p in @("$Lab\profile")) {
    New-Item -ItemType Directory -Path "$p\Config" -Force | Out-Null
    $modsConfig | Set-Content "$p\Config\ModsConfig.xml" -Encoding utf8
}
# 预热不带 `-turbotick-open-multiplayer`：第一次启动只为了让游戏把 Core 的默认 ModsConfig 写出来，
# 带上窗口开关反而会在一个即将被杀掉的进程里多开一个窗口，截图时可能抓错窗口。
# 也不用 `-quicktest`：那个参数会直接进地图，预热就失去了「让游戏写出 profile」以外的意义。
$warm = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -WindowStyle Hidden -PassThru `
    -ArgumentList @("-savedatafolder=$Lab\profile") `
    -RedirectStandardOutput "$Lab\logs\warm-out.log" -RedirectStandardError "$Lab\logs\warm-err.log"
Start-Sleep -Seconds 25
try { if (-not $warm.HasExited) { $warm.Kill() } } catch { }
Start-Sleep -Seconds 3
$modsConfig | Set-Content "$Lab\profile\Config\ModsConfig.xml" -Encoding utf8
# 语言在 Prefs.xml 的 `<langFolderName>`。只在文件已存在时改：预热已经让游戏写出了一份，直接改这一行
# 比去模拟「选项 → 语言」可靠得多。默认英文是游戏的初始语言，而玩家的客户端是简体中文。
$prefs = "$Lab\profile\Config\Prefs.xml"
if (Test-Path $prefs) {
    $text = Get-Content -Raw -Encoding UTF8 $prefs
    if ($text -match '<langFolderName>[^<]*</langFolderName>') {
        $text = $text -replace '<langFolderName>[^<]*</langFolderName>', "<langFolderName>$Language</langFolderName>"
        Set-Content -Path $prefs -Value $text -Encoding UTF8 -NoNewline
        Say "语言 = $Language"
    }
} else {
    Say "没有 Prefs.xml，保持默认语言"
}

Say '启动带窗口的游戏'
# 不带 `-quicktest`：它跳过标题界面直接进地图，而窗口开关挂在 `UIRoot_Entry.UIRootUpdate` 上，
# 那个阶段根本不会运行，截到的会是游戏画面而不是窗口。
# 但**连专服**时必须带 -quicktest：`AutoConnectOnce` 是从 `TurboGameComponent.GameComponentUpdate` 调的，
# 而 GameComponent 只在殖民地存在时更新。标题界面没有 game，永远连不上。
$windowFlag = if ($Window -eq 'modcheck') { '-turbotick-mod-check-demo' } else { '-turbotick-open-multiplayer' }
$args = @("-savedatafolder=$Lab\profile")
if ($Connect.Length -gt 0) {
    # -quicktest 会进地图，窗口开关在那条路径上是死代码，两者不叠加。
    $args += @('-quicktest', "-turbotick-connect=$Connect")
    Say "连专服 $Connect（-quicktest 进地图以驱动 AutoConnectOnce）"
} else {
    $args += $windowFlag
}
$cli = Start-Process "$game\RimWorld.exe" -WorkingDirectory $game -PassThru `
    -ArgumentList $args `
    -RedirectStandardOutput "$Lab\logs\client-out.log" -RedirectStandardError "$Lab\logs\client-err.log"
Say "pid=$($cli.Id)，窗口 $Window，等 $ShotAfter 秒"

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WinUI {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    // PrintWindow takes the window's own pixels, so a window that is behind another one still captures correctly.
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[void][WinUI]::SetProcessDPIAware()
Add-Type -AssemblyName System.Windows.Forms
# 键盘输入由 SendKeys 直接送给前台窗口，不需要模拟鼠标位置（模拟鼠标会被用户正在用的桌面抢走）。
$wshell = New-Object -ComObject WScript.Shell

function Shot($proc, $path) {
    $proc.Refresh()
    $h = $proc.MainWindowHandle
    # `-PassThru` gives an IntPtr that is sometimes "" rather than IntPtr.Zero before the window exists; comparing
    # against [IntPtr]::Zero alone let that through and SetForegroundWindow threw "Cannot convert null".
    if ($null -eq $h -or "$h" -eq "" -or $h -eq [IntPtr]::Zero) {
        Say "跳过截图：pid=$($proc.Id) 没有主窗口（可能已退出）"
        return $false
    }
    [void][WinUI]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 500
    $r = New-Object WinUI+RECT
    if (-not [WinUI]::GetWindowRect($h, [ref]$r)) { Say '跳过截图：GetWindowRect 失败'; return $false }
    $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    if ($w -le 0 -or $ht -le 0) { Say "跳过截图：窗口矩形 $w x $ht"; return $false }
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    # PrintWindow first: `CopyFromScreen` captures whatever is on the desktop at those coordinates, and Windows
    # refuses SetForegroundWindow while the user is typing in another window (measured: the screenshot came back as
    # this very editor). PrintWindow asks the window for its own pixels, so z-order does not matter.
    # PW_RENDERFULLCONTENT (2) is required for composited/GPU-presented windows; without it the result is black.
    $hdc = $g.GetHdc()
    try { $ok = [WinUI]::PrintWindow($h, $hdc, 2) } finally { $g.ReleaseHdc($hdc) }
    if (-not $ok) {
        Say 'PrintWindow 失败，退回屏幕截图'
        $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $ht))
    }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Say "截图 $path（${w}x${ht}）"
    return $true
}

Start-Sleep -Seconds $ShotAfter
[void](Shot $cli "$Lab\shot-title-$(Get-Date -Format HHmmss).png")

# 第二张：先关掉所有窗口，再截一次。
#
# 为什么需要：连真实专服时握手会被拒（本机 mod 列表与房间不同），`ModSyncDialog` 会盖在整个多人游戏窗口上，
# 于是第一张截图只拍到校验表，房间页的布局根本没入镜——这正是「布局重叠改完了吗」无法靠截图回答的原因。
# 用 ESC 走游戏自己的关闭路径（`Window.OnCancelKeyPressed`），比 PostMessage WM_CLOSE 更接近玩家操作。
if ($CloseWindows) {
    [void][WinUI]::SetForegroundWindow((Get-Process RimWorld -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1).MainWindowHandle)
    Start-Sleep -Milliseconds 400
    for ($i = 0; $i -lt 3; $i++) {
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        Start-Sleep -Milliseconds 700
    }
    Start-Sleep -Seconds 1
    [void](Shot $cli "$Lab\shot-title-$(Get-Date -Format HHmmss)-after-esc.png")
}

# 走 UI 打开多人游戏窗口：标题界面 → 多人游戏按钮。按钮位置随分辨率变化，因此不点坐标，
# 而是用 TurboTick 自己的调试开关（TURBOTICK_OPEN_MULTIPLAYER）在启动时直接把窗口挂上去。
# 开关没做进去时这里只截标题界面，仍是有效的「至少不崩」证据。
Say '收工'
if ($KeepOpen -gt 0) { Start-Sleep -Seconds $KeepOpen }
try { if (-not $cli.HasExited) { $cli.Kill() } } catch { }
Start-Sleep -Seconds 3
Get-CimInstance Win32_Process -Filter "Name='RimWorld.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine -like "*$Lab*" } |
    ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch { } }
if (Test-Path "$Lab\logs\client-out.log") {
    $errors = Get-Content -Encoding Default "$Lab\logs\client-out.log" | Select-String -Pattern 'ERROR|Exception' |
        Select-Object -First 5
    Say "日志 ERROR/Exception 行数：$($errors.Count)"
    $errors | ForEach-Object { Write-Host ('    ' + $_.Line.Substring(0, [Math]::Min(200, $_.Line.Length))) }
}
Say "截图目录：$Lab"
