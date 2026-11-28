# 网络信息采集和端口管理工具 v1.0

面向 Windows 内网终端的**信息采集 + 高危端口管控**桌面工具。单文件、自包含、免安装运行时，双击即以管理员权限运行。

界面采用「技术简洁风」：左侧九项导航，右侧卡片式操作区，全部操作都有明确的成功/失败反馈与一键回滚入口。

> ⚠️ 本工具会真实修改系统服务、注册表、防火墙规则与计划任务。仅限在**自有或已获书面授权**的终端上使用，误用可能中断远程桌面、文件共享、数据库等业务。使用前请阅读「安全与合规」一节。

---

## 一、功能模块

| 模块 | 能力 |
| --- | --- |
| 安全总览 | 管理员权限 / 防火墙状态 / 监听端口 / 高危端口暴露面实时态势；一键执行预置封堵；全端口扫描并给出风险解释 |
| 预置封堵 | 四套策略：终端防勒索（MS17-010 永恒之蓝）、远程入口封堵、弱服务收敛、数据库暴露面收敛；支持保留端口白名单 |
| 端口与防火墙 | 84 个预置高危端口清单；一键扫描封堵情况（绿=未封堵 / 红=已封堵）；**点选封禁**：单击端口即下发阻断，右键解除 |
| 共享与服务 | 默认共享、SMBv1、NTLM、SMB 签名、IIS FTP 匿名；8 类危险服务的逐项开关与恢复；服务器模式按 IP 放行 |
| IP 白名单 | SMB / FTP / 远程桌面等按来源 IP 放行，支持 CIDR |
| 端口转发 | SMB 端口中转（服务端/客户端）、规则清理、SMB over QUIC 备用端口检测 |
| 远程桌面 | 已切换为内置 **SubnetDesk 客户端**：设备发现、画面控制、文件传输等由 SubnetDesk 提供；主程序单 exe 内嵌，首次点击自动解压启动；关闭远程桌面/PST 退出时会一并结束 SubnetDesk |
| 数据上传 | **数据自动上传服务端**：按间隔自动采集刷新，供服务端在局域网扫描后拉取；配套「局域网发现」代理（UDP 广播身份 + TCP 只读拉取接口） |
| 关于 | 版本、版权、运行环境、部署形态 |

## 二、技术形态

- **宿主**：C# WinForms（.NET 10，win-x64），内嵌 WebBrowser 渲染 HTML 界面。
- **界面**：单文件 HTA 改造而来，`NativeApp/build_html.js` 将 HTA 转换为内嵌页 `ui.html`，并注入 `ActiveXObject → window.external` 兼容层。
- **原生层**：注册表 / 进程 / 防火墙规则直读，替代 `wmic`、`tasklist`、`netsh show` 等外部命令（毫秒级）；服务启停走 SCM API，不依赖 `sc.exe`。
- **零依赖发布**：`SelfContained + PublishSingleFile + ReadyToRun`，运行时不需预装 .NET，也不需要 VC++ 运行库；对外只依赖系统自带组件（mshtml、WMI、netsh、schtasks）。远程桌面由内嵌的 SubnetDesk 客户端提供，首次启动解压到 `%LOCALAPPDATA%\PortSecurityTool\remote`。

## 三、目录结构

```
.
├─ 网络信息采集和端口管理工具_v1.0_GUI.hta   界面与业务逻辑源文件（单文件 HTA）
├─ dist/
│  ├─ 网络信息采集和端口管理工具_v1.0_SubnetDesk.exe   成品（自包含单文件，管理员运行）
│  └─ 网络信息采集和端口管理工具_v1.0_GUI.hta
├─ ServerApp/                               服务端 + 数据大屏（Node 22）
│  ├─ server.js                             扫描发现 / 主动拉取 / SQLite 落库 / 汇总接口
│  ├─ public/                               数据大屏前端（HTML/CSS/JS）
│  ├─ test/seed.js                          演示数据（直接写入 SQLite）
│  └─ config.json                           端口 / 发现端口 / 拉取令牌 / 大屏令牌
├─ API接口文档_v1.0/                        数据接口说明 / JSON Schema / 脱敏报文示例
└─ NativeApp/                               C# 宿主与原生能力
   ├─ Program.cs / MainForm.cs              入口、窗体、托盘、单实例、自动上传定时器
   ├─ Bridge.cs                             COM 桥接（界面 → 原生）
   ├─ NativeApi.cs                          注册表 / 防火墙 / 进程直读
   ├─ SystemIntegration.cs                  自启、窗口唤起
   ├─ Collector.cs / WmiInfo.cs / SysInfo.cs             本机采集
   ├─ ApiJson.cs / ApiReport.cs                            采集报文契约（JSON）
   ├─ LanAgent.cs                                          局域网发现(UDP) + 拉取接口(TCP)
   ├─ EmbeddedRemote.cs                                       内置 SubnetDesk 远程桌面（解压/启动）
   ├─ NetDiscovery.cs / RdpAgent.cs / RdpClient.cs              旧远程桌面实现（保留，不再由界面启用）
   ├─ subnetdesk-remote.zip                                   内嵌的 SubnetDesk 便携包
   ├─ build_html.js                         HTA → ui.html，注入兼容层
   ├─ app.manifest / app.ico                管理员清单与图标
   ├─ probe/                                界面自检探针（在真实 WebBrowser 宿主里执行 JS）
   └─ apiprobe/                             功能自检程序（契约生成 / 局域网代理 / 远程桌面）
```

## 四、运行环境

- Windows 10 1607 及以上（含 22H2 / LTSC）、Windows 11，**64 位**
- 必须**以管理员身份运行**（清单已声明 `requireAdministrator`，双击自动提权）
- 端口封堵/放行依赖 Windows 防火墙服务处于运行状态

## 五、构建

前置：.NET 10 SDK、Node.js（仅用于生成内嵌界面）。

```bash
# 1) HTA → ui.html（注入原生宿主兼容层）
cd NativeApp
node build_html.js

# 2) 自包含单文件发布（内嵌 SubnetDesk）
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EmbedRemote=true \
  -p:PublishReadyToRun=true -p:DebugType=None

# 产物：NativeApp/bin/Release/net10.0-windows/win-x64/publish/PortSecurityTool.exe
```

自检（可选）：

```bash
cd NativeApp/apiprobe && dotnet build -c Release && ./bin/Release/net10.0-windows/ApiProbe.exe
```

## 六、数据接口

`API接口文档_v1.0/` 内含字段说明、JSON Schema 与脱敏示例报文，可据此对接自建服务端。

当前为**单向拉取**模式：客户端不再主动推送，数据由服务端在局域网内扫描发现客户端后主动取回。

- **数据自动上传服务端**：「数据上传」页开启自动上传并设置间隔（1 分钟起），到点自动重新采集并刷新上传缓存，服务端任何时刻拉取到的都是最新快照；最小化到托盘仍继续执行。
- **局域网发现代理**：默认开启，监听 UDP/TCP `18762`（可改）——UDP 响应 `PST-DISCOVER-V1` 广播/单播探测，TCP 提供只读接口 `/pst/v1/ping`、`/pst/v1/health`、`/pst/v1/device`。客户端启动时自动添加 UDP/TCP 入站防火墙放行规则，也可用界面「放行防火墙」手动添加。
- **访问令牌**：客户端「访问令牌」与服务端「客户端拉取令牌」填同一个值即可；客户端不设令牌时服务端可免令牌拉取。
- **已停用**：HTTP 推送（服务端旧地址返回 410）、本地查询服务 `127.0.0.1:18760`、电子表格/共享目录上报，客户端界面已移除对应入口。
- **远程桌面**：旧版 `TCP 18761` 代理已停用；远程桌面统一由内置 SubnetDesk 客户端提供，不再由 PST 自身监听或放行 3389。

### 服务端与数据大屏（ServerApp）

`ServerApp/` 是配套的零依赖 Node.js 服务端，扫描发现局域网客户端并定时拉取数据落库 SQLite，
提供数据大屏：首页汇总、风险分布、24 小时趋势、暴露端口 TOP、主机清单和单机详情，顶栏带「返回首页」按钮。

内网服务器部署可直接用单 exe（无需 Node）：

```
ServerApp\dist\PST-SecurityConsole.exe
```

首次运行在 exe 同级生成 `config.json` 和 `data\pst.db`。监听地址、大屏令牌、离线阈值在「系统设置」页修改；
发现端口、拉取令牌、定时拉取等在「客户端发现与拉取」模块内修改保存，不用手动编辑文件。
开发调试也可用源码运行：

```bash
cd ServerApp
node --no-warnings server.js     # 或双击 start.bat
```

- 大屏：`http://<服务器IP>:8080/`
- 系统设置：`http://<服务器IP>:8080/#/settings`
- 客户端发现：`http://<服务器IP>:8080/#/agents` —— 一键扫描在线客户端、立即拉取、查看发现列表；**发现端口 / 拉取令牌 / 定时拉取等设置都在该模块内**
- 扫描采用三路并行：UDP 广播 + 网段单播 UDP 探测 + TCP `18762` 端口探测，任一路命中即发现；跨网段可在扫描框手工填 `192.168.1.0/24`
- 客户端侧：开启「数据上传 → 局域网发现 · 供服务端扫描」，确认端口与服务端一致，令牌一致后点「保存并应用」
- 演示数据：`node test/seed.js`（或 `seed.bat`）
- 详细配置、接口和安全建议见 `ServerApp/README.md`

## 七、安全与合规

- 本工具定位为**终端安全基线加固**，所有封堵动作都可通过界面上的「恢复」按钮或对应命令回滚；部分操作需重启生效。
- 远程桌面放行前必须由被控端本机用户**人工确认**，且只对请求方那一个 IP 放行，不会对全网开放 3389。
- 请勿用于未授权的终端；在生产环境批量执行前，先在测试机验证策略影响面。
- 采集数据可能包含主机名、序列号、MAC、登录用户等信息，请按本单位数据分级要求存储与传输。

## 八、版本

- **v1.0** —— 首个整合版：信息采集、端口管控、局域网远程桌面、数据接口。
- **v1.5** —— 局域网发现 + 单向拉取：服务端一键扫描在线客户端并定时拉取入库；客户端「数据上传」页精简为自动上传与局域网代理两张卡片；服务端取消客户端主动上传通道，大屏增加「返回首页」按钮。

版权：成都铁路局集团公司党校（职工培训基地）设备信息科
