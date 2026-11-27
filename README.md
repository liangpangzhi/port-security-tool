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
| 远程桌面 | 局域网发现（ARP + 3389 + 本工具代理三路探测）；直连或「需对方确认」的远程请求；被请求方仅对请求方 IP 临时放行 |
| 数据采集 | 硬件、计算机名、端口封堵情况采集为 xlsx，推送至指定共享目录；定时或手动 |
| 数据接口 | 标准 JSON 报文 HTTP 推送、本地查询服务、离线补发队列，便于二次开发服务端做汇总分析 |
| 关于 | 版本、版权、运行环境、部署形态 |

## 二、技术形态

- **宿主**：C# WinForms（.NET 10，win-x64），内嵌 WebBrowser 渲染 HTML 界面。
- **界面**：单文件 HTA 改造而来，`NativeApp/build_html.js` 将 HTA 转换为内嵌页 `ui.html`，并注入 `ActiveXObject → window.external` 兼容层。
- **原生层**：注册表 / 进程 / 防火墙规则直读，替代 `wmic`、`tasklist`、`netsh show` 等外部命令（毫秒级）；服务启停走 SCM API，不依赖 `sc.exe`。
- **零依赖发布**：`SelfContained + PublishSingleFile + ReadyToRun`，运行时不需预装 .NET，也不需要 VC++ 运行库；对外只依赖系统自带组件（mshtml、WMI、netsh、mstsc、schtasks）。

## 三、目录结构

```
.
├─ 网络信息采集和端口管理工具_v1.0_GUI.hta   界面与业务逻辑源文件（单文件 HTA）
├─ dist/
│  ├─ 网络信息采集和端口管理工具_v1.0.exe    成品（自包含单文件，管理员运行）
│  └─ 网络信息采集和端口管理工具_v1.0_GUI.hta
├─ API接口文档_v1.0/                        数据接口说明 / JSON Schema / 脱敏报文示例
└─ NativeApp/                               C# 宿主与原生能力
   ├─ Program.cs / MainForm.cs              入口、窗体、托盘、单实例、定时采集
   ├─ Bridge.cs                             COM 桥接（界面 → 原生）
   ├─ NativeApi.cs                          注册表 / 防火墙 / 进程直读
   ├─ SystemIntegration.cs                  自启、窗口唤起
   ├─ Collector.cs / XlsxWriter.cs / WmiInfo.cs / SysInfo.cs   采集与表格生成
   ├─ ApiJson.cs / ApiReport.cs / ApiClient.cs / ApiServer.cs / ApiDoc.cs   数据接口
   ├─ NetDiscovery.cs / RdpAgent.cs / RdpClient.cs              局域网发现与远程桌面
   ├─ build_html.js                         HTA → ui.html，注入兼容层
   ├─ app.manifest / app.ico                管理员清单与图标
   ├─ probe/                                界面自检探针（在真实 WebBrowser 宿主里执行 JS）
   └─ apiprobe/                             接口与功能自检程序
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

# 2) 自包含单文件发布
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:DebugType=None

# 产物：NativeApp/bin/Release/net10.0-windows/win-x64/publish/PortSecurityTool.exe
```

自检（可选）：

```bash
cd NativeApp/apiprobe && dotnet build -c Release && ./bin/Release/net10.0-windows/ApiProbe.exe
```

## 六、数据接口

`API接口文档_v1.0/` 内含字段说明、JSON Schema 与脱敏示例报文，可据此对接自建服务端：

- **HTTP 推送**：`POST` JSON 报文，带 `Authorization: Bearer`、`X-PST-Report-Id`（幂等键）；失败落盘待发队列，恢复后自动补发。
- **本地查询服务**：默认 `127.0.0.1:18760`，仅监听回环，可配令牌；提供设备信息、端口状态、Schema、xlsx 报表下载。
- **远程桌面代理**（可选）：默认 `TCP 18761`，供局域网内同类工具发现与发起「需确认」的远程请求。

## 七、安全与合规

- 本工具定位为**终端安全基线加固**，所有封堵动作都可通过界面上的「恢复」按钮或对应命令回滚；部分操作需重启生效。
- 远程桌面放行前必须由被控端本机用户**人工确认**，且只对请求方那一个 IP 放行，不会对全网开放 3389。
- 请勿用于未授权的终端；在生产环境批量执行前，先在测试机验证策略影响面。
- 采集数据可能包含主机名、序列号、MAC、登录用户等信息，请按本单位数据分级要求存储与传输。

## 八、版本

- **v1.0** —— 首个整合版：信息采集、端口管控、局域网远程桌面、数据接口。

版权：成都铁路局集团公司党校（职工培训基地）设备信息科
