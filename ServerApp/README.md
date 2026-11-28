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
config.json        ← 修改 ingestToken / 端口后重启
data\pst.db        ← SQLite 数据，注意备份
```

- 大小：56 MB
- SHA256：`15dce9323eb4924df7447c5f25a4dbcdaae4f6b132b70a766df9b532ed86143f`
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
  "ingestToken": "change-me-ingest-token",
  "dashboardToken": "",
  "dbFile": "data/pst.db",
  "staleMinutes": 10
}
```

- `ingestToken`：客户端上报令牌，必须与客户端「数据接口 → 访问令牌」一致。
- `dashboardToken`：大屏访问令牌；留空则局域网可直接访问，建议内网也设置。
- `staleMinutes`：超过该分钟数未上报即判定离线，默认 10 分钟。

也可以用环境变量覆盖：`PST_PORT`、`PST_HOST`、`PST_INGEST_TOKEN`、
`PST_DASH_TOKEN`、`PST_DB`、`PST_STALE_MINUTES`。

## 客户端配置

在客户端「数据接口」页：

1. 勾选启用 HTTP 推送。
2. 推送地址填：`http://<服务器IP>:8080/api/v1/reports`
3. 访问令牌填 `ingestToken`。
4. 点「测试推送」，返回 2xx 即成功。
5. 采集策略设为定时采集后，客户端会自动上报；离线时进入本地 outbox，恢复后补发。

## 接口

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/api/v1/reports` | 客户端上报（Bearer ingestToken） |
| GET | `/api/health` | 健康检查 |
| GET | `/api/config` | 大屏配置（是否需要令牌、离线阈值） |
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
