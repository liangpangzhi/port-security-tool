# PST 终端安全运营中心（服务端）

接收客户端自动上报的 `port-security-tool.device-report` 报文，存入 SQLite，
并提供数据大屏：首页汇总、主机清单、单机详情、端口暴露面、上报历史。

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

- 大小：56 MB
- SHA256：`5851bd4a2973fef2b3ea6857ab70bf380dead3388f7044fab446650b46cdc2d9`
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

- 数据大屏：`http://<服务器IP>:8080/`
- 上报接口：`POST http://<服务器IP>:8080/api/v1/reports`
- 系统设置：`http://<服务器IP>:8080/#/settings`

## 页面系统设置

打开大屏右上角的齿轮，或访问 `http://<服务器IP>:8080/#/settings`，可直接在页面查看和修改：

- 监听地址、监听端口（修改后需重启服务端）
- 对外访问地址（反向代理/域名场景填写，留空自动推断）
- 客户端上报路径
- 上报令牌、大屏访问令牌
- 离线判定阈值

页面会显示当前生成的「上报地址」和「大屏地址」，支持一键复制；令牌只显示是否已配置，不显示明文。保存成功后立即写入 exe 同级的 `config.json`：

- 上报路径、令牌、离线阈值立即生效。
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
  "ingestPath": "/api/v1/reports",
  "ingestToken": "change-me-ingest-token",
  "dashboardToken": "",
  "dbFile": "data/pst.db",
  "staleMinutes": 10
}
```

- `publicBaseUrl`：对外访问地址，用于生成上报地址和大屏地址；留空按实际请求推断。
- `ingestPath`：客户端上报路径，默认 `/api/v1/reports`。
- `ingestToken`：客户端上报令牌，必须与客户端「数据接口 → 访问令牌」一致。
- `dashboardToken`：大屏访问令牌；留空则局域网可直接访问，建议内网也设置。
- `staleMinutes`：超过该分钟数未上报即判定离线，默认 10 分钟。

也可以用环境变量覆盖：`PST_PORT`、`PST_HOST`、`PST_PUBLIC_URL`、
`PST_INGEST_PATH`、`PST_INGEST_TOKEN`、`PST_DASH_TOKEN`、`PST_DB`、
`PST_STALE_MINUTES`。

## 客户端配置

在客户端「数据接口」页：

1. 勾选启用 HTTP 推送。
2. 推送地址填服务端「系统设置」页显示的「当前上报地址」（默认 `http://<服务器IP>:8080/api/v1/reports`）。
3. 访问令牌填 `ingestToken`。
4. 点「测试推送」，返回 2xx 即成功。
5. 采集策略设为定时采集后，客户端会自动上报；离线时进入本地 outbox，恢复后补发。

## 接口

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/api/v1/reports` | 客户端上报（Bearer ingestToken） |
| GET | `/api/health` | 健康检查 |
| GET | `/api/config` | 大屏配置（是否需要令牌、离线阈值） |
| GET | `/api/settings` | 读取当前配置（不返回令牌明文） |
| POST | `/api/settings` | 修改配置并写入 `config.json` |
| GET | `/api/summary` | 首页汇总：主机数、在线数、风险分布、端口 TOP、趋势、最近上报 |
| GET | `/api/hosts` | 主机清单，支持 `q` / `risk` / `status` 查询 |
| GET | `/api/hosts/:deviceId` | 单机详情、最新完整报文、最近 30 条历史 |

上报报文格式见仓库根目录 `API接口文档_v1.0/`。

## 演示数据

```bash
cd ServerApp
node test/seed.js
```

会生成 3 台主机、5 份快照（含高危/中危/低危），打开大屏即可看到效果。

## 安全建议

- 上线前修改 `ingestToken` 和 `dashboardToken`，不要使用默认值。
- 大屏包含主机名、IP、MAC、登录用户等敏感信息，只在内网开放。
- 建议放在反向代理/防火墙后，限制来源网段。
- 定期备份 `data/pst.db`。
