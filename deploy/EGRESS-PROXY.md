> 以下步骤解决一个具体症状：专服在 **国内机房**，`update-check` 写 `status: unavailable`（原因
> `SteamCMD did not answer within 120s`），`docker pull ghcr.io/...` 报 `i/o timeout`。这不是 DNS 问题——换
> `223.5.5.5 / 119.29.29.29 / 114.114.114.114` 解析出来的 Akamai/Steam 边缘 IP 全部在 TCP 443 上被丢弃，
> 而 `baidu.com`、`mirrors.aliyun.com`、`registry.npmmirror.com` 都是 200。**DNS 优选换不来一条能过的路由。**

## 借出口：把有外网的那台机器当代理

拓扑（实测部署）：

```text
  专服（无外网出口）                中继机（有出口，本部署是 192.168.0.29）        你的工作站
  111.170.175.27                    tinyproxy 127.0.0.1:18888                       （不参与）
        ▲                                    │
        └──── 172.17.0.1:17897 ◄── ssh -R ────┘
```

1. **中继机**装一个只监听回环的 CONNECT 代理，并把它反向转发到专服的 docker 网桥地址：

   ```bash
   apt-get install -y tinyproxy               # 监听 127.0.0.1:18888，Allow 127.0.0.1
   ssh -N -R 172.17.0.1:17897:127.0.0.1:18888 root@SERVER_HOST
   ```

   做成 systemd 单元（`Restart=always`，`-o ExitOnForwardFailure=yes`），机器重启后自动恢复。
   **专服的 sshd 必须允许客户端指定监听地址**，否则 `-R` 只能绑 `127.0.0.1`、容器看不见：

   ```bash
   echo 'GatewayPorts clientspecified' > /etc/ssh/sshd_config.d/10-turbotick-tunnel.conf
   sshd -t && systemctl reload ssh
   ```

2. **专服**把这条代理喂给两个消费者：

   ```bash
   # Docker 守护进程（拉镜像用），然后 restart docker
   mkdir -p /etc/systemd/system/docker.service.d
   cat > /etc/systemd/system/docker.service.d/http-proxy.conf <<'EOF'
   [Service]
   Environment="HTTP_PROXY=http://172.17.0.1:17897"
   Environment="HTTPS_PROXY=http://172.17.0.1:17897"
   Environment="NO_PROXY=localhost,127.0.0.1,172.17.0.0/16,10.0.0.0/8"
   EOF
   systemctl daemon-reload && systemctl restart docker

   # 容器内的 SteamCMD（installer 与 update-check 都读这三个变量）
   INSTALL_HTTP_PROXY=http://172.17.0.1:17897
   INSTALL_HTTPS_PROXY=http://172.17.0.1:17897
   INSTALL_ALL_PROXY=http://172.17.0.1:17897
   ```

3. **验证**（全部实测通过）：

   ```bash
   curl -x http://172.17.0.1:17897 -o /dev/null -w '%{http_code}\n' \
        https://api.steampowered.com/ISteamWebAPIUtil/GetServerInfo/v1/     # 200
   docker pull ghcr.io/lumidust/turbotick-headless:0.7.1                   # 成功，digest 可核对
   docker logs --tail 3 turbotick-headless-update-check-1                  # 出现 (unstable) … latest Steam build …
   cat data/state/game-update.json                                         # status: ok
   ```

   实测耗时：镜像 173 MB 经代理约 6 分钟；`update-check` 一次查询约 40 秒。

## 已知限制

- 中继机必须在线；它不在线时 `update-check` 会把 `status` 写成 `unavailable` 并保留上一次数据（不会被伪造成新鲜值），
  镜像也拉不动。文档里请把中继机当作部署的一部分来监控。
- 反向转发只在**一条 SSH 连接**上有效；中继机重启后由 systemd 重建，期间最多断几秒。
- 若中继机与专服之间本来就有 VPN，直接让专服走 VPN 出口更简单；本方案的价值是不改专服网络栈、只加两个变量。
