# 数据接口说明（API v1.0）

网络信息采集和端口管理工具（1.0版） · 局域网数据拉取接口
版权单位：成都铁路局集团公司党校（职工培训基地）设备信息科

本文档面向**服务端开发**：说明终端如何把采集数据交出来、字段含义、以及服务端如何接。
文档与契约文件（`schema_1.0.json`、`example_1.0.json`）随本仓库一同提供。

---

## 一、总体设计

当前版本采用**单向拉取**模式：**客户端不再主动推送**，数据由服务端在局域网内扫描发现客户端后主动取回。

| 通道 | 方向 | 状态 | 说明 |
| --- | --- | --- | --- |
| 局域网代理（发现 + 拉取） | 服务端 → 终端 | **当前唯一通道** | 终端在 UDP/TCP 18762 广播身份并提供只读拉取接口，服务端扫描后定时拉取 |
| HTTP 推送 | 终端 → 服务端 | 已停用 | 服务端不再提供接收接口，旧地址返回 410 |
| 电子表格 / 共享目录 | 终端 → 共享目录 | 已下线 | 客户端界面不再提供该功能 |

数据文档结构完全一致，服务端只需解析一份 JSON：

- `schema`：固定 `port-security-tool.device-report`
- `schemaVersion`：契约版本，当前 `1.0`
- 兼容原则：**字段只增不改**。新增字段一律可选；删除字段或改变字段含义必须提升 `schemaVersion`。
- 服务端解析时应忽略未知字段（forward compatible）。

---

## 二、通道：局域网代理（发现 + 拉取）

客户端在「数据上传 → 局域网发现 · 供服务端扫描」中开启后：

- 监听 **UDP 18762**：收到 `PST-DISCOVER-V1` 广播或单播探测时，回一份自身身份。
- 监听 **TCP 18762**：提供只读 HTTP 接口。
- 端口可改；**必须与服务端「系统设置 → 客户端发现与拉取 → 发现端口」一致**。
- 客户端启动时会自动添加 UDP/TCP 18762 入站防火墙放行规则。

### 2.1 身份应答（UDP）

服务端向广播地址（`255.255.255.255` 与各网段定向广播）或逐个地址单播发送：

```
PST-DISCOVER-V1
```

客户端回包为一行 JSON（UTF-8）：

```json
{
  "pst": "pst-agent",
  "v": "1",
  "ip": "172.30.245.77",
  "port": 18762,
  "name": "PC-01",
  "computer": "PC-01",
  "deviceId": "pst-fcdfc6bf8275c97c",
  "user": "zhangsan",
  "version": "1.0.0",
  "token": true
}
```

- `pst` 固定为 `pst-agent`，服务端据此判定是否为终端应答。
- `token` 表示该终端是否设置了访问令牌；为 `true` 时后续拉取必须带令牌。

### 2.2 只读接口（TCP）

```
GET  /pst/v1/ping              轻量身份信息（免令牌，供扫描探测用）
GET  /pst/v1/health            服务状态
GET  /pst/v1/device            最近一次采集结果（?refresh=1 强制重新采集）
```

- 鉴权：若终端配置了访问令牌，需带 `X-Api-Token: {令牌}`（或 `Authorization: Bearer {令牌}`、`?token={令牌}`）。
  未配置令牌时不校验，方便内网快速部署。
- `/pst/v1/ping` 免令牌，仅返回主机名、deviceId、版本，用于扫描阶段快速识别。
- `/pst/v1/device` 返回的 JSON 就是第四节的数据结构，可直接入库。
- 一次 WMI 采集约 1–2 秒；客户端「数据上传」页开启自动上传后，会按间隔在后台刷新采集缓存，
  服务端即使不带 `refresh=1` 也能拿到较新的数据。

示例：

```bash
# 扫描探测
curl http://172.30.245.77:18762/pst/v1/ping
# 拉取完整快照
curl -H "X-Api-Token: 你的令牌" "http://172.30.245.77:18762/pst/v1/device?refresh=1"
```

### 2.3 服务端扫描建议

为避免 UDP 广播被交换机或防火墙拦截，服务端扫描建议同时使用三种手段并合并结果：

1. UDP 广播（全局 + 各网卡定向广播）；
2. UDP 单播逐地址探测（网段内每个地址）；
3. TCP 端口探测 `{ip}:18762` 的 `/pst/v1/ping`。

任一路命中即视为发现成功。跨网段部署时 UDP 广播通常不可达，可由服务端指定网段做单播/TCP 探测。

---

## 三、历史通道（已停用，仅供老版本参考）

### 3.1 HTTP 推送（已停用）

老版本客户端会把 JSON `POST` 到服务端接口：

```
POST {接口地址}
Content-Type: application/json; charset=utf-8
Authorization: Bearer {令牌}
X-PST-Schema: port-security-tool.device-report/1.0
X-PST-Report-Id: 8f2c1d9a4b7e4c1f9a6d2e3b5c7f8a90
```

服务端已取消该接收接口，旧地址统一返回 `410 Gone`。老客户端如仍按此上报，会在日志里看到明确提示。

### 3.2 本地查询服务（已停用）

老版本客户端曾在 `127.0.0.1:18760` 提供 `/api/v1/*` 只读接口，当前版本不再启用，
由局域网代理（`0.0.0.0:18762`）取代。

---

## 四、数据结构

完整示例见 `example_1.0.json`，约束见 `schema_1.0.json`。字段说明如下（`?` 表示可能缺省或为 `null`）。

### 根

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `schema` | string | 固定 `port-security-tool.device-report` |
| `schemaVersion` | string | `1.0` |
| `reportId` | string | 本次上报唯一标识，32 位十六进制，幂等键 |
| `collectedAt` | string | ISO 8601 带时区，如 `2026-11-27T14:48:43+08:00` |
| `collectedAtText` | string | 本地可读时间 `yyyy-MM-dd HH:mm:ss` |

### agent（采集端信息）

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `name` | string | 工具名称 |
| `version` | string | 工具版本，如 `1.2.0` |
| `build` | string? | 构建时间 |
| `elevated` | bool | 是否以管理员权限运行（false 时部分端口状态可能读不到） |
| `osArch` | string | `x64` / `x86` |
| `runtime` | string | .NET 运行时版本 |

### device（设备信息）

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `deviceId` | string | **设备稳定标识**：整机 SN + 主网卡 MAC + 计算机名 的 SHA1 前 16 位，前缀 `pst-` |
| `computerName` | string | 计算机名 |
| `currentUser` | string | 采集时的登录用户 |
| `domain` | string | 域 / 工作组 |
| `serialNumber` | string | 整机 SN（来自 BIOS） |
| `osCaption` | string | 操作系统名称，如 `Windows 10 专业版` |
| `osVersion` | string | 系统版本号，如 `10.0.19045` |
| `cpu` | object | `model` / `physicalCores` / `logicalCores` / `currentMHz` / `maxMHz` |
| `memory` | object | `moduleCount` / `totalGB` / `detail`（内存条明细文本） |
| `board` | object | `manufacturer` / `product` / `serialNumber` |
| `bios` | object | `manufacturer` / `version` / `releaseDate`（已归一为 `yyyy-MM-dd`） |
| `disks` | object | `count` / `detail`（型号容量）/ `partitions`（分区占用文本） |
| `network` | object | `primaryMac` / `addresses[]`（`adapter`、`address`、`prefixLength`、`mac`、`primary`）/ `adapters`（网卡明细文本） |

> `deviceId` 用于跨时间串联同一台机器；`computerName` 可能被改名或重复（重装系统），不建议作为主键。

### ports（高危端口）

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `policy` | string | 端口策略标识，当前 `high-risk-84`（84 个预置高危端口） |
| `total` | int | 策略端口总数 |
| `blocked` | int | 已封堵数 |
| `exposed` | int | 未封堵数 |
| `items[]` | array | 每项：`port`、`service`（常见服务名）、`state`（`blocked` / `exposed`）、`critical`（是否严重级） |

判定口径：端口出现在防火墙**入站阻止规则**（含预置策略与手工封禁）即视为 `blocked`；策略清单内未命中阻止规则的视为 `exposed`。

### risk（风险概览）

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `score` | int | 0–100，`未封堵占比 × 100 + 严重端口数 × 8`，上限 100 |
| `level` | string | `high`（存在严重端口暴露）/ `medium`（评分 ≥ 60）/ `low` |
| `criticalCount` | int | 暴露的严重端口数量 |
| `criticalExposed` | int[] | 暴露的严重端口列表 |
| `exposedPorts` | int[] | 全部暴露端口列表 |

严重端口（`critical`）清单：21、23、135、139、445、512、513、514、1433、1521、3306、3389、4444、4899、5432、5900、5985、6379、11211、27017。
该评分是终端侧的粗算，服务端如需自定义口径可忽略 `risk`，直接用 `ports.items` 复算。

---

## 五、服务端落地建议

### 5.1 建表（SQL Server 示例）

```sql
CREATE TABLE dbo.DeviceReport (
    ReportId      CHAR(32)      NOT NULL PRIMARY KEY,
    DeviceId      VARCHAR(24)   NOT NULL,
    ComputerName  NVARCHAR(64)  NOT NULL,
    SerialNumber  NVARCHAR(64)  NULL,
    OsCaption     NVARCHAR(128) NULL,
    PrimaryIp     VARCHAR(45)   NULL,
    PrimaryMac    VARCHAR(32)  NULL,
    Dept          NVARCHAR(64)  NULL,     -- 服务端按网段/命名规则自行归属
    Blocked       INT           NOT NULL,
    Exposed       INT           NOT NULL,
    RiskScore     INT           NOT NULL,
    RiskLevel     VARCHAR(8)    NOT NULL,
    CollectedAt   DATETIME2     NOT NULL,
    ReceivedAt    DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    RawJson       NVARCHAR(MAX) NOT NULL
);

CREATE INDEX IX_DeviceReport_Device ON dbo.DeviceReport (DeviceId, CollectedAt DESC);
CREATE INDEX IX_DeviceReport_Time   ON dbo.DeviceReport (CollectedAt DESC);

-- 端口明细（可选，便于按端口维度统计）
CREATE TABLE dbo.DevicePort (
    ReportId CHAR(32)    NOT NULL,
    Port     SMALLINT    NOT NULL,
    Service  NVARCHAR(32) NULL,
    State    VARCHAR(8)  NOT NULL,
    Critical BIT         NOT NULL,
    CONSTRAINT PK_DevicePort PRIMARY KEY (ReportId, Port)
);
```

### 5.2 接收逻辑要点

1. 解析 JSON，取 `reportId`；已存在则直接返回 `200`（幂等），不重复入库。
2. 事务内写 `DeviceReport` + `DevicePort`。
3. 同一 `deviceId` 只保留最新一条"当前状态"视图，历史明细全部留存用于趋势分析。
4. `ReportId` 建议同时作为 `RawJson` 的检索键，原始报文留档便于追溯契约问题。

### 5.3 服务端拉取采集（当前推荐方式）

```python
# 终端已开启局域网代理（默认 UDP/TCP 18762），服务端扫描发现后直接拉取
import requests, json
r = requests.get("http://172.30.245.77:18762/pst/v1/device?refresh=1", timeout=15,
                 headers={"X-Api-Token": "可选令牌"})
doc = r.json()
assert doc["schema"] == "port-security-tool.device-report"
requests.post("http://10.0.0.10:8080/api/v1/device-reports", json=doc)
```

扫描阶段可先探测免令牌的身份接口，快速判断该地址是否为终端代理：

```python
ping = requests.get("http://172.30.245.77:18762/pst/v1/ping", timeout=3).json()
if ping.get("pst") == "pst-agent":
    print("发现终端:", ping.get("computerName") or ping.get("computer"))
```

### 5.4 常见分析口径

- **单机合规**：`ports.exposed == 0` 视为达标；否则按 `risk.criticalExposed` 排优先级。
- **单位整体**：按 `device.computerName` 前缀或 IP 网段归属部门，统计达标率、平均风险分。
- **趋势**：按 `deviceId + 天` 聚合 `exposed`，画暴露面收敛曲线。
- **异常发现**：某台机器 `blocked` 数量突然下降，通常意味着有人手工放行了端口；`agent.version` 混杂说明补丁推送不齐。

---

## 六、版本演进

| 版本 | 变更 |
| --- | --- |
| 1.0 | 首个版本：设备信息、84 端口封堵状态、风险概览；HTTP 推送、本地服务、文件导出三条通道 |
| 1.0（通道调整） | 停用 HTTP 推送与本地查询服务，统一改为「局域网代理：UDP 发现 + TCP 拉取」单通道；字段结构不变 |

新增能力时只追加字段（如后续可加 `software`、`hotfix`、`antivirus` 等采集项），`schemaVersion` 保持 `1.0`；
结构性调整（拆分/重命名/删除字段）时才升到 `1.1`，并在本表登记。

---

## 附录 A：局域网远程桌面代理（可选模块）

「远程桌面」页开启「响应远程请求」后，终端在 **TCP 18761**（可改）上提供一个极简 HTTP 服务，
用于局域网内被同类工具发现与请求远程桌面。它和上面的数据接口是两套独立协议，不共用端口与令牌。

| 方法 | 路径 | 鉴权 | 说明 |
| --- | --- | --- | --- |
| GET | `/pst/v1/ping` | 免令牌 | 返回计算机名、当前用户、RDP 开关，供局域网发现识别「这台机器装了本工具」 |
| GET | `/pst/v1/state` | 需令牌 | 返回 `rdp`（开关）、`listen`（3389 是否监听）、`allow`（当前临时放行的 IP）、`since` |
| POST | `/pst/v1/rdp-request` | 需令牌 | 在本机弹窗征得用户同意后，**只对请求方 IP** 放行 3389 并开启远程桌面开关，返回 `allowed` |
| POST | `/pst/v1/rdp-close` | 需令牌 | 立即撤销放行、重新封堵 3389 |

鉴权头：`X-Pst-Token: {令牌}`（两端需配置一致）。响应均为 `application/json`，字段值为字符串。

注意：
- 放行前会**临时移除** `Block_Port_3389_TCP_in / _UDP_in` 规则——Windows 防火墙 block 优先于 allow，不删则放行无效；`rdp-close` 会重新加回。
- 放行规则带 `remoteip`，只放行请求方那一个 IP，不会对全网开放 3389。
- 未点「允许」（含 30 秒超时）不会产生任何防火墙或注册表改动。
- 服务端若要据此做「远程运维审计」，可在 `rdp-request` 前后调用 `state` 取 `allow`/`since` 记录留痕。
