# TurboTick

[![Build](https://github.com/LumiDust/TurboTick/actions/workflows/build.yml/badge.svg)](https://github.com/LumiDust/TurboTick/blob/main/.github/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**0.7.1 · RimWorld 1.7 · 网络协议 v5**

共享殖民地联机 Mod。玩家端是**客户端 Mod**（Steam 创意工坊分发），服务端是**容器镜像**：镜像里跑一份加载真实
RimWorld 的**权威副本**，世界在服务器上推进，玩家端副本按状态通道逐项跟随（小人、物品、世界物件、尸体追踪、
消息与档案、视觉载体、方案表……），动画与寻路仍是原生行为。游戏端依赖
[Harmony 3](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)，不依赖其他联机 Mod。

## 谁从哪里拿

| 谁 | 从哪拿 | 说明 |
|---|---|---|
| **玩家** | [Steam 创意工坊 3834465013](https://steamcommunity.com/sharedfiles/filedetails/?id=3834465013) | 订阅即可，版本由 Steam 更新；**不要**手工塞 zip，否则容易和专服版本不一致 |
| **服主** | `ghcr.io/lumidust/turbotick-headless:0.7.1`（linux/amd64） | 服务端镜像，**不含游戏本体**；游戏、Harmony、存档都在宿主卷里 |
| 离线 / 固定版本 | Release 里的 `TurboTick-0.7.1-mod-1.7.zip` | 与镜像 payload **同字节**；只在不能连工坊（离线、锁版本、自建分发）时用 |
| 无游戏本体的旧形态 | `TurboTick-0.7.1-coordinator-linux-x64` | 0.6.1–0.7.0 的协调器，供旧局或不装游戏的机器回退 |

一句话：**客户端走工坊，服务端走镜像**。Release 里的 `mod-1.7.zip` 是服务端 payload 与离线玩家的同一份字节，
不是玩家的常规获取途径。

## 专服形态：镜像里的权威副本

| 方案 | 模拟器数量 | 动画与寻路 | 需要同步的份数 |
|---|---|---|---|
| **权威副本 + 每端副本**（0.7.1 默认） | 1 个权威 + N 个副本 | 原生正确 | N 份状态差量 |
| 协调器 + 每端模拟（0.6.1–0.7.0） | N | 原生正确 | N 份命令与回执 |

镜像只装服务端软件：TurboTick payload、部署脚本、SteamCMD bootstrap。`/game` 在镜像内是**空目录**，
RimWorld 本体、DLC、Harmony、存档、账号材料一律由宿主卷提供。启动前会逐文件校验 `Mods/TurboTick`
与镜像内容一致，缺失/改动/多余都要求停服重装。

## 服务端部署（镜像）

```sh
# 0) 前提：x86-64 Linux、Docker Engine + Compose v2、能下载 RimWorld 的 Steam 账号
mkdir -p /srv/turbotick/data/{game,profile,state,steam-cache}
chown -R 10001:10001 /srv/turbotick/data

# 1) 取部署文件（Release 里的 headless-installer.zip，或仓库根 docker-compose.yml + .env.example）
cd /srv/turbotick
unzip TurboTick-0.7.1-headless-installer.zip
cp .env.example .env          # 目录、端口、UID/GID、可选代理

# 2) 校验配置
docker compose --env-file .env config --quiet

# 3) 首次安装：SteamCMD 登录 → 装 RimWorld + Harmony（交互式，密码/令牌只进 SteamCMD 自己的提示）
docker compose --profile setup run --rm installer install

# 4) 起服
docker compose up -d headless

# 5) 游戏更新检测（可选但推荐）：每小时问一次 Steam，结果写 /state/game-update.json
docker compose --profile watch up -d update-check
```

- `/game`：RimWorld 本体 + `Mods/Harmony`（run 阶段只读）；`/profile`：配置与存档；`/state`：运行状态与更新状态。
- 游戏有新 build 时：停服 → `docker compose --profile setup run --rm installer install --user ACCOUNT` → 起服。
- 换版本镜像：`TURBOTICK_IMAGE=ghcr.io/lumidust/turbotick-headless:<版本>` 后 `docker compose up -d headless`，
  payload 不一致会拒绝启动并提示重装。
- 本地构建（无 ghcr 出口时）：`docker build -t turbotick-headless:0.7.1 .`，再把 `TURBOTICK_IMAGE` 指向该标签；
  或者在有出口的机器 `docker save` 后到目标机 `docker load`，见 [deploy/README.md](deploy/README.md) 的「无外网出口时离线导入镜像」。
- 完整变量表、目录权限、文件清单与回滚：见 [`deploy/container/README.md`](deploy/container/README.md)。

协调器（旧形态，不需要游戏本体）：`deploy/turbotick-coordinator.service` + `TurboTick-0.7.1-coordinator-linux-x64`，
参数见 `--help`。

## 客户端安装

1. 订阅工坊 [Harmony 2009463077](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077) 与
   [TurboTick 3834465013](https://steamcommunity.com/sharedfiles/filedetails/?id=3834465013)。
2. 模组顺序与专服保持一致（专服的 `ModsConfig.xml` 在 `/profile/Config`，可对照）。
3. 游戏内「多人游戏 → 加入游戏」，填 `服务器地址:30572`。

## 维护者：工坊与镜像的发布链

客户端只有一条常规发布途径——工坊：`scripts/prepare-workshop.ps1` 从 Release 的 mod zip 生成工坊目录，
按 [`docs/STEAM_WORKSHOP.md`](docs/STEAM_WORKSHOP.md) 上传；玩家订阅后由 Steam 分发与更新。
服务端只有一条常规发布途径——镜像：CI 在打 `v*` 标签时用**同一份** mod zip 做 payload 构建
`ghcr.io/lumidust/turbotick-headless:<版本>` 并跑容器契约测试，两条途径因此永远是同字节。

## 主持与加入

1. 启动协调器，确认监听端口可达；所有玩家连接同一地址，例如 `SERVER_HOST:30572`。
2. 首次开局时，主持者进入「多人游戏 → 主持游戏」，填写地址和端口，连接并申请房主身份。
3. 房主选择已有存档、新建殖民地或当前单人殖民地，把世界发布到该专服。
4. 其他玩家「加入游戏」连接同一地址，自动下载快照并进入。

暂停与速度使用原版按钮和快捷键，由专服确认后同步。可折叠玩家面板显示身份、延迟、地图和同步状态；同一地图能看到其他玩家的彩色指针与名字。

有人加入时全体玩家会从同一份检查点重载一次，这是刻意的：Verse 存档不会序列化全部临时缓存，一个从世界创建起一直跑到现在的进程和一个刚反序列化的进程天然会有细微差别。重载把所有人放到同一个加载点，之后的重放量只有个位数 tick。

## 同步覆盖

已实现专用命令（紧凑载荷 + 定点应用）：标记与取消、建造蓝图与取消、小人征召、右键指令、禁用、工作优先级、区域增删、配方增删、活动区分配、运输舱装载。

其余界面操作走**通用回退**：`Command_*` 的 `ProcessInput` 在点击前后各序列化一次选中目标的 Scribe 状态，发生变化就整段下发。一条路径覆盖开光开关、过滤器、床归属、种植物、技能与仪式、燃料下限等，以及第三方 Mod 自己添加的 gizmo——传输格式就是存档格式，所以「能存档就能同步」，Mod 作者无需改动。

服务端拒绝一次操作时会**明确告诉玩家**（未知动作、限流、房间未运行、载荷无效），而不是只写进协调器日志；同一类拒绝在 2 秒窗口内合并成一条并注明折叠次数，所以一次被拒的多格拖拽只是一行提示。面板同时显示本端落后多少 tick，并把房间刻意的管道前推（`SimulationLeadTicks`）从落后量里扣掉。

检测到两端状态不一致时**先暂停并指出类别**（位置／血量／工作／物体／标记／随机流），由玩家决定是否退回检查点，而不是静默回滚。若某一端的共享随机流不一致，类别里会直接出现「随机」。

## 版本、权限与连接

- 客户端与专服使用 **0.7.0 / 协议 v5**；所有玩家与专服必须使用同一份 Mod 字节。
- 握手校验**精确 GameBuild、DLC、模组内容、加载顺序和 Mod 配置**，包括程序集与 XML。仅「都是 1.7」、版本显示相同或模组同名仍可能得到 `ModHashMismatch`。同一 packageId 装了两份（例如本地 `Mods/Harmony` 与工坊 Harmony）会让 packageId 被加上 `_steam` 后缀，两端不一致就会握手失败。
- 房主、倍率控制者、检查点所有者由服务器管理；房主退出后，检查点归属自动移交给剩余玩家，世界继续推进。
- 默认端口 **30572/TCP**。公网直连需要可达地址、端口映射与防火墙放行；游戏与管理共用该 TCP 通道且未内置 TLS，需要链路加密时请用 VPN 或隧道承载。
- 默认不启用「快速生态」：该开发选项让每个 tick 前进 2000，会直接破坏共享时间线，共享世界会强制关闭它并记录一次告警。

## 已验证范围

下表只写**有日志或截图可查**的结论，每条都带日期与证据位置。旧的"零分叉"结论已作废：那批验收用的通用 gizmo 回灌会把同一个物体重复登记进 `listerThings`（详见 `docs/audit/FIXES.md` F7），两端被同样污染，看起来一致其实是同一个错误。

跨平台确定性的完整定位记录（收敛路径、被证伪的假设、失败过的改法、两个会静默失效的 Harmony 坑）见 [`docs/audit/DETERMINISM.md`](docs/audit/DETERMINISM.md)。

双端验收环境：Windows 客户端（2560×1440）+ `192.168.0.29` 的 Xvfb `:99` GUI 客户端（1600×900），协调器在独立 VPS，全部使用 Core + Harmony + TurboTick。

| 场景 | 结果 | 证据 |
|---|---|---|
| 双端 GUI 同局运行 | 两端 `In sync`，59 / 57 TPS，同一殖民地，能看到对方的彩色指针与选区 | `artifacts/audit/verify-lab/win-final.png`、`ubuntu-final.png` |
| 无输入长跑 | 48 460 tick，零分叉（13 分钟） | `verify-lab/logs/coordinator-f20-r*.log`、`VERIFY-LAB.md` §7.9 |
| 脚本化操作 | 一轮 22 772 tick 零分叉；三轮里两轮在 3 000–12 600 tick 出现一次分叉 | `VERIFY-LAB.md` §7.8、§7.10 |
| 晚加入 | 一次规范化重载后追平；重载期间另一端不再被冻住 | `VERIFY-LAB.md` §7.6 |
| 断线重连 | 从检查点续上，零分叉至 31 200 tick（0.6.1 期数据） | 历史记录，未在本轮复测 |
| 房主退出 | 剩余玩家继续推进并接管房主（0.6.1 期数据） | 历史记录，未在本轮复测 |
| 存档完整性 | `Saving Thing with already-used ID` **0 / 0 / 0**（修复前 25 → 50 线性增长） | `VERIFY-LAB.md` §7.4 |
| 检查点唯一性 | 同一 tick 只出现一次 `checkpoint accepted`（四轮） | `VERIFY-LAB.md` §7.8 |
| 逐条命令对账 | `迟到命令` 0 条（多轮） | `VERIFY-LAB.md` §7.5 |

单元测试 73 项、网络协议测试 30 组全绿（`dotnet run --project Tests/TurboTick.Tests.csproj`）。客户端启动时会自检补丁挂载并输出一行汇总；任何「已声明但未安装」的补丁会点名，通用 gizmo 捕获若没挂上任何入口也会显式告警；解析不到的 gizmo 类型也会单独点名。

已确认的限制：

- **间歇分叉的根因已定位并修复（0.7.0）**。根因是 **def XML 的加载顺序**：`DirectXmlLoader.XmlAssetsInModFolder` 用 `DirectoryInfo.GetFiles` 枚举文件，NTFS 与 ext4 给出的顺序不同，于是两端 `DefDatabase<InteractionDef>` 里同样 16 个 def 的排列从第 6 位起整体互换。`Pawn_InteractionsTracker.TryInteractRandomly` 用 `TryRandomElementByWeight` 遍历该列表，**同一个随机值因此落到不同的 def**，两条分支消耗的抽取次数不同，共享随机流从此错位——一株植物的种类、一只动物的等待时长、一个 pawn 的目标格都会在几千 tick 后跟着变。
  修复方式是在 mod 初始化时把那份列表原地按 `defName` 排序（`Source/InteractionDefOrder.cs` 的 `DefLoadOrder.Apply`）。不打补丁，因此不存在"装得太晚"的问题；只碰一个类型，因此不会波及其它泛型实例。
  修复后的验证：**两轮 30 分钟级长跑**（58 585 tick 与 119 214 tick）零分叉，每 600 tick 一条的探针快照合并 **196/196 条逐字相同**（快照内容含位置、健康、job、漫游奇偶位、think tick、目标格、过期区间、派系、饥饿度、giver 名、随机流计数与分段计数）。修复前后的自证：两端 `before=`（排序前的加载顺序）**不同**，而 `head=`（排序后）**逐字相同**。
- **残余：低频单 tick 相位差**（已定性，未消除）。某 tick 一端把 5 件物品瞬态解禁、另一端没有，随后各自恢复；两轮实测的受影响 id 形状相同（连续 4 个 + 单独 1 个），符合"一次分裂产生 4 个堆"的签名。
  - **不是持续状态差异**：共同采样点 27 / 24 / 15 个，**逐点相同，0 差异**；"≥3 连续采样点出现差异"的轮次比例为 **0/3**。
  - **不是随机流问题**：分叉帧 `rng` / `draws` / `iter` 两端逐字相同 —— 两端消耗同样多的随机数，只是对**不同目标**执行了同一步操作。
  - **但它不是"仅影响 job 选择"**：实测一次分叉里，一个 `nourishment = 0` 的殖民者在一端吃到了饭（正好是那 5 件中的一件）、另一端食物搜索失败转而躺下，流差因此拉开 12 抽。**它会改变一次进食等具体行为。**
  - **判断**：这是 job 选择差异的下游后果；job 选择本身已修两层（work giver 顺序、`ListerHaulables` 的输入）。它不产生持续差异、不损坏存档，且分叉检测 + 检查点回退可用。
  - 频率约每 10 万 tick 一次；十次观测落在五条不同路径上。完整的定位记录与被证伪的假设见 [`docs/audit/DETERMINISM.md`](docs/audit/DETERMINISM.md)。
- 第三方 Mod 若使用 `UnityEngine.Random`，其行为无法被确定化——该 API 全部是 `extern`，没有可供拦截的托管方法体。共享随机流只覆盖 Verse 的 `Rand`。
- 交易、远行队默认限制房主操作；运输舱同步为实验开关。
- 真实鼠标点击这一跳现由自动验收覆盖（`-turbotick-acceptance` 会合成 `ProcessInput` 事件），但**合成事件不经窗口命中测试**，所以"UI 布局把 gizmo 挪到别处"这类问题仍需人工看一次。











