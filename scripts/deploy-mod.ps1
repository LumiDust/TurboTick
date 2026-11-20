# 把当前构建推送到两个真实运行位置：本机游戏目录，以及远端专服。
#
# 为什么需要这个脚本：客户端和服务端的 mod 是**两份独立的拷贝**，而「两边 mod 内容必须一致」正是
# TurboTick 握手的前提。手工 `cp` 的结果是实测过的：客户端升到 22:30 的构建，服务端还停在 11 月的
# `af33973a`，于是玩家看到的是旧服务端发出的那句旧文案，而我从客户端一侧怎么查都是「已是最新」。
#
# 用法：
#   pwsh scripts/deploy-mod.ps1                 # 两边都推
#   pwsh scripts/deploy-mod.ps1 -ClientOnly     # 只推本机
#   pwsh scripts/deploy-mod.ps1 -ServerOnly     # 只推远端
#   pwsh scripts/deploy-mod.ps1 -Restart        # 推完重启容器让服务端加载新程序集
#
# 一次推送固定做这些事：
#   1. 校验本地三个 dll 存在且同一次构建（sha256 记录进日志，便于事后核对两边是否一致）
#   2. 复制 1.7/Assemblies/*.dll、About/、Languages/、LoadFolders.xml
#   3. **回读**目标端的 sha256 并与本地比对，不一致直接报错
#   4. `-Restart` 时先 stop 容器、替换、再 start（运行中的进程会锁住 dll，实测报「文本文件忙」）
param(
    [string]$ClientMods = 'C:\SteamLibrary\steamapps\common\RimWorld\Mods',
    [string]$ServerHost = 'root@192.168.0.29',
    [string]$ServerMods = '/data/rimworld/srv/mods',
    [switch]$ClientOnly,
    [switch]$ServerOnly,
    [switch]$Restart,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT = 'E:\dsh\workspace\rimworld\tools\dotnet10'
$dotnet = 'E:\dsh\workspace\rimworld\tools\dotnet10\dotnet.exe'
$refs = 'E:\dsh\workspace\rimworld\tools\ref17\ref\net10.0'
$assemblies = @('TurboTick.dll', 'TurboTick.Networking.dll', 'TurboTick.Simulation.dll')

function Say($m) { Write-Host "[$(Get-Date -Format HH:mm:ss)] $m" }
function Fail($m) { Write-Host "[$(Get-Date -Format HH:mm:ss)] 失败：$m" -ForegroundColor Red; exit 1 }

if (-not $SkipBuild) {
    Say '构建'
    & $dotnet build (Join-Path $repo 'Source/TurboTick.csproj') -c Release -p:RimWorldDir=$refs 2>&1 |
        Select-String -Pattern 'error CS|个警告|个错误' | ForEach-Object { Write-Host ('    ' + $_.Line.Trim()) }
    if ($LASTEXITCODE -ne 0) { Fail '构建未通过' }
}

Say '本地产物'
$local = @{}
foreach ($name in $assemblies) {
    $path = Join-Path $repo "1.7/Assemblies/$name"
    if (-not (Test-Path $path)) { Fail "缺少 $path（先构建）" }
    # 一次构建的三个 dll 时间戳应当接近；相差超过五分钟说明其中一个是旧产物，是最常见的「改了没生效」。
    $local[$name] = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ("    {0} {1}  {2}" -f $local[$name].Substring(0,16), (Get-Item $path).LastWriteTime.ToString('HH:mm:ss'), $name)
}
$stamps = $assemblies | ForEach-Object { (Get-Item (Join-Path $repo "1.7/Assemblies/$_")).LastWriteTime }
if ((($stamps | Measure-Object -Maximum).Maximum - ($stamps | Measure-Object -Minimum).Minimum).TotalMinutes -gt 5) {
    Fail '三个 dll 的时间戳相差超过 5 分钟，其中至少一个是旧产物；先完整重建'
}

function CopyMod([string]$dest, [string]$label) {
    Say "推送到$label：$dest"
    $root = Join-Path $dest 'TurboTick'
    New-Item -ItemType Directory -Path (Join-Path $root '1.7/Assemblies') -Force | Out-Null
    foreach ($name in $assemblies) {
        Copy-Item -LiteralPath (Join-Path $repo "1.7/Assemblies/$name") -Destination (Join-Path $root "1.7/Assemblies/$name") -Force
    }
    foreach ($dir in @('About', 'Languages')) {
        $target = Join-Path $root $dir
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        Copy-Item -LiteralPath (Join-Path $repo $dir) -Destination $target -Recurse -Force
    }
    Copy-Item -LiteralPath (Join-Path $repo 'LoadFolders.xml') -Destination (Join-Path $root 'LoadFolders.xml') -Force
    # 回读校验：复制成功不等于内容正确（运行中的进程可能让 cp 只写了一半），只认 sha256。
    foreach ($name in $assemblies) {
        $actual = (Get-FileHash (Join-Path $root "1.7/Assemblies/$name") -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $local[$name]) { Fail "$label 的 $name 校验失败：$actual ≠ $($local[$name])" }
    }
    Say "$label 校验通过（3 个 dll 一致）"
}

if (-not $ServerOnly) { CopyMod $ClientMods '本机游戏目录' }

if (-not $ClientOnly) {
    Say "打包并上传到 $ServerHost"
    $stage = Join-Path ([IO.Path]::GetTempPath()) ('tt-deploy-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    try {
        $mod = Join-Path $stage 'TurboTick'
        New-Item -ItemType Directory -Path (Join-Path $mod '1.7/Assemblies') -Force | Out-Null
        foreach ($name in $assemblies) {
            Copy-Item -LiteralPath (Join-Path $repo "1.7/Assemblies/$name") -Destination (Join-Path $mod "1.7/Assemblies/$name")
        }
        foreach ($dir in @('About', 'Languages')) {
            Copy-Item -LiteralPath (Join-Path $repo $dir) -Destination (Join-Path $mod $dir) -Recurse
        }
        Copy-Item -LiteralPath (Join-Path $repo 'LoadFolders.xml') -Destination $mod
        $tarball = Join-Path $stage 'mod.tgz'
        tar czf $tarball -C $stage TurboTick
        if ($LASTEXITCODE -ne 0) { Fail '打包失败' }
        # 二进制走 stdin：ssh 传多行脚本会被 CRLF 破坏，二进制更不能走命令行参数。
        # **不能**写 `$bytes | ssh …`：Windows PowerShell 5.1 会把 byte[] 按「每个字节一行」转成文本再喂给
        # 原生命令，收到的 tgz 只有几十字节，远端报 `gzip: stdin: not in gzip format`（本轮实测踩到）。
        # 用 bash 的重定向把文件直接接到 stdin 才是逐字节传输。
        & bash -c "ssh -o ConnectTimeout=15 $ServerHost 'cat > /tmp/tt-mod.tgz && echo uploaded' < '$tarball'"
        if ($LASTEXITCODE -ne 0) { Fail '上传失败' }
        # 远端脚本先落到本地文件，再整份管道给 ssh 的 bash。理由有两条，都是实测出来的：
        #   1. 直接 `& ssh host $multiline` 会被 PowerShell 拆成多个参数，换行变成命令分隔，脚本走形；
        #   2. ssh 往 stderr 写一个字（哪怕只是 "Warning: Permanently added…"），PS 5.1 就把它当
        #      NativeCommandError 抛出，`$LASTEXITCODE` 与 `$result` 都拿不到真实结果。
        $remote = @"
set -e
rm -rf /tmp/tt-mod && mkdir -p /tmp/tt-mod && tar xzf /tmp/tt-mod.tgz -C /tmp/tt-mod
D=$ServerMods/TurboTick
# 先解到暂存目录，再整体替换，避免中途失败留下半个 mod（半套 dll 会让服务端启动即报类型加载失败）。
rm -rf /tmp/tt-mod/old && mv `$D /tmp/tt-mod/old
cp -a /tmp/tt-mod/TurboTick `$D
chown -R 10001:10001 `$D
for f in $($assemblies -join ' '); do
    cmp -s "`$D/1.7/Assemblies/`$f" "/tmp/tt-mod/TurboTick/1.7/Assemblies/`$f" || { echo "VERIFY FAIL `$f"; exit 1; }
done
echo "server-verify-ok"
"@
        $scriptFile = Join-Path $stage 'remote.sh'
        # LF only: a CRLF shebang/script fed to bash on the other side fails on the first line with a stray \r.
        [IO.File]::WriteAllText($scriptFile, ($remote -replace "`r`n", "`n"))
        $result = & bash -c "ssh -o ConnectTimeout=15 $ServerHost bash -s < '$scriptFile' 2>&1"
        $result | ForEach-Object { Write-Host ('    ' + $_) }
        if (($result -join "`n") -notmatch 'server-verify-ok') { Fail '远端校验未通过' }
        Say '远端校验通过'
    }
    finally { if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue } }
}

if ($Restart) {
    Say '重启容器以加载新程序集'
    & ssh -o ConnectTimeout=15 $ServerHost 'docker stop turbotick-server >/dev/null; docker start turbotick-server >/dev/null; echo restarted' |
        ForEach-Object { Write-Host ('    ' + $_) }
}

Say '完成'
Write-Host ''
Write-Host '下次启动游戏即使用新程序集；服务端若未加 -Restart，需要手动重启容器才会加载。' -ForegroundColor DarkGray
