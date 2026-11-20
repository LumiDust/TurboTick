# TurboTick headless 容器部署（0.7.1）

本版是容器预览：入口与部署流程验证通过，随机存档长期锁步仍可能分叉，先在独立数据目录验证并保留旧包。稳定版 latest 不随预览更新。

镜像：`ghcr.io/lumidust/turbotick-headless:0.7.1`，仅 **linux/amd64**，基础镜像 Ubuntu 24.04。它启动原生 Linux RimWorld 常驻模拟，客户端仍参与锁步模拟。旧 `ghcr.io/lumidust/turbotick-server:0.5.1` 是已退役独立 .NET 网络服的历史镜像，保留原标签，不将新内容覆盖到旧标签。

镜像只服务**服务端**：玩家端 Mod 由 Steam 创意工坊订阅分发，镜像里的 TurboTick payload 与其同字节，
启动前会校验宿主 `/game/Mods/TurboTick` 与镜像一致。镜像仅包含官方 SteamCMD 小型 bootstrap、运行库、
部署脚本以及已构建的完整 TurboTick Mod。**原版游戏、DLC、Harmony、Steam 凭据均不在镜像内**；前两者由安装阶段准备，Harmony 从官方工坊独立下载。构建镜像不编译 Mod。游戏 build、完整 Mod 文件、加载顺序与配置必须同所有客户端一致。

本文命令在含根 Compose 文件的仓库/部署目录执行。已有真实实例使用独立目录、Compose project 名和端口，不向其挂载目录执行测试。

## 1. 获取部署文件并准备目录

部署需要 Docker Engine（Linux 容器）与 Compose v2。准备根 [Compose 文件](../../docker-compose.yml)、[环境模板](../../.env.example)，按需复制 [管理密码覆盖配置](compose.admin.example.yml)。运行用户默认 `10001:10001`，镜像不在启动时切到 root 或自动 chown 宿主文件。

```bash
cp .env.example .env
sudo install -d -m 0750 -o 10001 -g 10001 \
  ./data/game ./data/profile ./data/state ./data/steam-cache
chmod 600 .env
docker compose --env-file .env config --quiet
docker compose pull headless installer
```

`.env` 已由仓库规则忽略；模板无个人账号、路径或密码。根目录默认 bind 到 `./data/game`、`./data/profile`、`./data/state`、`./data/steam-cache`。修改路径或 UID/GID 时，先在宿主创建对应目录，再按 `.env` 数值配置所有者。若以非 root Docker 运行，可由宿主管理员提前创建这些目录。Compose 使用 `create_host_path: false`，避免 Docker 悄悄创建 root 所有的目录。

| 宿主变量 | 默认值 | 容器路径 / 使用阶段 |
|---|---|---|
| `TURBOTICK_GAME_DIR` | `./data/game` | `/game`；install 可写，run 只读 |
| `TURBOTICK_PROFILE_DIR` | `./data/profile` | `/profile`；两阶段可写，配置、Saves、日志 |
| `TURBOTICK_STATE_DIR` | `./data/state` | `/state`；两阶段可写，恢复数据与部署锁 |
| `TURBOTICK_STEAM_CACHE_DIR` | `./data/steam-cache` | `/steam`；仅 installer，认证与工坊缓存 |
| `TURBOTICK_UID` / `TURBOTICK_GID` | `10001` / `10001` | Compose 进程 UID/GID；四目录需匹配 |
| `TURBOTICK_BIND_ADDRESS` | `0.0.0.0` | 宿主监听地址 |
| `TURBOTICK_PUBLISHED_PORT` | `30572` | 宿主 TCP 端口；容器固定 30572 |

`headless` 不挂载 `/steam`，不传 Steam 账号环境，不在启动时下载或更新。它有 `init: true`、只读根文件系统和独立可写且允许原生库映射的 `/tmp`（tmpfs 必须显式 `exec`，Harmony/MonoMod 会从临时目录加载原生辅助库），通过 `/profile` 与 `/state` 持久化。`installer` 仅通过 `setup` profile 显式运行，使用相同镜像、可交互 stdin/tty，不发布端口。

## 1.5 游戏本体不在镜像里，且会被自动检测更新

镜像只带三样东西：发布流水线产出的 TurboTick payload、部署脚本、以及一份 SteamCMD bootstrap。
**RimWorld 本体、DLC、Harmony、存档、账号材料一律在宿主卷里**（`/game`、`/profile`、`/state`、`/steam`），
镜像内 `/game` 是空目录；`docker run --entrypoint bash <image> -c 'ls -A /game'` 应当没有任何输出。

Steam 发布新 build 时不会通知任何人，所以更新检测是一个显式服务：

```bash
# 一次性检查（写入 /state/game-update.json）
docker compose run --rm update-check check-update

# 常驻：每小时问一次（默认 3600 秒，可用 TURBOTICK_UPDATE_CHECK_SECONDS 改）
docker compose --profile watch up -d update-check
```

`/state/game-update.json` 的形状：

| 字段 | 含义 |
|---|---|
| `status` | `ok` 或 `unavailable`（后者表示这次没能问到 Steam） |
| `installedVersion` | `/game/Version.txt` 的第一行（例如 `1.7.5011`） |
| `installedBuildId` | `/game/steamapps/appmanifest_294100.acf` 里的 build id；复制来的游戏树没有这个文件，因此为 `null` |
| `latestBuildId` | SteamCMD `app_info_print 294100` 报出的 build id |
| `needsUpdate` | 已知两端 build id 时为布尔值；`installedBuildId` 未知时为 `null`（不猜） |
| `newBuildSinceLastCheck` | 与上一次检查相比是否出现了新的 build |
| `reason` | 只在 `unavailable` 时出现 |

检测失败不会清掉上一次的快照：失败时保留 `latestBuildId` 并写 `status: unavailable`，避免「文件看起来是新的，
其实是旧的」；`TURBOTICK_UPDATE_TIMEOUT_SECONDS`（默认 120）给 SteamCMD 设上限，否则没有出路的机器会卡在 TCP 重试里，
连状态文件都留不下。检测走 SteamCMD，需要容器能访问 Steam；在无法直连 Steam 的机器上把代理传给该服务
（`INSTALL_HTTPS_PROXY` / `INSTALL_ALL_PROXY`，与 install 阶段同一组变量）。

## 2. 显式 install：原生 SteamCMD 登录与安装

```bash
docker compose --profile setup run --rm installer install
```

先读账号名，再由 SteamCMD 原生提示密码、手机确认、邮件验证码或 Steam Guard；不脚本代答、不保证二维码入口。也可仅提供账号：

```bash
docker compose --profile setup run --rm installer install --user ACCOUNT
# 或本次仅给 installer 环境，不进入 runtime：
docker compose --profile setup run --rm -e STEAM_USER=ACCOUNT installer install
```

密码与 Guard 仅在原生交互中输入。入口发现 `STEAM_PASS` 或 `STEAM_GUARD` 会报错退出。认证缓存持久化在宿主 steam-cache 中；缓存复用沿用同一目录、UID/GID 与容器 HOME `/steam/home`。安装进程退场后，运行宿主不接触该目录。

入口将只读 `/opt/steamcmd-seed` 复制到 `/steam/steamcmd`，SteamCMD 后续自更新和凭据写入均发生在缓存中。官方安装器复制为 `/opt/turbotick/install-server.sh` 并实际执行，固定 AppId `294100`、Linux 平台、可执行文件 `RimWorld`。默认分支 `unstable`，默认版本要求 `1.7`；精确钉住实际完整版本：

```bash
docker compose --profile setup run --rm installer install --user ACCOUNT \
  --branch unstable --expected-version '1.7.BUILD revREVISION'
```

把 `.env` 中 `TURBOTICK_EXPECTED_GAME_VERSION` 同步为该值，使 run 也检查它。`Version.txt` 是实际版本依据；分支随发布变化。支持 `--no-validate` 省略 SteamCMD 的游戏 `validate`，本地布局和版本检查照常执行。

Harmony 单独执行 `+workshop_download_item 294100 2009463077 validate`，先下载到 `/steam` 下的缓存，再复制**完整目录**到 `/game/Mods/Harmony`。若已有 `packageId=brrainz.harmony` 的有效 1.7 安装（文件夹名可不同），默认原样保留；重复 Harmony 会报错。显式刷新：

```bash
docker compose stop headless
docker compose --profile setup run --rm installer install --user ACCOUNT --update-harmony
```

TurboTick 从镜像 `/opt/turbotick/mod/TurboTick` 完整复制到 `/game/Mods/TurboTick`，清除旧包残留并校验完整 SHA256 清单。不可只替换三个 DLL：XML、图片、语言和配置目录同样属于匹配 payload。

可选代理仅给 installer：在 `.env` 设置 `INSTALL_HTTP_PROXY`、`INSTALL_HTTPS_PROXY`、`INSTALL_ALL_PROXY`。使用容器实际可达的 `PROXY_HOST:PROXY_PORT`；容器中的 loopback 指向容器自身。SteamCMD 各传输协议是否采用代理由原生程序决定。runtime 不继承这些代理变量。

## 3. 使用已有游戏与 Harmony，完全跳过下载

先将匹配的完整 Linux 游戏与有效 Harmony 1.7 Mod 放入 `./data/game`，保证 `RimWorld` 有执行位，再执行：

```bash
docker compose --profile setup run --rm installer install --skip-download
```

此模式不调用任何 SteamCMD、不提示账号、不读写认证缓存；不需要 `/steam` 存在。Compose 的 installer 仍声明了缓存 bind，因此使用 Compose 时须准备空缓存目录；直接 `docker run` 可完全省略该挂载：

```bash
docker run --rm --platform linux/amd64 --user 10001:10001 \
  --mount type=bind,src="$PWD/data/game",dst=/game \
  --mount type=bind,src="$PWD/data/profile",dst=/profile \
  --mount type=bind,src="$PWD/data/state",dst=/state \
  ghcr.io/lumidust/turbotick-headless:0.7.1 install --skip-download
```

缺少游戏布局、执行位、Harmony 或写权限即报错。`--skip-download` 与 `--update-harmony` 互斥。

新建 profile 时生成 `/profile/Config/ModsConfig.xml`，顺序为 Core、Harmony、TurboTick，版本使用 `Version.txt` 的真实数字 token（例如 `1.7.5000`，去掉空格与 revision）。已有 profile 配置和 Saves 永不覆盖；已有配置若未启用必需项，预检会明确报错，由操作者编辑原配置。其他 DLC/Mod 与 `Mod_*.xml` 配置需与客户端一起部署。

上游安装器还会在游戏目录准备 `Saved/Config/ModsConfig.xml`、`start-server.sh` 与安装元数据；容器 run 始终直接使用 `/profile`，不执行该生成 launcher。上游对已有游戏 Saved 配置同样保留。

## 4. 默认 run、健康检查与正常停机

```bash
docker compose up -d headless
docker compose logs -f headless
docker compose ps
docker inspect --format '{{json .State.Health}}' "$(docker compose ps -q headless)"
```

run 在不调用 SteamCMD、curl 或缓存的情况下检查：

- 游戏 `Version.txt`、原生可执行 `RimWorld`、`Assembly-CSharp.dll`、`RimWorld.dll`、`Waylith.dll`、`UnityStubs.dll`、`Assets.zip`、`Data/Core`。
- TurboTick 的 About packageId/版本、LoadFolders 的 1.7 路由、三个非空 DLL，以及整个目录的文件/目录清单与 SHA256；旧包、缺项、改动与多余文件均失败。
- 独立 Harmony 的 packageId、1.7 LoadFolders 与相应 `0Harmony.dll`，profile 必需模组及顺序，profile/state 实际写权限。

然后最终执行，无 shell 字符串求值：

```text
exec /game/RimWorld -headless -turbotick-server -savedatafolder=/profile [逐项额外参数]
HOME=/profile
XDG_CONFIG_HOME=/profile/.config
TURBOTICK_DATA=/state
LD_LIBRARY_PATH=/game
```

额外游戏参数逐个作为字面 argv 传递；入口会阻止额外 `-savedatafolder` 或 `-turbotick-data` 改变固定挂载路径。例如隔离手工诊断可 `docker run ... IMAGE run -logfile /profile/Player.log`。正常实例使用默认命令。

install/run 都通过 `/state/deployment.lock` 的非阻塞 `flock` 互斥，运行锁的 FD 随最终 exec 保留到游戏退出。请让同一部署始终使用同一个 state；不同 state 无从保护共用 game/profile，应为每个实例使用各自四目录。锁占用即失败，入口不擅自终止占用者。运行时先移除旧状态文件，避免把上个进程的状态误认作新实例健康。

健康检查读取 `/state/headless-status.json`，要求 `connected=true`、`failure` 为空、状态属于 `lobby/playing/restoring/loading-initial-save/publishing-initial-world`；UTC 与文件 mtime 都需在最近 30 秒内，未来超过 5 秒同样失败。暂停与空大厅无需 tick 递增。检查每 15 秒一次、超时 5 秒、启动宽限 120 秒、连续失败 3 次转 unhealthy。Docker 的 unhealthy 标记本身不会自动重启进程；请先查看 status 和日志定位，再决定停机恢复。

```bash
docker compose stop headless
# 必要时移除容器，保留 bind 目录：
docker compose down
```

镜像 `STOPSIGNAL SIGTERM`，Compose `stop_grace_period: 90s`，`init` 将信号传给直接 exec 的游戏，游戏在提交最终检查点后退出。直接 `docker run` 常驻实例需加 `--init --stop-timeout 90`，或 `docker stop --time 90 CONTAINER`。避免在运行期间改动游戏/Mod 文件。

## 5. 环境与可选管理密码

| 名称 | 使用阶段 / 默认 / 语义 |
|---|---|
| `TURBOTICK_GAME_BRANCH` | install；`unstable`；等同 `--branch` |
| `TURBOTICK_EXPECTED_GAME_VERSION` | install/run；`1.7`；版本前缀或 Version.txt 完整首行 |
| `STEAM_USER` | 仅 install；空则交互读取账号；支持 `--user` |
| `TURBOTICK_HOST` / `TURBOTICK_PORT` | run；容器固定 `0.0.0.0` / `30572` |
| `TURBOTICK_DATA` | run；入口固定 `/state` |
| `HOME` / `XDG_CONFIG_HOME` | run；入口固定 `/profile` / `/profile/.config` |
| `TURBOTICK_SAVE` | run；空；首次空房间导入 `/profile/Saves/NAME.rws`，只填无扩展名的 NAME |
| `TURBOTICK_CONTROLLER_ONLY` | run；`false`；由服务器确认仿真设置权限 |
| `TURBOTICK_HEALTH_MAX_AGE_SECONDS` | health；`30`；正整数，检查状态文件新鲜度 |
| `TURBOTICK_ADMIN_PASSWORD_FILE` | run；可选；容器中可读的共享管理密码文件 |
| `INSTALL_HTTP_PROXY` / `INSTALL_HTTPS_PROXY` / `INSTALL_ALL_PROXY` | Compose installer；映射为原生代理环境 |

启用共享管理密码的示例（使用任意与部署目录分离的宿主文件路径）：

```bash
# 文件由操作者在宿主准备，内容仅为管理密码，授权运行 UID 读取。
export TURBOTICK_ADMIN_PASSWORD_HOST_FILE=/ABSOLUTE/PATH/admin-password
docker compose -f docker-compose.yml -f deploy/container/compose.admin.example.yml config --quiet
docker compose -f docker-compose.yml -f deploy/container/compose.admin.example.yml up -d headless
```

文件为空、不可读或格式不合要求会由 headless 启动报错；Steam 登录不授予游戏管理权限。

## 6. 更新、备份和从历史网络服迁移

1. 先 `docker compose stop headless`，等待正常检查点退出；备份完整 state/profile、对应游戏 build 与完整 Mod 组合。`server-state.json`、`Snapshots/` 与 profile 应作为匹配的一组保存。
2. 为新版本使用独立部署目录和 game/profile/state/cache；让所有客户端使用同一 Release Mod 字节与游戏 build。保留旧镜像标签与旧目录用于回滚。
3. 在新目录 pull 指定标签，执行显式 install（已有游戏可用 `--skip-download`）。需要更新 Harmony 时明确传 `--update-harmony`；run 不隐式更新。
4. 核对预检输出、日志和健康状态。验证完成后再切换服务端口；回滚恢复整组匹配数据与旧镜像。

原 `turbotick-server:0.5.1` 的 `/data` 是历史网络服数据，当前部署使用新的 `/state`、`/profile` 与原版游戏。不要仅替换旧容器镜像并继续把 `/data` 当作 headless profile；迁移先保留原状态，在新目录按完整匹配组合验证恢复。新 Compose project 名为 `turbotick-headless`，不会用相同服务名替换旧网络服；同端口正式切换需由操作者先停旧实例。

## 7. 镜像构建与发布接口

Release/CI 准备 **已构建且与本版本 Release 完全相同**的完整目录 `artifacts/container-mod/TurboTick`。其 About 版本必须 `0.7.1`，并包含 `About/About.xml`、`LoadFolders.xml`、`1.7/Assemblies/TurboTick.dll`、`TurboTick.Networking.dll`、`TurboTick.Simulation.dll` 及所有资源。该目录不是镜像内重新编译的产物。

```bash
# CI 已把 Release Mod 解包到 artifacts/container-mod/TurboTick 后：
docker buildx build --platform linux/amd64 --load \
  -t ghcr.io/lumidust/turbotick-headless:0.7.1 .
bash scripts/test-container.sh ghcr.io/lumidust/turbotick-headless:0.7.1
# 发布由 Release 流程执行；不会改写 turbotick-server:0.5.1。
docker push ghcr.io/lumidust/turbotick-headless:0.7.1
```

Docker context 为 allowlist，仅包含该完整 payload、容器脚本、现有 SteamCMD 安装器与 LICENSE；排除源码、游戏、Harmony、`.env` 与缓存。镜像构建生成 `/opt/turbotick/payload.sha256.json`，用于完整目录核对，原 payload 字节不变。

Ubuntu 24.04 显式安装依赖（其余传递依赖由 apt 解析）：

| 用途 | 包 |
|---|---|
| 工具/入口/健康 | `bash ca-certificates curl tar gzip coreutils util-linux python3`；`flock` 来自 util-linux |
| SteamCMD 32 位 bootstrap | `libc6-i386 lib32gcc-s1 lib32stdc++6` |
| LinuxGame / .NET 10 | `libc6 libgcc-s1 libstdc++6 libicu74 libssl3t64 zlib1g libgssapi-krb5-2 liblttng-ust1t64` |
| Waylith 图形/窗口原生依赖 | `libvulkan1 mesa-vulkan-drivers libgl1 libegl1 libglfw3 libx11-6 libxext6 libxrandr2 libxinerama1 libxcursor1 libxi6 libxrender1 libxkbcommon0 libwayland-client0` |
| 音频/输入/字体原生依赖 | `libasound2t64 libsdl2-2.0-0 libfreetype6 fontconfig fonts-dejavu-core` |

虽然 headless 不创建游戏窗口，原生库解析仍需随游戏 build 核对；未使用 Xvfb。准备实际游戏后可在停服状态只读检查：

```bash
docker compose run --rm --no-deps --entrypoint bash headless -lc \
  'export LD_LIBRARY_PATH=/game; ldd /game/RimWorld; find /game -maxdepth 2 -name "*.so*" -type f ! -name "*.debug" -exec ldd {} \;'
```

关注 `not found`，必要时与同 build 的原生发布清单对照。上表包含动态加载项；`ldd` 单看主可执行文件不覆盖所有 Waylith 或 .NET native 库。

## 8. 离线验证

[test-container.sh](../../scripts/test-container.sh) 要求镜像已在本地，不拉镜像、不连网，所有测试容器使用 `--network none`，以随机命名卷和 fake Game/SteamCMD/Harmony 验证入口契约，结束自动清理。它验证字面 argv、固定 HOME/state、runtime 无认证缓存、原生退出码、旧包与额外文件拒绝、skip-download 无 SteamCMD、Harmony 保留/显式完整更新、保存配置/存档、数字版本、部署锁、健康新鲜度、SIGTERM 和写权限。

```bash
bash scripts/test-container.sh
```

通过时结尾为 `All offline container contract tests passed.`。fake 测试不证明真实游戏 build 的锁步模拟；实际验收还需同 build 客户端加入、共同采样、空服推进与正常停机/重启恢复。
