"""Rewrite host/client wording in the localized keyed files.

The mod has exactly one authority (the dedicated server) and every game client is a
display replica, so wording that implies a player hosts the world ("host", "房主",
"控制者") describes a role that no longer exists. Each replacement is asserted to
appear exactly once; a silent miss would leave a key pointing at text nobody reads.
"""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "Languages"

PAIRS = {
    "ChineseSimplified": [
        # Tab keys disappear: there is one page, not host/join.
        ('  <TurboTick_HostTab>主持游戏</TurboTick_HostTab>\n'
         '  <TurboTick_JoinTab>加入游戏</TurboTick_JoinTab>\n', ''),
        ('  <TurboTick_HostConnectStep>1. 连接专用服务器</TurboTick_HostConnectStep>\n'
         '  <TurboTick_ConnectAsHost>连接专服并申请主持</TurboTick_ConnectAsHost>\n'
         '  <TurboTick_HostGuideTitle>先连专服，再主持</TurboTick_HostGuideTitle>\n'
         '  <TurboTick_HostGuide>1. 填写已启动的专用服务器地址、端口和玩家名。\\n\\n2. 连接成功并取得房主身份后，选择已有存档或新建殖民地。\\n\\n3. 完成读档／建图后，把殖民地发布到同一专服。\\n\\n其他玩家连接这个专服即可加入，专服独立于房主电脑运行。</TurboTick_HostGuide>\n'
         '  <TurboTick_DedicatedConnected>专服已连接 · 房主</TurboTick_DedicatedConnected>\n'
         '  <TurboTick_HostSaveGuide>已取得专服确认的房主身份。\\n\\n选择已有存档或新建殖民地，完成后会上传到当前专服，供全员载入。</TurboTick_HostSaveGuide>\n'
         '  <TurboTick_HostConnectFirst>请先通过“主持游戏”连接专用服务器并取得房主身份。</TurboTick_HostConnectFirst>\n'
         '  <TurboTick_HostConnectionLost>连接或房主身份已变化，存档发布已停止；请重新连接专服。</TurboTick_HostConnectionLost>\n'
         '  <TurboTick_HostRoleReady>专服已确认房主身份，请选择已有存档或新建殖民地。</TurboTick_HostRoleReady>\n'
         '  <TurboTick_HostOccupied>专服已有房主，请通过“加入游戏”进入。</TurboTick_HostOccupied>\n'
         '  <TurboTick_ConnectionFailed>专服连接未完成：{0}</TurboTick_ConnectionFailed>\n',
         '  <TurboTick_ConnectStep>1. 连接专用服务器</TurboTick_ConnectStep>\n'
         '  <TurboTick_ConnectToServer>连接专服并进入</TurboTick_ConnectToServer>\n'
         '  <TurboTick_DeployColony>＋ 部署殖民地到专服</TurboTick_DeployColony>\n'
         '  <TurboTick_ConnectGuideTitle>所有画面都由专服驱动</TurboTick_ConnectGuideTitle>\n'
         '  <TurboTick_ConnectGuide>1. 填写已启动的专用服务器地址、端口和玩家名。\\n\\n2. 连接后自动下载服务器存档并追平进度；本机只负责显示，不模拟世界。\\n\\n3. 想换一份殖民地，用“部署殖民地到专服”把本地存档或新建殖民地交给服务器接管。\\n\\n专服独立于任何玩家电脑运行。</TurboTick_ConnectGuide>\n'
         '  <TurboTick_DedicatedConnected>专服已连接 · 权威端</TurboTick_DedicatedConnected>\n'
         '  <TurboTick_HostSaveGuide>专服已接受你的部署请求。\\n\\n选择已有存档或新建殖民地，完成后会上传到当前专服，供全员载入。</TurboTick_HostSaveGuide>\n'
         '  <TurboTick_HostConnectFirst>请先连接专用服务器，再部署殖民地。</TurboTick_HostConnectFirst>\n'
         '  <TurboTick_HostConnectionLost>连接或部署权限已变化，存档发布已停止；请重新连接专服。</TurboTick_HostConnectionLost>\n'
         '  <TurboTick_HostRoleReady>专服已接受部署请求，请选择已有存档或新建殖民地。</TurboTick_HostRoleReady>\n'
         '  <TurboTick_HostOccupied>专服正在接收另一份部署，请稍后再试。</TurboTick_HostOccupied>\n'
         '  <TurboTick_ConnectionFailed>专服连接未完成：{0}</TurboTick_ConnectionFailed>\n'),
        ('  <TurboTick_CreateAndHost>新建并主持</TurboTick_CreateAndHost>\n'
         '  <TurboTick_StartHosting>开始主持</TurboTick_StartHosting>\n'
         '  <TurboTick_DirectConnect>直接连接</TurboTick_DirectConnect>\n'
         '  <TurboTick_JoinGame>加入多人游戏</TurboTick_JoinGame>\n'
         '  <TurboTick_JoinGuideTitle>与朋友一起落地</TurboTick_JoinGuideTitle>\n'
         '  <TurboTick_JoinGuide>1. 向房主取得专用服务器的地址与端口。\\n\\n2. 使用相同的游戏版本、模组与配置。\\n\\n3. 连接后自动下载存档并追平进度。\\n\\n同一地图上会显示玩家的彩色指针与名字。暂停和速度使用原版按钮即可。</TurboTick_JoinGuide>\n',
         '  <TurboTick_CreateAndHost>部署新殖民地</TurboTick_CreateAndHost>\n'
         '  <TurboTick_StartHosting>部署选中的殖民地</TurboTick_StartHosting>\n'),
        ('  <TurboTick_WaitForHost>已连接房间，等待房主开始共享殖民地；开始后会自动载入。</TurboTick_WaitForHost>',
         '  <TurboTick_WaitForHost>已连接专服，正在等待共享殖民地；就绪后会自动载入。</TurboTick_WaitForHost>'),
        ('  <TurboTick_HostCreating>完成原版新建殖民地流程后，自动主持游戏。</TurboTick_HostCreating>\n'
         '  <TurboTick_HostLoading>正在载入选中的殖民地，完成后自动主持游戏。</TurboTick_HostLoading>\n'
         '  <TurboTick_HostConnecting>正在连接专用服务器并申请房主身份…</TurboTick_HostConnecting>',
         '  <TurboTick_HostCreating>完成原版新建殖民地流程后，自动部署到专服。</TurboTick_HostCreating>\n'
         '  <TurboTick_HostLoading>正在载入选中的殖民地，完成后自动部署到专服。</TurboTick_HostLoading>\n'
         '  <TurboTick_HostConnecting>正在连接专用服务器并申请部署权限…</TurboTick_HostConnecting>'),
        ('  <TurboTick_HostCancelled>已取消主持游戏。</TurboTick_HostCancelled>',
         '  <TurboTick_HostCancelled>已取消部署。</TurboTick_HostCancelled>'),
        ('  <TurboTick_HostReady>房间已就绪，朋友现在可以加入。</TurboTick_HostReady>',
         '  <TurboTick_HostReady>共享殖民地已就绪，朋友现在可以加入。</TurboTick_HostReady>'),
        ('  <TurboTick_HudHost>房主</TurboTick_HudHost>\n'
         '  <TurboTick_HudController>控制者</TurboTick_HudController>\n'
         '  <TurboTick_HudReadOnly>只读</TurboTick_HudReadOnly>',
         '  <TurboTick_HudCanCommand>可操作</TurboTick_HudCanCommand>\n'
         '  <TurboTick_HudViewOnly>仅观看</TurboTick_HudViewOnly>'),
        ('  <TurboTick_Prototype>主持和加入都先连接专用服务器。主持者取得房主身份后选择已有存档或新建殖民地，再由专服向全员分发共享存档。</TurboTick_Prototype>',
         '  <TurboTick_Prototype>所有玩家都先连接专用服务器，由它持有并模拟唯一的殖民地，再把画面同步给每个客户端。</TurboTick_Prototype>'),
        ('  <TurboTick_RoomPrototype>房间已连接，等待房主开始共享殖民地。</TurboTick_RoomPrototype>',
         '  <TurboTick_RoomPrototype>已连接专服，等待共享殖民地就绪。</TurboTick_RoomPrototype>'),
        ('  <TurboTick_EveryoneTradesDesc>默认只有房主能进行交易与组建远行队，避免两端银两、货物与人口不一致。开启后各自操作，可能造成分叉。</TurboTick_EveryoneTradesDesc>',
         '  <TurboTick_EveryoneTradesDesc>默认只有被授权的玩家能进行交易与组建远行队，避免银两、货物与人口在不同客户端上分叉。开启后谁的客户端都能提交命令，由专服依次执行。</TurboTick_EveryoneTradesDesc>'),
        ('由你选择已有存档或新建殖民地。</TurboTick_AdminConfirmReset>',
         '由你选择已有存档或新建殖民地。</TurboTick_AdminConfirmReset>'),
        ('  <TurboTick_AdminResetComplete>服务器已归档重开。取得主持身份后，请重新选择存档或新建殖民地。</TurboTick_AdminResetComplete>',
         '  <TurboTick_AdminResetComplete>服务器已归档重开。取得部署权限后，请重新选择存档或新建殖民地。</TurboTick_AdminResetComplete>'),
        ('把这个文件与房主的那份对比', '把这个文件与服务器的那份对比'),
    ],
    "English": [
        ('  <TurboTick_HostTab>Host game</TurboTick_HostTab>\n'
         '  <TurboTick_JoinTab>Join game</TurboTick_JoinTab>\n', ''),
        ('  <TurboTick_HostConnectStep>1. Connect to dedicated server</TurboTick_HostConnectStep>\n'
         '  <TurboTick_ConnectAsHost>Connect and request host role</TurboTick_ConnectAsHost>\n'
         '  <TurboTick_HostGuideTitle>Connect, then host</TurboTick_HostGuideTitle>\n'
         "  <TurboTick_HostGuide>1. Enter the running dedicated server's address, port and your player name.\\n\\n2. Connect and obtain the host role, then select a save or create a colony.\\n\\n3. Publish the loaded/generated colony to that same server.\\n\\nOther players join this server. It runs independently of the host's computer.</TurboTick_HostGuide>\n"
         '  <TurboTick_DedicatedConnected>Server connected · Host</TurboTick_DedicatedConnected>\n'
         '  <TurboTick_HostSaveGuide>The dedicated server has granted you the host role.\\n\\nSelect a save or create a colony, then publish it to this server for everyone to load.</TurboTick_HostSaveGuide>\n'
         '  <TurboTick_HostConnectFirst>Use Host game to connect to the dedicated server and obtain the host role first.</TurboTick_HostConnectFirst>\n'
         '  <TurboTick_HostConnectionLost>The connection or host role changed. Save publication has stopped; reconnect to the server.</TurboTick_HostConnectionLost>\n'
         '  <TurboTick_HostRoleReady>The server confirmed your host role. Select a save or create a colony.</TurboTick_HostRoleReady>\n'
         '  <TurboTick_HostOccupied>This server already has a host. Use Join game to enter.</TurboTick_HostOccupied>\n'
         '  <TurboTick_ConnectionFailed>Dedicated server connection did not complete: {0}</TurboTick_ConnectionFailed>\n',
         '  <TurboTick_ConnectStep>1. Connect to the dedicated server</TurboTick_ConnectStep>\n'
         '  <TurboTick_ConnectToServer>Connect and enter</TurboTick_ConnectToServer>\n'
         '  <TurboTick_DeployColony>+ Deploy a colony</TurboTick_DeployColony>\n'
         '  <TurboTick_ConnectGuideTitle>The server drives every frame</TurboTick_ConnectGuideTitle>\n'
         "  <TurboTick_ConnectGuide>1. Enter the running dedicated server's address, port and your player name.\\n\\n2. Connecting downloads the server's save and catches up automatically; this game only renders, it never simulates the world.\\n\\n3. To replace the colony, use Deploy a colony to hand a local save or a new colony to the server.\\n\\nThe server runs independently of any player's computer.</TurboTick_ConnectGuide>\n"
         '  <TurboTick_DedicatedConnected>Server connected · authority</TurboTick_DedicatedConnected>\n'
         '  <TurboTick_HostSaveGuide>The server accepted your deployment request.\\n\\nSelect a save or create a colony, then it uploads to this server for everyone to load.</TurboTick_HostSaveGuide>\n'
         '  <TurboTick_HostConnectFirst>Connect to the dedicated server before deploying a colony.</TurboTick_HostConnectFirst>\n'
         '  <TurboTick_HostConnectionLost>The connection or deployment permission changed. Save publication has stopped; reconnect to the server.</TurboTick_HostConnectionLost>\n'
         '  <TurboTick_HostRoleReady>The server accepted your deployment request. Select a save or create a colony.</TurboTick_HostRoleReady>\n'
         '  <TurboTick_HostOccupied>Another deployment is in progress on this server. Try again shortly.</TurboTick_HostOccupied>\n'
         '  <TurboTick_ConnectionFailed>Dedicated server connection did not complete: {0}</TurboTick_ConnectionFailed>\n'),
        ('  <TurboTick_CreateAndHost>Create and host</TurboTick_CreateAndHost>\n'
         '  <TurboTick_StartHosting>Start hosting</TurboTick_StartHosting>\n'
         '  <TurboTick_DirectConnect>Direct connection</TurboTick_DirectConnect>\n'
         '  <TurboTick_JoinGame>Join multiplayer game</TurboTick_JoinGame>\n'
         '  <TurboTick_JoinGuideTitle>Land together</TurboTick_JoinGuideTitle>\n'
         "  <TurboTick_JoinGuide>1. Get the dedicated server's address and port from the host.\\n\\n2. Use the same game version, mods and configuration.\\n\\n3. Join to download the save and catch up automatically.\\n\\nColored pointers and names show other players on the same map. Use the native pause and speed buttons.</TurboTick_JoinGuide>\n",
         '  <TurboTick_CreateAndHost>Deploy a new colony</TurboTick_CreateAndHost>\n'
         '  <TurboTick_StartHosting>Deploy the selected colony</TurboTick_StartHosting>\n'),
        ('  <TurboTick_WaitForHost>Connected. Waiting for the host to share a colony; the save will load automatically.</TurboTick_WaitForHost>',
         '  <TurboTick_WaitForHost>Connected to the server. Waiting for a shared colony; it loads automatically once ready.</TurboTick_WaitForHost>'),
        ('  <TurboTick_HostCreating>Complete the native new-colony setup to host automatically.</TurboTick_HostCreating>\n'
         '  <TurboTick_HostLoading>Loading the selected colony, then hosting automatically.</TurboTick_HostLoading>\n'
         '  <TurboTick_HostConnecting>Connecting to the dedicated server and requesting the host role...</TurboTick_HostConnecting>',
         '  <TurboTick_HostCreating>Complete the native new-colony setup to deploy it automatically.</TurboTick_HostCreating>\n'
         '  <TurboTick_HostLoading>Loading the selected colony, then deploying it automatically.</TurboTick_HostLoading>\n'
         '  <TurboTick_HostConnecting>Connecting to the dedicated server and requesting deployment rights...</TurboTick_HostConnecting>'),
        ('  <TurboTick_HostCancelled>Hosting cancelled.</TurboTick_HostCancelled>',
         '  <TurboTick_HostCancelled>Deployment cancelled.</TurboTick_HostCancelled>'),
        ('  <TurboTick_HostReady>Your room is ready. Friends can join now.</TurboTick_HostReady>',
         '  <TurboTick_HostReady>The shared colony is ready. Friends can join now.</TurboTick_HostReady>'),
        ('  <TurboTick_HudHost>Host</TurboTick_HudHost>\n'
         '  <TurboTick_HudController>Controller</TurboTick_HudController>\n'
         '  <TurboTick_HudReadOnly>Read-only</TurboTick_HudReadOnly>',
         '  <TurboTick_HudCanCommand>Can command</TurboTick_HudCanCommand>\n'
         '  <TurboTick_HudViewOnly>View only</TurboTick_HudViewOnly>'),
    ],
}


def main() -> int:
    failures = 0
    for language, pairs in PAIRS.items():
        path = ROOT / language / "Keyed" / "TurboTick.xml"
        text = path.read_text(encoding="utf-8")
        for old, new in pairs:
            count = text.count(old)
            if count != 1:
                print(f"{language}: expected 1 hit, found {count}: {old.splitlines()[0][:80]}")
                failures += 1
                continue
            text = text.replace(old, new)
        path.write_text(text, encoding="utf-8")
        print(f"{language}: rewritten")
    return failures


if __name__ == "__main__":
    raise SystemExit(main())
