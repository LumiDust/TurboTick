# TurboTick

[![Build](https://github.com/LumiDust/TurboTick/actions/workflows/build.yml/badge.svg)](https://github.com/LumiDust/TurboTick/blob/main/.github/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**0.6.1 协调器架构 · RimWorld 1.7 · 网络协议 v5**

独立的共享殖民地联机 Mod，支持多人共同操作同一世界。由**客户端 Mod**和**协调器专服**组成：每个玩家在本地完整模拟这份殖民地，专服只排定操作顺序、按最慢玩家的进度推进时间线并保存快照，自身不跑殖民地模拟。游戏端依赖 [Harmony 3](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)；不依赖其他联机 Mod。

## 为什么是协调器而不是权威服务器

| 方案 | 模拟器数量 | 动画与寻路 | 需要同步的份数 |
|---|---|---|---|
| **协调器 + 每端模拟**（本版） | N | 原生正确 | N |
| 服务器唯一模拟 + 显示副本 | 1 | 做不到：冻结的 tick 没有可绘制的内容，位置只能按帧硬写回 | 0，但要为渲染另造一套 |
| 服务器也模拟（锁步） | N+1 | 原生正确 | N+1 |

服务器多一个模拟器不会带来任何能力提升，只是把需要同步的状态从 N 变成 N+1。所以 0.6.1 起专服不再加载殖民地、不再引用游戏程序集，也就不需要安装 RimWorld。

## 下载与安装

从 [0.6.1 Release](https://github.com/LumiDust/TurboTick/releases/tag/v0.6.1) 下载配套附件；玩家也可订阅 [Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3834465013)。固定世界请保留同一次 Release 的原包，避免客户端与专服在不同时间更新。

| 附件 | 用途 |
|---|---|
| `TurboTick-0.6.1-mod-1.7.zip` | 玩家客户端；解压后把完整 `TurboTick` 目录放入游戏 `Mods` |
| `TurboTick-0.6.1-coordinator-linux-x64` | 专服；自包含单文件，**不需要安装游戏本体** |

玩家启用 Harmony、TurboTick 和与专服完全一致的其他模组后重启游戏。

## 部署专服

协调器是一个自包含的 .NET 单文件程序，目标机只需要 x64 Linux：

```sh
install -m 0755 TurboTick-0.6.1-coordinator-linux-x64 /srv/turbotick/TurboTick.Server
mkdir -p /srv/turbotick/state
printf '%s' 'YOUR-ADMIN-PASSWORD' > /srv/turbotick/secrets/admin-password
chmod 400 /srv/turbotick/secrets/admin-password
```

`deploy/turbotick-coordinator.service` 是一份可直接使用的 systemd 单元。运行参数也可以走环境变量：`TURBOTICK_HOST`、`TURBOTICK_PORT`、`TURBOTICK_DATA`、`TURBOTICK_GAME_VERSION`、`TURBOTICK_MOD_HASH`、`TURBOTICK_ADMIN_PASSWORD_FILE`。

常用参数：`--host` 绑定地址（默认 127.0.0.1）、`--port` 端口（默认 30572，0 表示随机）、`--data` 状态与快照目录、`--game-version` / `--mod-hash` 预锁定兼容指纹、`--controller-only` 只允许一个控制者、`--import` 导入本地快照。`--help` 列全量。

没配管理密码时管理功能关闭；配置后在大厅「管理服务器」独立认证，可查看状态、归档旧局并重开空房间。

> `deploy/container/`、`scripts/steamcmd/` 与 `deploy/turbotick-headless.service` 属于 0.6.0 的 headless 游戏托管部署，供历史版本回退使用；0.6.1 的专服不需要它们。

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

检测到两端状态不一致时**先暂停并指出类别**（位置／血量／工作／物体／标记／随机流），由玩家决定是否退回检查点，而不是静默回滚。若某一端的共享随机流不一致，类别里会直接出现「随机」。

## 版本、权限与连接

- 客户端与专服使用 **0.6.1 / 协议 v5**；所有玩家与专服必须使用同一份 Mod 字节。
- 握手校验**精确 GameBuild、DLC、模组内容、加载顺序和 Mod 配置**，包括程序集与 XML。仅「都是 1.7」、版本显示相同或模组同名仍可能得到 `ModHashMismatch`。同一 packageId 装了两份（例如本地 `Mods/Harmony` 与工坊 Harmony）会让 packageId 被加上 `_steam` 后缀，两端不一致就会握手失败。
- 房主、倍率控制者、检查点所有者由服务器管理；房主退出后，检查点归属自动移交给剩余玩家，世界继续推进。
- 默认端口 **30572/TCP**。公网直连需要可达地址、端口映射与防火墙放行；游戏与管理共用该 TCP 通道且未内置 TLS，需要链路加密时请用 VPN 或隧道承载。
- 默认不启用「快速生态」：该开发选项让每个 tick 前进 2000，会直接破坏共享时间线，共享世界会强制关闭它并记录一次告警。

## 已验证范围

验收在两台互不相关的 headless 客户端之间自动完成（同一份游戏与 Mod 字节、各自独立的 profile 与存档目录），全部使用 Core + Harmony + TurboTick 组合：

| 场景 | 结果 |
|---|---|
| 无输入长跑 | 14 900 tick，零分叉 |
| 脚本化操作 345 次 | 20 000+ tick，零分叉 |
| 晚加入（间隔 152 秒） | 仅一次规范化重载，追平后零分叉 |
| 断线重连 | 零分叉至 31 200 tick |
| 房主退出 | 剩余玩家继续推进并接管房主 |
| 整对象状态同步 | 63 条 Scribe 状态走完采样→下发→应用，零分叉 |
| 检查点节律 | 6 000 tick 间隔连续接受 |

单元测试 62 项、网络协议测试 29 组全绿。客户端启动时会自检补丁挂载并输出一行汇总；任何「已声明但未安装」的补丁会点名，通用 gizmo 捕获若没挂上任何入口也会显式告警。

已确认的限制：

- 真实点击事件到 gizmo 委托这一跳无法自动化，需要人工在界面上点一次确认。
- 第三方 Mod 若使用 `UnityEngine.Random`，其行为无法被确定化——该 API 全部是 `extern`，没有可供拦截的托管方法体。共享随机流只覆盖 Verse 的 `Rand`。
- 交易、远行队默认限制房主操作；运输舱同步为实验开关。
