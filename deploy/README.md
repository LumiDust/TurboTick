# TurboTick headless 专服部署

> **本文描述的是 0.6.0 的 headless 游戏托管部署，0.6.1 起不再是默认方式。**
>
> 0.6.1 的专服是**协调器**：自包含单文件、不加载殖民地、**不需要安装 RimWorld**，
> 每个玩家在本地完整模拟。部署方式见[仓库 README](../README.md#部署专服)与
> `deploy/turbotick-coordinator.service`。
>
> 本文保留的原因只有两个：回退到 ≤0.6.0 的 headless 版本时需要它；以及从旧 headless
> 世界迁移到协调器时可以参考数据目录结构。文中「旧独立 .NET 网络服已退役」对应 0.6.0
> 当时的决定，0.6.1 已改回该形态（但不再模拟殖民地）。

**0.6.0 权威同步 · RimWorld 1.7 · 网络协议 v4**。本文只使用通用占位值：`ACCOUNT` 为有权下载游戏的 Steam 账号，`SERVER_HOST` 为玩家可达地址，`BUILD` 为已核对的完整游戏 build，`PROXY_HOST:PROXY_PORT` 为可选代理。Linux 根目录示例为 `/srv/turbotick`，Windows 为 `C:\Servers\TurboTick`。

本版作为容器预览发布：安装/运行隔离、入口与健康检查已验证，但新随机存档的双端长跑仍观察到锁步分叉，故障会挂起时间线。先用独立数据目录试运行，保留旧包和恢复数据，不把镜像健康状态当作长期模拟一致的证明。稳定版 latest 不随此预览更新。

0.6.0 的专服只有 **headless 常驻模拟**，可选 Linux 原生/systemd、Windows 原生或 Linux Docker。它加载真正的 RimWorld、Harmony、TurboTick 和匹配模组，执行完整模拟、保存检查点，全部玩家退出后仍可推进世界。客户端仍执行完整 lockstep 模拟；新玩家加入时所有模拟副本共同重载检查点，会有一次加载停顿。

旧独立 .NET 网络服已退役，旧 `ghcr.io/lumidust/turbotick-server` 镜像停止新发布。≤0.5.1 的历史附件保留用于回退，迁移见[旧独立网络服迁移](#旧独立网络服迁移)。

## 选择下载与部署方式

从 [0.6.0 Release](https://github.com/LumiDust/TurboTick/releases/tag/v0.6.0) 下载并保存原包及 `SHA256SUMS`：

| 下载项 | 使用者与内容 |
|---|---|
| `TurboTick-0.6.0-mod-1.7.zip` | 所有玩家；完整 `TurboTick/`，解压到各自游戏 `Mods` |
| `TurboTick-0.6.0-headless-installer.zip` | 服主；同字节的 `TurboTick/`、`scripts/steamcmd/`、`docker-compose.yml`、`.env.example`、`deploy/container/`、本说明、systemd 模板及许可证 |
| `ghcr.io/lumidust/turbotick-headless:0.6.0` | Docker headless 镜像，仅 **linux/amd64** |

部署附件不含 Dockerfile 或用于构建镜像的 `artifacts/container-mod` 输入目录，也不含游戏、Harmony、账号缓存、密码、存档或日志；使用已发布镜像无需本地构建。镜像打包 Ubuntu 系统依赖、SteamCMD bootstrap、容器入口和该 Release 的同字节 TurboTick Mod，安装阶段另行下载游戏与 Harmony。游戏及 Harmony 也不嵌入 Mod 附件。

发布流水线先构建、测试并生成客户端 Mod zip，再校验其 `SHA256SUMS`，从该 zip 解出完整 `TurboTick/` 作为镜像 payload；镜像构建不会另编译一套 DLL。镜像保存 payload 文件清单，run 在启动游戏前检查已安装 TurboTick 与镜像内容逐文件一致，发现缺失、修改或多余文件时要求停服后重新安装。镜像的干净构建上下文只纳入公开入口/安装脚本、许可证及该 payload，游戏/Harmony/账号材料由外置卷提供。

镜像不发布 ARM/arm64；原生 ARM 也未作为本版支持目标验证。旧 .NET 镜像曾支持的架构不代表 headless 游戏具有相同支持。

部署前为所有参与端固定：**精确 GameBuild、DLC 集合、完整模组文件、加载顺序与配置**。仅版本显示“1.7”、模组同名或自行重编译同版本 DLL 都不足以保证指纹一致。优先把同一份 Release Mod 包分发给服务端与玩家，不混用不同时间自动更新的工坊副本。

## Docker：从空机器开始

### 1. 安装 Docker Engine 与 Compose

以下是 Ubuntu 24.04 x86-64 的示例；其他发行版按 [Docker Engine 官方安装说明](https://docs.docker.com/engine/install/)安装，使用 Compose v2 的 `docker compose` 命令。先确认 `uname -m` 输出 `x86_64`，宿主有足够空间容纳完整游戏、工坊内容及备份。部署使用的系统账号须能访问 Docker daemon；也可在以下命令前使用 `sudo`。

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates curl unzip
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg \
  -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
. /etc/os-release
printf 'deb [arch=%s signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu %s stable\n' \
  "$(dpkg --print-architecture)" "$VERSION_CODENAME" | \
  sudo tee /etc/apt/sources.list.d/docker.list >/dev/null
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io \
  docker-buildx-plugin docker-compose-plugin
sudo systemctl enable --now docker
docker version
docker compose version
```

如最后两项提示 Docker socket 权限问题，继续用 `sudo docker ...`，或按[官方非 root 管理说明](https://docs.docker.com/engine/install/linux-postinstall/)配置宿主账号后重新登录。容器内仍默认使用 UID/GID **10001:10001**。

### 2. 解压部署包，准备目录与权限

把 headless 附件及校验文件放到服务器后校验；`SHA256SUMS` 同时列出其他附件时，只校验已下载的附件条目：

```bash
grep '  TurboTick-0.6.0-headless-installer.zip$' SHA256SUMS | sha256sum -c -
sudo install -d -m 0755 /srv/turbotick
sudo unzip TurboTick-0.6.0-headless-installer.zip -d /srv/turbotick
sudo chown -R "$(id -u):$(id -g)" /srv/turbotick
cd /srv/turbotick
mkdir -p data/game data/profile data/state data/steam-cache backups secrets
sudo chown -R 10001:10001 data/game data/profile data/state data/steam-cache
sudo chmod 0750 data/game data/profile data/state data/steam-cache
chmod 0700 backups secrets
cp .env.example .env
```

首次运行前明确创建并授权 bind 目录。让 Docker 自动创建缺失目录通常会得到 root 所有的目录，默认非 root 安装器随即写入失败。若使用不同 UID/GID，同时修改 Compose 的运行身份配置并把四个目录及已有内容 `chown` 给该身份。

| 宿主 bind 路径 | 容器路径 | installer（setup） | headless（日常 run） | 内容与备份 |
|---|---|---|---|---|
| `./data/game` | `/game` | 读写 | **只读** | Linux 游戏、DLC、完整 Harmony 与 TurboTick/附加模组；按 build/模组包保留 |
| `./data/profile` | `/profile` | 读写 | 读写 | `Config`、`Saves` 与游戏用户数据；必须备份 |
| `./data/state` | `/state` | 读写 | 读写 | 协议状态、快照、`headless-status.json` 及 deployment 锁；必须备份 |
| `./data/steam-cache` | `/steam` | 读写 | **不挂载** | SteamCMD、账号认证缓存和工坊下载缓存；单独受控保存 |

游戏只读不影响日常存档与检查点，它们写入 profile/state。两个实例各用独立四目录；setup 与 run 通过共享 deployment 锁互斥，安装、更新和运行不可并行。先正常停止 headless，再运行 installer；让 installer 退出后再启动 headless。

Compose 中 installer 与 headless 均固定镜像 `ghcr.io/lumidust/turbotick-headless:0.6.0`。编辑 `.env` 中的以下配置；镜像更新时两个模块必须同时使用同一 image 引用：

```dotenv
TURBOTICK_UID=10001
TURBOTICK_GID=10001
TURBOTICK_BIND_ADDRESS=0.0.0.0
TURBOTICK_PUBLISHED_PORT=30572
TURBOTICK_GAME_BRANCH=unstable
TURBOTICK_EXPECTED_GAME_VERSION=1.7.BUILD
STEAM_USER=
```

`TURBOTICK_GAME_DIR`、`TURBOTICK_PROFILE_DIR`、`TURBOTICK_STATE_DIR`、`TURBOTICK_STEAM_CACHE_DIR` 默认即上表四目录。账号留空会交互读取，也可填 `ACCOUNT`；密码和 Steam Guard 码不进入 `.env`。

`client-headless` 下载 AppId **294100**，默认分支 **unstable**；分支会移动，以安装后 `Version.txt` 的完整首行为准。执行前把 `1.7.BUILD` 替换为所有客户端已确认的版本；需要精确到 revision 时使用完整首行，例如 `TURBOTICK_EXPECTED_GAME_VERSION="1.7.5000 rev1275"`。这里的版本例子不是对未来分支下载内容的保证。

在附件根目录执行，先查看合并后的配置：

```bash
docker compose config
docker compose pull
docker compose --profile setup run --rm installer --help
```

### 3. 可选代理：只传给 installer

容器内 `127.0.0.1` 指向容器本身，宿主仅监听回环的代理不会自动可达。可将代理监听在容器可达的宿主地址，并使用 `http://PROXY_HOST:PROXY_PORT`；Linux Docker 也可用 `host.docker.internal:host-gateway` 映射。

若使用宿主可达的局域网地址，在 `.env` 中设置以下键即可；Compose 只传给 installer：

```dotenv
INSTALL_HTTP_PROXY=http://PROXY_HOST:PROXY_PORT
INSTALL_HTTPS_PROXY=http://PROXY_HOST:PROXY_PORT
INSTALL_ALL_PROXY=http://PROXY_HOST:PROXY_PORT
```

若使用 host-gateway，则为 installer 增加以下 override，例如 `compose.proxy.yml`；运行安装命令时显式合并，日常运行只用原 Compose：

```yaml
services:
  installer:
    extra_hosts:
      - "host.docker.internal:host-gateway"
    environment:
      HTTP_PROXY: "http://host.docker.internal:PROXY_PORT"
      HTTPS_PROXY: "http://host.docker.internal:PROXY_PORT"
      ALL_PROXY: "http://host.docker.internal:PROXY_PORT"
```

```bash
docker compose -f docker-compose.yml -f compose.proxy.yml --profile setup \
  run --rm installer install --user ACCOUNT
```

也可把 `host.docker.internal` 替换为宿主局域网地址 `PROXY_HOST`。代理软件须监听该地址并允许容器网络访问。代理变量只作用于安装阶段的 SteamCMD 子进程，SteamCMD 采用何种下载传输由自身决定；不要把账号、代理凭据或 Guard 码带进日常 headless 运行环境。

### 4. 首次交互登录、安装游戏和 Harmony

在真实交互终端中运行，保持 stdin 与 TTY：

```bash
docker compose --profile setup run --rm installer
```

安装器读取 Steam 账号，也可显式传入：

```bash
docker compose --profile setup run --rm installer install --user ACCOUNT
```

密码、Steam 手机确认、邮件验证码或令牌码由 **SteamCMD 原生提示**处理，按提示完成；不承诺二维码登录。首次认证成功后缓存保留在 `data/steam-cache`，后续更新沿用该目录、运行身份与账号。密码和 Guard 码不需要放入 Compose、`.env`、镜像层或运行参数。缓存过期时重新交互认证；玩家不需要拿到安装账号缓存。

安装器下载所选 Linux 游戏并检查可执行文件、`Version.txt`、四个 1.7 程序集、`Assets.zip` 和 `Data/Core`，再从官方工坊 **2009463077** 获取**完整 Harmony Mod 包**，部署镜像自带的 TurboTick。Harmony 应是独立 `Mods/Harmony/About/About.xml`，不只复制一个 `0Harmony.dll`，也不把它塞进 TurboTick 的程序集目录。

新建 profile 的初始 `Config/ModsConfig.xml` 按以下顺序启用基础组合：

```xml
<activeMods>
  <li>ludeon.rimworld</li>
  <li>brrainz.harmony</li>
  <li>lumidust.turbotick</li>
</activeMods>
```

已有配置与存档始终保留；重新安装不会替换已有 `ModsConfig.xml`，需要加入 DLC 或模组时由服主核对后编辑。已有 Harmony 默认保留；安装器会完整替换 TurboTick 为本镜像同字节的包，清除旧包残留文件。

以后显式更新游戏并复用账号缓存：

```bash
docker compose stop -t 90 headless
docker compose --profile setup run --rm installer install --user ACCOUNT
```

需要更新官方 Harmony 时，在已备份的独立副本中额外传 `--update-harmony`，并为全部客户端部署相同的 Harmony 包：

```bash
docker compose --profile setup run --rm installer install \
  --user ACCOUNT --update-harmony
```

`install` 的其他选项为 `--branch NAME`、`--expected-version V`、`--no-validate` 与 `--skip-download`。追加选项时示例显式写出 `install`：Compose 的 `run SERVICE COMMAND ...` 会替换原 command，而不是自动把参数追加到它后面。`--skip-download` 与 `--update-harmony` 互斥；Steam 密码/Guard 环境变量不用于此容器入口。

若已准备好且匹配版本的 **Linux** 游戏与完整 Harmony，把它们放入 `data/game`，保证可执行权限和 UID/GID 后，可跳过游戏/Harmony下载：

```bash
docker compose --profile setup run --rm installer install --skip-download
```

该模式仍检查布局、版本及 Harmony，准备 TurboTick 与缺失的初始配置；不登录 Steam。Windows 游戏文件不替代 Linux 可执行文件。每次维护的可用选项以同版本 `installer --help` 为准。

### 5. 启动、端口与健康检查

Compose 为 `/tmp` 设置可写、`exec` 的 tmpfs。Harmony/MonoMod 会在临时目录生成并加载原生辅助库；Docker tmpfs 默认的 `noexec` 会导致 `mm-exhelper.so: failed to map segment`。自定义 `docker run` 时保留 `--tmpfs /tmp:rw,exec,nosuid,nodev,size=512m,mode=1777`，避免仅设可写却遗漏执行映射。

```bash
docker compose up -d headless
docker compose ps
docker compose logs --tail=100 headless
docker compose exec headless cat /state/headless-status.json
```

`headless` 是默认 runtime，执行 run 时不自动更新游戏、不访问 Steam 登录、不挂载 `/steam` 缓存。Compose 只映射 **30572/TCP**；路由器/NAT、防火墙和云防火墙也应允许玩家访问 `SERVER_HOST:30572`。游戏与管理连接共用该端口，当前未内置 TLS；链路加密可通过 VPN 或隧道承载。

健康检查读取 `/state/headless-status.json`，要求状态新鲜、`connected=true`、state 属于可运行状态且 `failure` 为空；默认状态最大年龄 30 秒，可用 `TURBOTICK_HEALTH_MAX_AGE_SECONDS` 调整。镜像每 15 秒检查，预留 120 秒启动期。初次加载要留出时间，结合 `docker compose ps`、日志中的加载/监听结果和状态文件的 state、phase、connected 及 failure 判断。健康状态通过后进行真实客户端连接与晚加入验证。

空服启动后，在客户端进入「多人游戏 → 主持游戏」，填入 `SERVER_HOST:30572`，申请房主，选已有存档、新建殖民地或当前单人殖民地并发布。其他玩家选「加入游戏」。已有持久化世界时使用原 state/profile 自动恢复。

正常停止：

```bash
docker compose stop -t 90 headless
```

Compose 向游戏发送 SIGTERM，并保留 **90 秒**让主线程提交最终检查点。等待退出成功再备份或更新；不要以 `kill -9` 或断电作为日常停服。`docker compose down` 移除容器和网络时 bind 目录仍在，维护时保留这些目录。

### 6. 管理密码只从运行时 secret 文件读取

管理权限独立于当前房主；未配置密码时管理入口关闭。准备一个仅运行身份可读的文件，示例用隐藏输入避免密码进入 shell 历史：

```bash
read -r -s -p 'TurboTick admin password: ' ADMIN_PASSWORD
printf '\n'
printf '%s\n' "$ADMIN_PASSWORD" > secrets/admin-password
unset ADMIN_PASSWORD
sudo chown 10001:10001 secrets/admin-password
sudo chmod 0400 secrets/admin-password
```

通过只读运行时挂载或 Compose secret，把文件提供为 `/run/secrets/turbotick-admin-password`，并仅给 headless 设置：

```yaml
services:
  headless:
    environment:
      TURBOTICK_ADMIN_PASSWORD_FILE: /run/secrets/turbotick-admin-password
    volumes:
      - ./secrets/admin-password:/run/secrets/turbotick-admin-password:ro
```

把这段保存为 `compose.admin.yml` 后，运行阶段始终合并该文件：

```bash
docker compose -f docker-compose.yml -f compose.admin.yml up -d headless
```

停止、查看日志、升级和回滚也使用相同的 `-f` 列表。文件末尾换行会移除，空文件或读取失败会使启动失败。密码不烘焙到镜像、不存入 `.env`，不传给 installer。在大厅「管理服务器」中输入同一地址及共享密码，可查看状态、确认归档旧局并重开空房间；客户端不保存密码。SteamID、玩家名、SteamCMD 认证及创建世界不授予此权限。

## 已有存档、DLC 与附加模组

### 导入存档

最直接的方法是在匹配客户端中加载 `.rws`，连接空 headless，取得房主后发布。也可在停服状态把存档复制到所选 profile 的 `Saves/WORLD.rws`；Docker 对应宿主 `data/profile/Saves/WORLD.rws`，核对所有权后在 `.env` 设置 `TURBOTICK_SAVE=WORLD`，再启动。原生进程则在运行环境设置该变量。

`TURBOTICK_SAVE` 只接受**无扩展名的名称**，仅在空房间首次导入。已有 state 中的持久化世界优先；该变量不会覆盖正在恢复的世界。需要换局时先备份，再通过已认证的管理入口归档重开，或使用全新独立 state/profile 试导入。确认导入完成后移除一次性导入配置。

### DLC 和附加模组

1. 正常停服并备份。通过 SteamCMD/已有游戏安装取得服务端需要的完整 DLC 内容，放入游戏相应 `Data` 目录；仅修改 `ModsConfig.xml` 不会安装 DLC。
2. 把完整第三方 Mod 根目录放进 `data/game/Mods/MOD_NAME`（原生部署为游戏 `Mods`）。保留 `About`、版本子目录、XML、程序集及其他内容，核对 Linux 文件名大小写。日常 `/game` 只读，必须在停服安装阶段或宿主侧维护。
3. 编辑 `profile/Config/ModsConfig.xml`：保留 Core/Harmony/TurboTick，并根据各 Mod 依赖设置相同 DLC、加载顺序。把相关 `Mod_*.xml` 等配置同步到服务器 profile；已有配置不会被安装器自动覆盖。
4. 服务端与每个玩家使用同一份 Mod 包、精确 GameBuild 和相同配置。在独立测试实例上加载存档、发布、加入、运行并正常停止恢复后再切换正式实例。

新增或更新 Mod 可能改变持久化世界兼容指纹。同步新文件后也不能直接假定旧 world 可恢复；按[升级流程](#备份版本固定升级与回滚)处理 `ModHashMismatch`。不要编辑状态文件的 hash 跳过检查。

## Linux 原生与 systemd

### 1. 准备游戏与 Mod

解压同一 headless 附件到 `/srv/turbotick/packages`，另建 `/srv/turbotick/game`、`/srv/turbotick/headless-profile` 与 `/srv/turbotick/headless-state`。安装 Bash、基本系统工具、SteamCMD 及所选游戏 build 的原生运行依赖；[SteamCMD 官方说明](https://developer.valvesoftware.com/wiki/SteamCMD)包含不同发行版的 32 位依赖和安装方式。安装器不自动安装宿主依赖，也不自动启动服务。

以下假设 SteamCMD 在 `/opt/steamcmd/steamcmd.sh`，由同一安装账号、同一 HOME 交互运行。先认证：

```bash
cd /srv/turbotick/packages
bash scripts/steamcmd/install-server.sh \
  --steamcmd /opt/steamcmd/steamcmd.sh --user ACCOUNT --login-only
```

SteamCMD 自行处理密码/手机/邮件/验证码，成功后复用相同可执行文件、操作系统账号及 HOME 的缓存。保持 `STEAM_PASS`、`STEAM_GUARD` 未设置。可选 `--proxy http://PROXY_HOST:PROXY_PORT` 仅设置 SteamCMD 子进程代理环境。

预检后安装：

```bash
bash scripts/steamcmd/install-server.sh --profile client-headless \
  --steamcmd /opt/steamcmd/steamcmd.sh --user ACCOUNT \
  --dir /srv/turbotick/game --branch unstable \
  --expected-version 1.7.BUILD \
  --mod-source /srv/turbotick/packages/TurboTick --mod-name TurboTick \
  --launch-arg -headless --launch-arg -turbotick-server --preflight
```

确认计划后去掉 `--preflight` 再运行。`--branch public` 选择默认分支，但仍须核对精确版本。安装成功后生成 `start-server.sh`，其 profile 固定在游戏目录的 `Saved`；本节 systemd 使用单独的 `headless-profile`，不要混用两套配置。

原生安装器的选项：

| 选项 | 行为 |
|---|---|
| `--preflight` / `--dry-run` | 参数/本地前提预检，不认证、不下载、不写文件 |
| `--check-only` | 仅检查已有游戏 layout/版本，不调用 SteamCMD、不写文件 |
| `--skip-download` | 校验已有游戏后准备 Mod、缺失配置和启动器，不调用 SteamCMD |
| `--no-validate` | 只略过 SteamCMD `validate`，本地 layout/版本检查仍执行 |
| `--mods-config PATH` | 只在尚无配置时复制初始配置，已有配置和存档保留 |

每个 `--launch-arg` 是一个字面 argv 项；显式参数替换默认数组，不做 shell 拆词或求值，旧 `LAUNCH_ARGS` 字符串已停用。仅 `-headless` 不启用 TurboTick 监听器，必须同时有 `-turbotick-server`。

原生 `--mod-source` 是覆盖式复制，不自动清理旧包已删除的文件；更新时先停服，在独立版本目录准备完整包后替换。SteamCMD 失败可能留下部分下载或旧启动器，应以本次退出码和完整校验结果判断安装成功。

原生安装器不代替 Harmony 下载，使用官方工坊完整包：

```bash
/opt/steamcmd/steamcmd.sh \
  +force_install_dir /srv/turbotick/game \
  +login ACCOUNT +workshop_download_item 294100 2009463077 validate +quit
```

按 SteamCMD 输出的实际下载目录，把**整个 Harmony Mod**复制到 `/srv/turbotick/game/Mods/Harmony`，确认其 `About/About.xml` 存在。原生安装器默认初始配置是 Core-only；将 `Saved/Config/ModsConfig.xml` 复制到 `/srv/turbotick/headless-profile/Config/ModsConfig.xml`，按上文 Core/Harmony/TurboTick 顺序启用，并复制匹配 DLC/第三方配置。已有配置不覆盖。

### 2. 创建运行账号并安装 systemd 模板

```bash
sudo useradd --system --home-dir /srv/turbotick/headless-profile \
  --shell /usr/sbin/nologin turbotick
sudo install -d -o turbotick -g turbotick -m 0750 \
  /srv/turbotick/headless-profile/Config /srv/turbotick/headless-state
if ! sudo test -e /srv/turbotick/headless-profile/Config/ModsConfig.xml; then
  sudo cp /srv/turbotick/game/Saved/Config/ModsConfig.xml \
    /srv/turbotick/headless-profile/Config/ModsConfig.xml
fi
sudo chown -R turbotick:turbotick \
  /srv/turbotick/headless-profile /srv/turbotick/headless-state
```

若账号或配置已存在，保留它们并调整权限；游戏目录需要该账号读取/执行权限。完成配置后，从附件根安装[systemd 模板](turbotick-headless.service)：

```bash
cd /srv/turbotick/packages
sudo cp deploy/turbotick-headless.service /etc/systemd/system/turbotick-headless.service
sudo systemctl daemon-reload
sudo systemctl enable --now turbotick-headless.service
systemctl status turbotick-headless.service
journalctl -u turbotick-headless.service -f
```

模板使用 `/srv/turbotick/game`、`headless-profile`、`headless-state`；如目录或可执行文件名不同，先修改 `WorkingDirectory`、`ExecStart`、`HOME`、`XDG_CONFIG_HOME` 与 `TURBOTICK_DATA`。可选环境文件 `/srv/turbotick/headless.env` 只放运行参数，例如 `TURBOTICK_ADMIN_PASSWORD_FILE=/srv/turbotick/secrets/admin-password`；密码文件应由运行身份读取。Steam 账号密码和 Guard 码不进入运行环境文件。

```bash
sudo systemctl stop turbotick-headless.service
```

模板以 SIGTERM 正常停止，`TimeoutStopSec=90` 留给最终检查点。停服后再替换游戏或 DLL。

## Windows 原生

Windows 使用匹配的 Windows 游戏，不使用 Linux Docker 镜像。安装 PowerShell 7 与 [SteamCMD](https://developer.valvesoftware.com/wiki/SteamCMD)，把 headless 附件解压到 `C:\Servers\TurboTick\packages`。本例 SteamCMD 位于 `C:\Servers\SteamCMD\steamcmd.exe`。

```powershell
Set-Location C:\Servers\TurboTick\packages
./scripts/steamcmd/install-server.ps1 `
  -SteamCmd 'C:\Servers\SteamCMD\steamcmd.exe' -User ACCOUNT -LoginOnly

./scripts/steamcmd/install-server.ps1 -Profile client-headless `
  -SteamCmd 'C:\Servers\SteamCMD\steamcmd.exe' -User ACCOUNT `
  -Dir 'C:\Servers\TurboTick\game' -Branch unstable `
  -ExpectedVersion '1.7.BUILD' `
  -ModSource 'C:\Servers\TurboTick\packages\TurboTick' -ModName TurboTick `
  -LaunchArgs @('-headless', '-turbotick-server') -Preflight
```

确认预检后移除 `-Preflight` 执行。SteamCMD 原生完成 Steam Guard，复用同一账号、可执行文件和用户目录缓存。`-Proxy 'http://PROXY_HOST:PROXY_PORT'` 是可选下载代理；`-CheckOnly`、`-SkipDownload`、`-NoValidate`、`-ModsConfig PATH` 与 Linux 原生选项对应。`-LaunchArgs` 是字面字符串数组。

单独下载完整 Harmony：

```powershell
& 'C:\Servers\SteamCMD\steamcmd.exe' `
  +force_install_dir 'C:\Servers\TurboTick\game' `
  +login ACCOUNT +workshop_download_item 294100 2009463077 validate +quit
```

以实际输出目录为准，把完整包复制为 `C:\Servers\TurboTick\game\Mods\Harmony`，启用 Core/Harmony/TurboTick 和匹配 DLC/模组，准备 profile。首次复制时才执行以下 `Copy-Item`，已有配置继续保留：

```powershell
New-Item -ItemType Directory -Force -Path `
  'C:\Servers\TurboTick\profile\Config', 'C:\Servers\TurboTick\state' | Out-Null
if (-not (Test-Path 'C:\Servers\TurboTick\profile\Config\ModsConfig.xml')) {
  Copy-Item 'C:\Servers\TurboTick\game\Saved\Config\ModsConfig.xml' `
    'C:\Servers\TurboTick\profile\Config\ModsConfig.xml'
}
Set-Location 'C:\Servers\TurboTick\game'
$env:TURBOTICK_HOST = '0.0.0.0'
$env:TURBOTICK_PORT = '30572'
$env:TURBOTICK_DATA = 'C:\Servers\TurboTick\state'
& ./RimWorld.exe -headless -turbotick-server `
  '-savedatafolder=C:\Servers\TurboTick\profile'
```

可执行文件名以检测结果为准。直接用生成的 `start-server.ps1` 时使用的是游戏 `Saved`，不要同时混用上例 profile。管理密码只设置 `TURBOTICK_ADMIN_PASSWORD_FILE` 指向运行身份可读的文件。放行 30572/TCP，观察状态和日志后再发布世界；Ctrl+C 正常停止并等待最终检查点完成，不立即关闭终端或结束进程。

## 运行参数与状态

| 环境变量 | 默认值与用途 |
|---|---|
| `TURBOTICK_HOST` | `0.0.0.0`；回环或通配监听地址，供内部常驻模拟接入 |
| `TURBOTICK_PORT` | `30572`；游戏和管理连接共用 TCP 端口 |
| `TURBOTICK_DATA` | 原生默认 `<profile>/TurboTickServer`；Docker 固定 `/state` |
| `TURBOTICK_SAVE` | 空；空房间首次导入 profile 中无扩展名的存档名称 |
| `TURBOTICK_CONTROLLER_ONLY` | `false`；设为 true 后仅服务器指定控制者可调整仿真设置 |
| `TURBOTICK_ADMIN_PASSWORD_FILE` | 空；运行时可读的共享管理密码文件路径 |

host、port、data、save、controller-only 也可用 `-turbotick-NAME=VALUE` 设置。每个实例使用独立 profile/state 和端口。游戏加载未完成、常驻模拟故障或内部连接断开时，服务器停止授予新 tick；时间线最多领先常驻模拟 6 tick。

状态目录中的 `headless-status.json` 记录 phase、常驻模拟连接、服务器/已执行 tick、`checkpointTick` 和故障；`server-state.json` 与 `Snapshots/` 是恢复数据，应作为整体保留。每 3000 个逻辑 tick 自动提交检查点，每 10 秒保存协议状态。正常停止再备份，不能把运行中复制的一半快照当作完整恢复点。

## 备份、版本固定、升级与回滚

### 停服备份并固定版本

保留四类材料：原 Release Mod 与部署附件/`SHA256SUMS`、精确游戏 build 与完整 DLC/模组包、profile/state 整体、Compose/运行配置及镜像引用。Steam cache 和管理密码另行受控备份，不作为公开附件。镜像用 `:0.6.0` 固定标签，需要不可变引用时记录 digest，并在 Compose 或 image override 中为 installer/headless **同时**填写 `ghcr.io/lumidust/turbotick-headless@sha256:IMAGE_DIGEST`；当前 `.env` 只配置路径、身份、端口等，不改变 image 字段。

Docker 示例：

```bash
cd /srv/turbotick
docker compose stop -t 90 headless
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
sudo tar --numeric-owner -czf "backups/turbotick-$STAMP.tar.gz" \
  data/game data/profile data/state docker-compose.yml .env
docker image inspect ghcr.io/lumidust/turbotick-headless:0.6.0 \
  --format '{{json .RepoDigests}}' > "backups/image-$STAMP.txt"
```

自建 override 也加入备份；备份中可能含私人存档与配置，不上传公开仓库。可用 `docker image save` 离线保存原镜像。游戏目录大时可以单独归档，但必须与 profile/state 的恢复点及包清单关联；至少保留一套可完整恢复的旧游戏和 Mod 字节，而不只写下工坊名称。

### 升级

1. 正常停服并备份。保留旧目录与镜像，不在唯一副本中直接更新。
2. 将新 Release/Compose/入口解压到独立版本目录，准备独立游戏/profile/state 副本，固定新镜像。运行对应版本 installer，在已保留的 Steam cache 中复用认证；游戏更新仍是显式安装操作，不发生在 `up` 时。
3. 确认游戏完整 build、Harmony、DLC、附加 Mod、加载顺序和配置，再把**同一份新 Release Mod 包**部署给全部玩家。已有 Harmony 默认保留，需升级时显式选择更新并为客户端保留相同包。
4. 测试实例使用 `30573` 等不同宿主端口和独立四目录，验证启动、恢复、连接、主持/加入、晚加入、正常停止与再次恢复。禁止两个实例同时打开同一 state/profile，也不要让测试实例连接正式玩家。
5. 新镜像只替换运行环境和 TurboTick 包，**不会迁移旧世界的兼容指纹**。若恢复出现 `ModHashMismatch`，恢复旧完整包继续原世界；需要迁到新组合时，先在旧匹配客户端导出 `.rws`，在隔离副本中以新组合验证加载后，向新空 headless 发布/导入。保留旧世界直至新实例恢复验收完成。
6. 验证通过后停掉测试与旧正式实例，再切换正式端口/配置；留下旧备份和明确版本清单。不要编辑 `server-state.json`、快照或 hash 字段来跳过兼容检查。

测试副本放在单独部署目录，例如 `/srv/turbotick-test`，其 `.env` 四目录只指向副本，并设置 `TURBOTICK_PUBLISHED_PORT=30573`。Compose 默认声明固定 project 名，因此测试的**每条命令还要加独立 `-p`**，避免把正式容器替换：

```bash
cd /srv/turbotick-test
docker compose -p turbotick-test --profile setup run --rm installer install --skip-download
docker compose -p turbotick-test up -d headless
docker compose -p turbotick-test logs --tail=100 headless
docker compose -p turbotick-test stop -t 90 headless
```

此例假定副本已有匹配 Linux 游戏与 Harmony；需要下载时去掉 `--skip-download`，按提示登录。若使用代理、密码或 image override，同样合并相应 `-f` 文件。

### 完整回滚

先正常停止新实例。保留其失败现场供排查，用**新的空恢复目录**解压旧备份，恢复原镜像引用、原部署配置、原游戏/DLC/完整模组包及同一恢复点的 profile/state，修复 UID/GID 权限。先在不同端口验证旧版本客户端能加入、世界 tick 与检查点正确，再切换正式端口。

仅把镜像标签改回旧值而继续使用新 state，或只恢复快照不恢复 profile/模组配置，均不是完整回滚。旧版本与新版本不要交替写入同一 state；回滚也意味着恢复到备份时刻的游戏进度。

## 从原生 systemd 迁移到 Docker

1. 记录当前完整 GameBuild、DLC、所有 Mod 的原包/指纹、加载顺序、配置和运行参数，保留原服务文件与目录备份。
2. `sudo systemctl stop turbotick-headless.service`，等待最终检查点和进程退出；停掉其他自启动入口，迁移期间原服务保持停止。
3. 按 Docker 步骤准备独立目录。把原 **profile 整体**复制到 `data/profile`，原 **state 整体**复制到 `data/state`。同为 Linux 且 build 完全相同时可复制原游戏到 `data/game`；否则显式安装对应 Linux build，并部署**原世界同字节的 TurboTick、Harmony 与其他 Mod 包**。
4. 将四目录所有权改为容器 UID/GID，运行时路径改成 `/profile`、`/state` 和可选 secret 路径。SteamCMD cache 仅用于 installer，不进入 run。
5. 在独立副本与不同宿主端口（例如 30573）验证。原生旧版 Mod 与 0.6.0 镜像不匹配时，先按升级流程完成存档导出/新世界发布验证；不要仅凭版本号或编辑 hash 强行恢复。
6. 验证连接、晚加入、正常停机与重启恢复后，停止测试实例，切到正式 30572。原生服务继续禁用，保留原目录和恢复材料以便回滚。

测试时旧服务与容器即使端口不同，也不共享 live profile/state。容器中的 `/game` 只读，原生安装后残留的启动器不用于 Docker run。

## 旧独立网络服迁移

≤0.5.1 的独立网络服是玩家模拟、服务器协调与保存上传检查点的旧架构。它的快照/协议目录不是可直接放入 headless `Saves` 的 `.rws` 世界；不要把整个旧存储挂载成新 `/state` 并期待自动转换。

1. 保留旧 Release 客户端、旧网络服附件/镜像和完整数据备份，先在原匹配版本中让客户端加入并读取旧世界。
2. 用旧匹配客户端导出/另存标准 RimWorld `.rws`，同时记录原 GameBuild、DLC、完整 Mod 包、顺序和配置；验证该存档能在原组合中加载。
3. 正常停止旧网络服。新建 headless 的游戏/profile/state；用独立副本、不同端口和目标版本配套 Mod 验证加载导出的存档，再由房主发布到空 headless，或通过 `Saves/WORLD.rws` 与 `TURBOTICK_SAVE=WORLD` 首次导入。
4. 验证加入、晚加入、检查点和重启恢复后再切正式地址。旧服务与旧数据保留为回退点，迁移失败时恢复旧版本及其整套备份。

## 故障排查

| 现象 | 检查与处理 |
|---|---|
| `no matching manifest` / `exec format error` | 确认宿主为 x86-64，镜像仅 linux/amd64；不要沿用旧网络镜像的 ARM 支持说明 |
| installer 写入 `Permission denied` | 确认四个 bind 目录已存在，并为 UID/GID 10001:10001 所有；自定义身份时同步 chown 已有内容 |
| installer 提示 deployment 锁冲突 | 正常停止 headless 并等待退出；确认无其他安装器/实例共用 state，勿删除正在持有的锁来并行安装 |
| Steam Guard 反复出现或缓存未复用 | 用交互终端完成原生提示；确认仍挂同一 `data/steam-cache`、同一身份及账号，缓存过期则重新认证 |
| 代理连不上 | 容器 127.0.0.1 不是宿主；使用可达的 `PROXY_HOST` 或 host-gateway，并检查代理监听地址和容器访问规则 |
| 缺少 layout、游戏版本不符 | 检查 installer 退出码与 SteamCMD 成功标记，核对 Linux 文件、执行权限和 `Version.txt`；分支名称不保证 build |
| 缺少 Harmony / 游戏 Mod 红字 | 确认完整官方工坊包位于 `Mods/Harmony`，正确版本的 `About`/LoadFolders/程序集均在；不要只复制 DLL |
| `ModHashMismatch` / build 不一致 | 对齐精确 GameBuild、DLC、Mod 文件字节、顺序和配置；恢复持久化世界对应包，或按升级流程新建世界，勿改 hash 绕过 |
| 容器启动但状态不健康 | 看 `docker compose logs` 与 `headless-status.json` 的 state/phase/connected/failure；检查初次加载时间、profile 权限和 Mod 加载，不只看 PID/端口 |
| 客户端连接失败 | 确认服务健康、宿主映射与防火墙 30572/TCP、玩家可达地址；localhost 只用于同机客户端 |
| 导入变量似乎无效 | 存档在 profile `Saves`，变量只填无扩展名名称；已有持久化世界会恢复原局，先归档或用新独立 state 试导入 |
| 重启后 world 指纹不符 | 镜像更新不迁移旧指纹；恢复原游戏/完整模组包/profile/state，或走导出/发布流程 |
| 密码文件读取失败 | 核对仅 run 挂载的路径、容器 UID/GID 和文件非空；确认 headless 的 `_FILE` 路径与实际 secret 一致 |
| 正常停止耗时较久 | 允许 90 秒最终检查点窗口，观察日志与退出结果；不要提前强杀，再检查存储空间与写权限 |

## 验证范围与未来 profile

基础 headless 已在 **1.7.5000 rev1275** 的 Windows 原生客户端与 Linux headless 上，用 Core/Harmony/TurboTick 匹配组合完成实机验证：tick **120–21780** 内 **334 个同 tick 共同采样**的状态哈希和 RNG 全部一致，覆盖晚加入、暂停恢复、外部玩家全部退出后继续推进、正常检查点停机和重启恢复。该结果限定于此 build、组合与测试时段；容器宿主环境、第三方 Mod 与长期运行应各自验收。

源码的[安装器离线测试](https://github.com/LumiDust/TurboTick/blob/main/scripts/steamcmd/test-install-server.ps1)验证参数、失败处理、文件布局、配置保留和字面 argv，不连接 Steam，也不代表实际下载或游戏验收：

```powershell
./scripts/steamcmd/test-install-server.ps1 -Bash 'C:\Program Files\Git\bin\bash.exe'
```

`official-server` 是原生 SteamCMD 安装器为未来应用保留的**显式 profile**，不是本版默认部署或另一种已支持产品。尚未内置经核实的官方专服 AppId、layout 或启动行为；须同时指定 AppId、必需路径、可执行文件及字面 argv，或显式空 argv：

```bash
bash scripts/steamcmd/install-server.sh --profile official-server \
  --app-id SERVER_APP_ID --steamcmd /opt/steamcmd/steamcmd.sh --user ACCOUNT \
  --dir /srv/turbotick/official/BUILD --branch public \
  --required-path server.pak --required-path runtime \
  --executable bin/OfficialServer \
  --launch-arg --data=/srv/turbotick/official-data --preflight
```

`SERVER_APP_ID` 替换为已核实的数字，示例 layout/参数按真实包更换。该 profile 不假定客户端 DLL 或 `Mods/Saved`，不自动追加 `-savedatafolder`，不套用客户端 Mod/ModsConfig 复制行为。仅替换 AppId 不证明兼容；真实文件、原生依赖、数据路径与 TurboTick 接入仍需验证。
