# PST 终端安全运营中心（服务端）

扫描发现局域网内的客户端并定时拉取其采集数据，存入 SQLite，
并提供数据大屏：首页汇总、主机清单、单机详情、端口暴露面、采集历史。

数据流向是**单向拉取**：客户端不主动推送，服务端在一键扫描到客户端后按周期取回数据，
客户端侧的「数据自动上传」只负责把本机快照保持新鲜。

## 技术形态

- Node.js 22.5+，运行时零外部依赖
- `node:http` + `node:sqlite`（内置 SQLite）
- 前端静态 HTML/CSS/JS，无 CDN、无构建步骤
- 可用 `@yao-pkg/pkg` 打包为单 exe，内网服务器无需安装 Node
- 数据文件：`data/pst.db`（首次启动自动创建）

## 启动

### 方式一：单 exe 部署（推荐，内网服务器无需 Node）

把 `ServerApp/dist/PST-SecurityConsole.exe` 复制到服务器任意目录，双击或命令行运行：

```bat
PST-SecurityConsole.exe
```

首次运行会在 exe 同级自动生成：

```
PST-SecurityConsole.exe
config.json        ← 可编辑，也可在大屏「系统设置」页修改
data\pst.db        ← SQLite 数据，注意备份
```

- 大小：55 MB
- SHA256：`1dfdd27dc2ee5c1a6fde3f53646dddd5b311b5cdc035058eb64a6a2db85cb25a`
- 不需要安装 Node.js、数据库或 VC++ 运行库
- 默认监听 `0.0.0.0:8080`，Windows 防火墙需放行 8080/TCP
- 建议放到固定目录（如 `D:\PST-Server\`），不要放在只读目录
- 部分杀软可能对 pkg 打包程序误报，需加白名单或做代码签名

### 方式二：Node 源码运行（开发/调试）

```bash
cd ServerApp
node --no-warnings server.js
```

或 `npm start`。

默认地址：

- 数据大屏：`http://<服务器IP>:8080/`（顶栏「首页」按钮随时返回）
- 客户端发现：`http://<服务器IP>:8080/#/agents`
- 系统设置：`http://<服务器IP>:8080/#/settings`
- 客户端发现与拉取：`http://<服务器IP>:8080/#/agents`（**发现/拉取相关设置都在这里**）

## 页面系统设置

打开大屏右上角的齿轮，或访问 `http://<服务器IP>:8080/#/settings`，可直接在页面查看和修改：

- 监听地址、监听端口（修改后需重启服务端）
- 对外访问地址（反向代理/域名场景填写，留空自动推断）
- 大屏访问令牌、离线判定阈值

> 客户端发现端口、拉取令牌、定时拉取开关与间隔已移至「客户端发现与拉取」模块（`#/agents`），在模块内点「保存设置」即可。

页面会显示当前生成的「大屏地址」，支持一键复制；令牌只显示是否已配置，不显示明文。保存成功后立即写入 exe 同级的 `config.json`：

- 离线阈值立即生效。
- 监听地址和端口需要重启服务端后生效。
- 修改大屏令牌后，当前浏览器会自动使用新令牌；清空后大屏恢复免密访问。

## 重新打包 exe

```bash
cd ServerApp
npm ci
npm run build:exe
```

产物：`ServerApp/dist/PST-SecurityConsole.exe`。
打包会把 `public/` 静态资源一起写入 exe；`config.json` 和 `data/` 始终放在 exe 同级，便于部署和备份。

## 配置

单 exe 首次运行会在 exe 同级生成 `config.json`；源码运行则编辑 `ServerApp/config.json`：

```json
{
  "port": 8080,
  "host": "0.0.0.0",
  "publicBaseUrl": "",
  "dashboardToken": "",
  "dbFile": "data/pst.db",
  "staleMinutes": 10,
  "discoveryPort": 18762,
  "agentToken": "",
  "pollEnabled": false,
  "pollIntervalMinutes": 15
}
```

- `publicBaseUrl`：对外访问地址，用于生成大屏地址；留空按实际请求推断。
- `dashboardToken`：大屏访问令牌；留空则局域网可直接访问，建议内网也设置。
- `staleMinutes`：超过该分钟数未采集到数据即判定离线，默认 10 分钟。
- `discoveryPort`：UDP 广播发现端口，也是客户端 TCP 拉取端口，默认 18762；需与客户端「发现端口」一致。
- `agentToken`：服务端拉取客户端时携带的令牌，必须与客户端「访问令牌」一致；客户端未设令牌时可留空。
- `pollEnabled` / `pollIntervalMinutes`：是否定时扫描并拉取，以及间隔分钟数（默认 15 分钟）。

也可以用环境变量覆盖：`PST_PORT`、`PST_HOST`、`PST_PUBLIC_URL`、
`PST_DASH_TOKEN`、`PST_DB`、`PST_STALE_MINUTES`、`PST_DISCOVERY_PORT`、
`PST_AGENT_TOKEN`、`PST_POLL_ENABLED`、`PST_POLL_MINUTES`。

## 客户端配置

客户端只需在「数据上传」页做两件事：

1. 「数据自动上传服务端」：开启自动上传并设置间隔（分钟）。客户端会按间隔在后台重新采集，
   刷新上传缓存，服务端任何时刻拉取到的都是较新快照；点「立即上传」可立刻刷新一次。
2. 「局域网发现 · 供服务端扫描」：确认端口与服务端「发现端口」一致（默认 18762），
   设置访问令牌后点「保存并应用」。若系统防火墙拦截，点「放行防火墙」
   （客户端启动时也会自动添加 UDP/TCP 18762 入站放行规则）。

### 服务端侧

1. 打开大屏右上角雷达按钮（或 `http://<服务器IP>:8080/#/agents`）。
2. 在模块内设置：发现端口（须与客户端一致）、客户端拉取令牌（须与客户端一致）、是否启用定时拉取及间隔，点「保存设置」。
3. 点「一键扫描在线客户端」。扫描采用三路并行：UDP 广播 + 网段单播 UDP 探测 + TCP 18762 端口探测，任一路命中即发现；
   跨网段部署时在扫描框手工填 `192.168.1.0/24` 或具体网段。
4. 发现后点「立即拉取数据」即可入库；开启「定时拉取」后，服务端会按间隔自动扫描并拉取全部在线客户端。
5. 令牌：服务端「客户端拉取令牌」需与客户端「访问令牌」一致；客户端未设令牌时可留空。

## 接口

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/health` | 健康检查 |
| GET | `/api/config` | 大屏配置（是否需要令牌、离线阈值） |
| GET | `/api/settings` | 读取当前配置（不返回令牌明文） |
| POST | `/api/settings` | 修改配置并写入 `config.json` |
| GET | `/api/summary` | 首页汇总：主机数、在线数、风险分布、端口 TOP、趋势、最近采集 |
| GET | `/api/hosts` | 主机清单，支持 `q` / `risk` / `status` 查询 |
| GET | `/api/hosts/:deviceId` | 单机详情、最新完整报文、最近 30 条历史 |
| GET | `/api/agents` | 已发现客户端列表与状态 |
| POST | `/api/agents/scan` | 扫描发现；可传 `{ "cidrs": ["192.168.1.0/24"] }` 指定网段 |
| POST | `/api/agents/poll` | 拉取全部客户端；可传 `{ "only": "192.168.1.20" }` 拉单台 |
| POST | `/api/agents/scan-and-poll` | 先扫描再拉取 |
| GET | `/api/poll/status` | 定时拉取开关、间隔、上次结果 |

客户端主动上传通道已停用：旧的 `POST /api/v1/reports` 统一返回 `410 Gone`。
报文格式见仓库根目录 `API接口文档_v1.0/`。

## 演示数据

```bash
cd ServerApp
node test/seed.js
```

会生成 3 台主机、5 份快照（含高危/中危/低危），直接写入 `data/pst.db`，打开大屏即可看到效果。

## 安全建议

- 上线前设置 `dashboardToken`（大屏令牌）与 `agentToken`（客户端拉取令牌），不要留空裸奔。
- 客户端「访问令牌」与服务端「客户端拉取令牌」必须一致，否则拉取会返回 401。
- 大屏包含主机名、IP、MAC、登录用户等敏感信息，只在内网开放。
- 建议放在反向代理/防火墙后，限制来源网段。
- 定期备份 `data/pst.db`。
