# 给容器部署加一个管理页（0.7.2 现状：镜像里没有 Portal）

镜像 `/opt/turbotick/` 只有 `container.py`、`entrypoint.sh`、`install-server.sh`、`mod`、`payload.sha256.json`、
`steamcmd.sh` —— **不含 `TurboTick.Portal`**，所以用镜像部署的服默认没有网页管理台。0.6.x 的页面是游戏进程内的
监听器，0.7.x 拆成了独立进程，容器这一侧还没把它装进去。

本部署（111.170.175.27）的做法是把它当**宿主机服务**跑，和容器共用数据目录：

```bash
# 1. 本机发布（Windows 工作站）
dotnet publish Portal/TurboTick.Portal.csproj -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true \
  -p:RimWorldDir=tools/ref17/ref/net10.0 -o _lab/portal
scp _lab/portal/TurboTick.Portal root@SERVER:/srv/turbotick/portal/

# 2. 服务器：服务账号 = 容器里的 10001（宿主机原来没有这个 uid）
groupadd -g 10001 turbotick && useradd -u 10001 -g 10001 -M -s /usr/sbin/nologin turbotick

# 3. 密码只放文件，不写进 unit / .env
openssl rand -hex 16 > /srv/turbotick/admin-password
chmod 600 /srv/turbotick/admin-password && chown 10001:10001 /srv/turbotick/admin-password
```

systemd 单元要点（完整文件见服务器 `/etc/systemd/system/turbotick-portal.service`）：

```ini
Environment=TURBOTICK_DATA=/srv/turbotick/data/state
Environment=TURBOTICK_SAVES=/srv/turbotick/data/profile/Saves
Environment=TURBOTICK_GAME=/srv/turbotick/data/game
Environment=TURBOTICK_CONFIG=/srv/turbotick/data/profile/Config/ModsConfig.xml
Environment=TURBOTICK_ADMIN_BIND=0.0.0.0          # 非回环绑定强制登录
Environment=TURBOTICK_ADMIN_PORT=30573
Environment=TURBOTICK_ADMIN_PASSWORD_FILE=/srv/turbotick/admin-password
Environment=HOME=/srv/turbotick/portal             # nologin 用户没有 HOME，单文件包要先能解压
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/srv/turbotick/portal/.extract
ExecStart=/srv/turbotick/portal/TurboTick.Portal --no-supervise --no-auto-start
User=10001
```

两个 `--no-*` 是承重的：默认的 Portal 是**监督者**，会自己再起一个 RimWorld；容器里已经有一个，多起一个会抢
30572 与 profile 锁。`--no-supervise --no-auto-start` 让它只做网页端：读 `/state/headless-status.json` 显示状态，
写 `/state/portal-commands/<id>.json` 给运行中的游戏下命令。

验证：

```bash
curl -o /dev/null -w '%{http_code}\n' http://127.0.0.1:30573/            # 200 登录页
curl -c jar -d 'password=…' http://127.0.0.1:30573/login -o /dev/null -w '%{http_code}\n'  # 303
curl -b jar -o /dev/null -w '%{http_code}\n' http://127.0.0.1:30573/    # 200 控制台
```

已实测：登录成功写 `管理页登录成功（会话 1，有效期 43200 秒）`；控制台读到的状态是 `lobby`。

## 待办

把 `Portal/` 加进镜像（Dockerfile publish + 独立 `portal` 服务），这样就不再需要宿主机 dotnet 产物；
在那之前换版本只需重新 scp 同名二进制并 `systemctl restart turbotick-portal`。
