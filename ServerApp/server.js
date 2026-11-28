#!/usr/bin/env node
'use strict';

/*
 * PST 终端安全运营中心 - 服务端
 *
 * 零外部依赖：Node 22+ 内置 http + node:sqlite。
 * 接收客户端 POST 的 port-security-tool.device-report 报文，
 * 落库后为数据大屏提供汇总、主机列表、主机详情和历史接口。
 */

const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { DatabaseSync } = require('node:sqlite');

// pkg 打包后 __dirname 在只读快照里；配置、数据库必须放到 exe 同级。
const APP_ROOT = process.pkg ? path.dirname(process.execPath) : __dirname;
const PUBLIC_DIR = path.join(__dirname, 'public');
const CONFIG_PATH = process.env.PST_CONFIG || path.join(APP_ROOT, 'config.json');

const DEFAULT_CONFIG = {
  port: 8080,
  host: '0.0.0.0',
  publicBaseUrl: '',
  ingestPath: '/api/v1/reports',
  ingestToken: 'change-me-ingest-token',
  dashboardToken: '',
  dbFile: 'data/pst.db',
  staleMinutes: 10,
};

// 单 exe 首次运行：在 exe 同级生成可编辑的 config.json
if (!fs.existsSync(CONFIG_PATH)) {
  try {
    fs.writeFileSync(CONFIG_PATH, JSON.stringify(DEFAULT_CONFIG, null, 2) + '\n', 'utf8');
    console.log('[config] 已生成 ' + CONFIG_PATH + '，请修改 ingestToken 后重新启动');
  } catch (err) {
    console.error('[config] 无法生成配置文件：' + err.message);
  }
}

let fileConfig = {};
try {
  fileConfig = JSON.parse(fs.readFileSync(CONFIG_PATH, 'utf8'));
} catch (err) {
  if (fs.existsSync(CONFIG_PATH)) {
    console.error('[config] 读取失败：' + err.message);
  }
}

const config = {
  port: Number(process.env.PST_PORT || fileConfig.port || 8080),
  host: process.env.PST_HOST || fileConfig.host || '0.0.0.0',
  publicBaseUrl: String(process.env.PST_PUBLIC_URL ?? fileConfig.publicBaseUrl ?? '').replace(/\/+$/, ''),
  ingestPath: String(process.env.PST_INGEST_PATH ?? fileConfig.ingestPath ?? '/api/v1/reports'),
  ingestToken: process.env.PST_INGEST_TOKEN ?? fileConfig.ingestToken ?? '',
  dashboardToken: process.env.PST_DASH_TOKEN ?? fileConfig.dashboardToken ?? '',
  dbFile: process.env.PST_DB || fileConfig.dbFile || 'data/pst.db',
  staleMinutes: Math.max(2, Number(process.env.PST_STALE_MINUTES || fileConfig.staleMinutes || 10)),
};
if (!config.ingestPath.startsWith('/')) config.ingestPath = '/' + config.ingestPath;

const DB_PATH = path.isAbsolute(config.dbFile) ? config.dbFile : path.join(APP_ROOT, config.dbFile);
fs.mkdirSync(path.dirname(DB_PATH), { recursive: true });

const db = new DatabaseSync(DB_PATH);
db.exec(`
  PRAGMA journal_mode = WAL;
  PRAGMA synchronous = NORMAL;

  CREATE TABLE IF NOT EXISTS devices (
    device_id       TEXT PRIMARY KEY,
    computer_name   TEXT NOT NULL,
    domain          TEXT,
    current_user    TEXT,
    serial_number   TEXT,
    os_caption      TEXT,
    os_version      TEXT,
    cpu_model       TEXT,
    memory_gb       REAL,
    primary_ip      TEXT,
    primary_mac     TEXT,
    agent_version   TEXT,
    first_seen      INTEGER NOT NULL,
    last_seen       INTEGER NOT NULL,
    last_report_id  TEXT,
    report_count    INTEGER NOT NULL DEFAULT 0,
    risk_score      INTEGER NOT NULL DEFAULT 0,
    risk_level      TEXT NOT NULL DEFAULT 'low',
    ports_total     INTEGER NOT NULL DEFAULT 0,
    ports_blocked   INTEGER NOT NULL DEFAULT 0,
    ports_exposed   INTEGER NOT NULL DEFAULT 0,
    critical_count  INTEGER NOT NULL DEFAULT 0
  );

  CREATE TABLE IF NOT EXISTS reports (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    report_id       TEXT NOT NULL UNIQUE,
    device_id       TEXT NOT NULL,
    computer_name   TEXT,
    collected_at    TEXT,
    collected_ts    INTEGER NOT NULL,
    received_at     INTEGER NOT NULL,
    client_ip       TEXT,
    risk_score      INTEGER NOT NULL DEFAULT 0,
    risk_level      TEXT NOT NULL DEFAULT 'low',
    ports_total     INTEGER NOT NULL DEFAULT 0,
    ports_blocked   INTEGER NOT NULL DEFAULT 0,
    ports_exposed   INTEGER NOT NULL DEFAULT 0,
    critical_count  INTEGER NOT NULL DEFAULT 0,
    payload         TEXT NOT NULL
  );

  CREATE INDEX IF NOT EXISTS idx_reports_device
    ON reports(device_id, collected_ts DESC);
  CREATE INDEX IF NOT EXISTS idx_reports_collected
    ON reports(collected_ts DESC);
  CREATE INDEX IF NOT EXISTS idx_devices_last_seen
    ON devices(last_seen DESC);
`);

// ---------------- 小工具 ----------------

function sendJson(res, status, obj) {
  const body = JSON.stringify(obj);
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
    'Content-Length': Buffer.byteLength(body),
    'Access-Control-Allow-Origin': '*',
  });
  res.end(body);
}

function readBody(req, limit = 16 * 1024 * 1024) {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks = [];
    req.on('data', (chunk) => {
      size += chunk.length;
      if (size > limit) {
        reject(new Error('请求体超过限制'));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}

function asText(v) {
  return v == null ? '' : String(v).trim();
}

function asInt(v, fallback = 0) {
  const n = Number(v);
  return Number.isFinite(n) ? Math.trunc(n) : fallback;
}

function asNum(v, fallback = 0) {
  const n = Number(v);
  return Number.isFinite(n) ? n : fallback;
}

function decodeHeader(v) {
  const s = asText(v);
  if (!s) return '';
  try { return decodeURIComponent(s); } catch { return s; }
}

function clientIp(req) {
  const raw = asText(req.headers['x-forwarded-for']).split(',')[0].trim()
    || req.socket.remoteAddress
    || '';
  return raw.replace(/^::ffff:/, '');
}

function bearer(req) {
  const h = asText(req.headers.authorization);
  return h.startsWith('Bearer ') ? h.slice(7).trim() : '';
}

function dashboardAllowed(req, url) {
  if (!config.dashboardToken) return true;
  const token = asText(req.headers['x-dashboard-token']) || asText(url.searchParams.get('token'));
  return token === config.dashboardToken;
}

function requestHostname(req) {
  const host = asText(req.headers.host);
  try {
    return new URL('http://' + (host || 'localhost')).hostname;
  } catch {
    return host.split(':')[0] || 'localhost';
  }
}

function baseUrlFor(req) {
  if (config.publicBaseUrl) return config.publicBaseUrl;
  const proto = asText(req.headers['x-forwarded-proto']).split(',')[0].trim() || 'http';
  const host = config.host === '0.0.0.0' ? requestHostname(req) : config.host;
  return proto + '://' + host + ':' + config.port;
}

function settingsPayload(req) {
  const base = baseUrlFor(req).replace(/\/+$/, '');
  return {
    host: config.host,
    port: config.port,
    publicBaseUrl: config.publicBaseUrl,
    ingestPath: config.ingestPath,
    staleMinutes: config.staleMinutes,
    ingestTokenSet: !!config.ingestToken,
    dashboardTokenSet: !!config.dashboardToken,
    ingestUrl: base + config.ingestPath,
    dashboardUrl: base + '/',
    configPath: CONFIG_PATH,
    dbFile: config.dbFile,
  };
}

function persistConfig() {
  let current = {};
  try { current = JSON.parse(fs.readFileSync(CONFIG_PATH, 'utf8')); } catch { }
  const next = Object.assign({}, current, {
    port: config.port,
    host: config.host,
    publicBaseUrl: config.publicBaseUrl,
    ingestPath: config.ingestPath,
    ingestToken: config.ingestToken,
    dashboardToken: config.dashboardToken,
    dbFile: config.dbFile,
    staleMinutes: config.staleMinutes,
  });
  const tmp = CONFIG_PATH + '.tmp-' + process.pid;
  fs.writeFileSync(tmp, JSON.stringify(next, null, 2) + '\n', 'utf8');
  try {
    fs.renameSync(tmp, CONFIG_PATH);
  } catch {
    fs.copyFileSync(tmp, CONFIG_PATH);
    try { fs.unlinkSync(tmp); } catch { }
  }
}

const RESERVED_PATHS = new Set([
  '/', '/api/health', '/api/config', '/api/settings', '/api/summary', '/api/hosts',
]);

function applySettings(input) {
  const errors = [];
  const values = {};

  if (Object.prototype.hasOwnProperty.call(input, 'host')) {
    const host = asText(input.host);
    if (!host || host.length > 120 || !/^[0-9a-zA-Z.:\-]+$/.test(host)) {
      errors.push('监听地址格式不正确（可填 0.0.0.0、127.0.0.1、IP 或主机名）');
    } else values.host = host;
  }

  if (Object.prototype.hasOwnProperty.call(input, 'port')) {
    const port = asInt(input.port, -1);
    if (port < 1 || port > 65535) errors.push('端口必须是 1-65535 的整数');
    else values.port = port;
  }

  if (Object.prototype.hasOwnProperty.call(input, 'publicBaseUrl')) {
    let url = asText(input.publicBaseUrl).replace(/\/+$/, '');
    if (url) {
      try {
        const u = new URL(url);
        if (u.protocol !== 'http:' && u.protocol !== 'https:') throw new Error('bad protocol');
        if (!u.hostname) throw new Error('bad host');
        url = u.toString().replace(/\/+$/, '');
      } catch {
        errors.push('对外访问地址必须是完整的 http:// 或 https:// 地址');
      }
    }
    values.publicBaseUrl = url;
  }

  if (Object.prototype.hasOwnProperty.call(input, 'ingestPath')) {
    let p = asText(input.ingestPath);
    if (!p.startsWith('/')) p = '/' + p;
    p = p.replace(/\/{2,}/g, '/');
    if (p.length > 120 || /\s|\?|#/.test(p)) errors.push('上报路径不能包含空格、? 或 #');
    else if (RESERVED_PATHS.has(p) || p.startsWith('/api/hosts/')) errors.push('上报路径与系统接口冲突');
    else values.ingestPath = p;
  }

  if (Object.prototype.hasOwnProperty.call(input, 'staleMinutes')) {
    const m = asInt(input.staleMinutes, -1);
    if (m < 2 || m > 1440) errors.push('离线判定必须是 2-1440 分钟');
    else values.staleMinutes = m;
  }

  if (input.clearIngestToken === true) {
    values.ingestToken = '';
  } else if (asText(input.ingestToken).length > 0) {
    const t = asText(input.ingestToken);
    if (t.length < 8 || t.length > 200) errors.push('上报令牌长度需为 8-200 个字符');
    else values.ingestToken = t;
  }

  if (input.clearDashboardToken === true) {
    values.dashboardToken = '';
  } else if (asText(input.dashboardToken).length > 0) {
    const t = asText(input.dashboardToken);
    if (t.length < 8 || t.length > 200) errors.push('大屏令牌长度需为 8-200 个字符');
    else values.dashboardToken = t;
  }

  if (errors.length) return { ok: false, errors };

  const old = {
    host: config.host,
    port: config.port,
    publicBaseUrl: config.publicBaseUrl,
    ingestPath: config.ingestPath,
    ingestToken: config.ingestToken,
    dashboardToken: config.dashboardToken,
    staleMinutes: config.staleMinutes,
  };
  Object.assign(config, values);

  try {
    persistConfig();
  } catch (err) {
    Object.assign(config, old);
    return { ok: false, errors: ['写入 config.json 失败：' + err.message] };
  }

  return {
    ok: true,
    restartRequired: config.host !== old.host || config.port !== old.port,
    dashboardTokenChanged: config.dashboardToken !== old.dashboardToken,
  };
}

// ---------------- 报文归一化 ----------------

function normalizeReport(payload, req, bodyText) {
  const device = payload.device || {};
  const risk = payload.risk || {};
  const ports = payload.ports || {};
  const network = device.network || {};
  const addresses = Array.isArray(network.addresses) ? network.addresses : [];
  const primary = addresses.find((a) => a && a.primary) || addresses[0] || {};

  const computerName = asText(device.computerName)
    || decodeHeader(req.headers['x-pst-computer'])
    || '未知主机';

  let deviceId = asText(device.deviceId);
  if (!deviceId) {
    const seed = [computerName, device.serialNumber, primary.mac, primary.address].join('|');
    deviceId = 'pst-' + crypto.createHash('sha1').update(seed).digest('hex').slice(0, 16);
  }

  const items = Array.isArray(ports.items) ? ports.items : [];
  const exposedItems = items.filter((p) => p && p.state === 'exposed');
  const exposed = Number.isFinite(Number(ports.exposed))
    ? asInt(ports.exposed)
    : exposedItems.length;
  const criticalFromItems = exposedItems.filter((p) => p && p.critical).length;
  const criticalCount = Number.isFinite(Number(risk.criticalCount))
    ? asInt(risk.criticalCount)
    : criticalFromItems;

  const collectedAt = asText(payload.collectedAt) || asText(payload.collectedAtText);
  let collectedTs = Date.parse(collectedAt);
  if (!Number.isFinite(collectedTs)) collectedTs = Date.now();

  const reportId = asText(req.headers['x-pst-report-id'])
    || asText(payload.reportId)
    || crypto.randomUUID().replace(/-/g, '');

  return {
    reportId,
    deviceId,
    computerName,
    domain: asText(device.domain),
    currentUser: asText(device.currentUser),
    serialNumber: asText(device.serialNumber),
    osCaption: asText(device.osCaption),
    osVersion: asText(device.osVersion),
    cpuModel: asText(device.cpu && device.cpu.model),
    memoryGb: asNum(device.memory && device.memory.totalGB),
    primaryIp: asText(primary.address),
    primaryMac: asText(network.primaryMac) || asText(primary.mac),
    agentVersion: asText(payload.agent && payload.agent.version),
    collectedAt,
    collectedTs,
    riskScore: Math.max(0, Math.min(100, asInt(risk.score))),
    riskLevel: asText(risk.level) || 'low',
    portsTotal: asInt(ports.total),
    portsBlocked: asInt(ports.blocked),
    portsExposed: exposed,
    criticalCount,
    payload: bodyText,
    clientIp: clientIp(req),
  };
}

// ---------------- 入库 ----------------

const insertReportStmt = db.prepare(`
  INSERT OR IGNORE INTO reports
    (report_id, device_id, computer_name, collected_at, collected_ts, received_at,
     client_ip, risk_score, risk_level, ports_total, ports_blocked, ports_exposed,
     critical_count, payload)
  VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
`);

const insertDeviceStmt = db.prepare(`
  INSERT INTO devices
    (device_id, computer_name, domain, current_user, serial_number, os_caption, os_version,
     cpu_model, memory_gb, primary_ip, primary_mac, agent_version, first_seen, last_seen,
     last_report_id, report_count, risk_score, risk_level, ports_total, ports_blocked,
     ports_exposed, critical_count)
  VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?, ?, ?, ?, ?)
`);

const updateDeviceStmt = db.prepare(`
  UPDATE devices SET
    computer_name = ?, domain = ?, current_user = ?, serial_number = ?, os_caption = ?,
    os_version = ?, cpu_model = ?, memory_gb = ?, primary_ip = ?, primary_mac = ?,
    agent_version = ?, last_seen = ?, last_report_id = ?, risk_score = ?, risk_level = ?,
    ports_total = ?, ports_blocked = ?, ports_exposed = ?, critical_count = ?
  WHERE device_id = ?
`);

const bumpDeviceStmt = db.prepare(`
  UPDATE devices SET report_count = report_count + 1 WHERE device_id = ?
`);

function ingestReport(req, res, bodyText) {
  if (!config.ingestToken) {
    return sendJson(res, 503, { ok: false, error: '服务端未配置 ingestToken' });
  }
  if (bearer(req) !== config.ingestToken) {
    return sendJson(res, 401, { ok: false, error: 'invalid ingest token' });
  }

  let payload;
  try {
    payload = JSON.parse(bodyText);
  } catch {
    return sendJson(res, 400, { ok: false, error: 'invalid json' });
  }
  if (!payload || typeof payload !== 'object') {
    return sendJson(res, 400, { ok: false, error: 'empty body' });
  }

  const schema = asText(payload.schema);
  if (schema && schema !== 'port-security-tool.device-report') {
    return sendJson(res, 400, { ok: false, error: 'unsupported schema: ' + schema });
  }

  const n = normalizeReport(payload, req, bodyText);
  const now = Date.now();

  db.exec('BEGIN IMMEDIATE');
  try {
    const result = insertReportStmt.run(
      n.reportId, n.deviceId, n.computerName, n.collectedAt, n.collectedTs, now,
      n.clientIp, n.riskScore, n.riskLevel, n.portsTotal, n.portsBlocked,
      n.portsExposed, n.criticalCount, n.payload
    );

    if (result.changes === 0) {
      db.exec('COMMIT');
      return sendJson(res, 200, {
        ok: true,
        duplicate: true,
        deviceId: n.deviceId,
        reportId: n.reportId,
        message: 'report already exists',
      });
    }

    const existing = db.prepare('SELECT last_seen FROM devices WHERE device_id = ?').get(n.deviceId);
    if (!existing) {
      insertDeviceStmt.run(
        n.deviceId, n.computerName, n.domain, n.currentUser, n.serialNumber,
        n.osCaption, n.osVersion, n.cpuModel, n.memoryGb, n.primaryIp,
        n.primaryMac, n.agentVersion, n.collectedTs, n.collectedTs,
        n.reportId, n.riskScore, n.riskLevel, n.portsTotal, n.portsBlocked,
        n.portsExposed, n.criticalCount
      );
    } else {
      bumpDeviceStmt.run(n.deviceId);
      if (n.collectedTs >= asInt(existing.last_seen)) {
        updateDeviceStmt.run(
          n.computerName, n.domain, n.currentUser, n.serialNumber, n.osCaption,
          n.osVersion, n.cpuModel, n.memoryGb, n.primaryIp, n.primaryMac,
          n.agentVersion, n.collectedTs, n.reportId, n.riskScore, n.riskLevel,
          n.portsTotal, n.portsBlocked, n.portsExposed, n.criticalCount,
          n.deviceId
        );
      }
    }

    db.exec('COMMIT');
  } catch (err) {
    try { db.exec('ROLLBACK'); } catch { }
    console.error('[ingest] ' + err.message);
    return sendJson(res, 500, { ok: false, error: 'database error' });
  }

  console.log(`[ingest] ${n.computerName} (${n.primaryIp || '-'}) risk=${n.riskScore}/${n.riskLevel} exposed=${n.portsExposed}`);
  return sendJson(res, 200, {
    ok: true,
    duplicate: false,
    deviceId: n.deviceId,
    reportId: n.reportId,
    receivedAt: now,
  });
}

// ---------------- 查询 ----------------

function getSummary() {
  const now = Date.now();
  const staleMs = config.staleMinutes * 60000;
  const devices = db.prepare('SELECT * FROM devices').all();

  let online = 0;
  let high = 0;
  let medium = 0;
  let low = 0;
  let exposedTotal = 0;
  let criticalTotal = 0;
  let scoreSum = 0;

  for (const d of devices) {
    const isOnline = now - asInt(d.last_seen) <= staleMs;
    if (isOnline) online++;
    if (d.risk_level === 'high') high++;
    else if (d.risk_level === 'medium') medium++;
    else low++;
    exposedTotal += asInt(d.ports_exposed);
    criticalTotal += asInt(d.critical_count);
    scoreSum += asInt(d.risk_score);
  }

  const latestStmt = db.prepare(
    'SELECT payload FROM reports WHERE device_id = ? ORDER BY collected_ts DESC LIMIT 1'
  );
  const portMap = new Map();
  for (const d of devices) {
    const row = latestStmt.get(d.device_id);
    if (!row) continue;
    let report;
    try { report = JSON.parse(row.payload); } catch { continue; }
    const items = report && report.ports && Array.isArray(report.ports.items)
      ? report.ports.items : [];
    for (const p of items) {
      if (!p || p.state !== 'exposed') continue;
      const key = String(p.port);
      const cur = portMap.get(key) || {
        port: asInt(p.port),
        service: asText(p.service) || '-',
        critical: !!p.critical,
        hosts: 0,
      };
      cur.hosts++;
      portMap.set(key, cur);
    }
  }
  const topPorts = [...portMap.values()]
    .sort((a, b) => (b.hosts - a.hosts) || (a.port - b.port))
    .slice(0, 10);

  const hourMs = 3600000;
  const nowHour = Math.floor(now / hourMs) * hourMs;
  const start = nowHour - 23 * hourMs;
  const trendRows = db.prepare(
    'SELECT collected_ts FROM reports WHERE collected_ts >= ?'
  ).all(start);
  const trend = Array.from({ length: 24 }, (_, i) => ({
    t: start + i * hourMs,
    count: 0,
  }));
  for (const r of trendRows) {
    const idx = Math.floor((asInt(r.collected_ts) - start) / hourMs);
    if (idx >= 0 && idx < 24) trend[idx].count++;
  }

  const recent = db.prepare(`
    SELECT report_id, device_id, computer_name, collected_at, collected_ts,
           risk_score, risk_level, ports_exposed, critical_count
    FROM reports ORDER BY collected_ts DESC, id DESC LIMIT 8
  `).all();

  const lastSeen = devices.reduce((m, d) => Math.max(m, asInt(d.last_seen)), 0);

  return {
    generatedAt: now,
    staleMinutes: config.staleMinutes,
    totals: {
      hosts: devices.length,
      online,
      offline: Math.max(0, devices.length - online),
      reports: db.prepare('SELECT COUNT(*) AS n FROM reports').get().n,
      exposedPorts: exposedTotal,
      criticalExposed: criticalTotal,
      avgScore: devices.length ? Math.round(scoreSum / devices.length) : 0,
    },
    risk: { high, medium, low },
    topPorts,
    trend,
    recent,
    lastSeen: lastSeen || null,
  };
}

function listHosts(url) {
  const now = Date.now();
  const staleMs = config.staleMinutes * 60000;
  const q = asText(url.searchParams.get('q')).toLowerCase();
  const risk = asText(url.searchParams.get('risk')).toLowerCase();
  const status = asText(url.searchParams.get('status')).toLowerCase();

  let rows = db.prepare(`
    SELECT * FROM devices
    ORDER BY risk_score DESC, last_seen DESC, computer_name ASC
  `).all().map((d) => ({
    deviceId: d.device_id,
    computerName: d.computer_name,
    domain: d.domain,
    currentUser: d.current_user,
    serialNumber: d.serial_number,
    osCaption: d.os_caption,
    osVersion: d.os_version,
    cpuModel: d.cpu_model,
    memoryGb: d.memory_gb,
    primaryIp: d.primary_ip,
    primaryMac: d.primary_mac,
    agentVersion: d.agent_version,
    firstSeen: d.first_seen,
    lastSeen: d.last_seen,
    reportCount: d.report_count,
    riskScore: d.risk_score,
    riskLevel: d.risk_level,
    portsTotal: d.ports_total,
    portsBlocked: d.ports_blocked,
    portsExposed: d.ports_exposed,
    criticalCount: d.critical_count,
    online: now - d.last_seen <= staleMs,
  }));

  if (q) {
    rows = rows.filter((d) => [
      d.computerName, d.primaryIp, d.primaryMac, d.currentUser, d.domain,
      d.serialNumber, d.osCaption,
    ].some((v) => asText(v).toLowerCase().includes(q)));
  }
  if (risk && risk !== 'all') rows = rows.filter((d) => d.riskLevel === risk);
  if (status === 'online') rows = rows.filter((d) => d.online);
  if (status === 'offline') rows = rows.filter((d) => !d.online);
  return rows;
}

function getHost(deviceId) {
  const d = db.prepare('SELECT * FROM devices WHERE device_id = ?').get(deviceId);
  if (!d) return null;
  const reportRow = db.prepare(
    'SELECT * FROM reports WHERE device_id = ? ORDER BY collected_ts DESC LIMIT 1'
  ).get(deviceId);
  const history = db.prepare(`
    SELECT report_id, collected_at, collected_ts, risk_score, risk_level,
           ports_total, ports_blocked, ports_exposed, critical_count
    FROM reports WHERE device_id = ? ORDER BY collected_ts DESC LIMIT 30
  `).all(deviceId).reverse();

  let report = null;
  if (reportRow) {
    try { report = JSON.parse(reportRow.payload); } catch { }
  }

  return {
    device: {
      deviceId: d.device_id,
      computerName: d.computer_name,
      domain: d.domain,
      currentUser: d.current_user,
      serialNumber: d.serial_number,
      osCaption: d.os_caption,
      osVersion: d.os_version,
      cpuModel: d.cpu_model,
      memoryGb: d.memory_gb,
      primaryIp: d.primary_ip,
      primaryMac: d.primary_mac,
      agentVersion: d.agent_version,
      firstSeen: d.first_seen,
      lastSeen: d.last_seen,
      reportCount: d.report_count,
      riskScore: d.risk_score,
      riskLevel: d.risk_level,
      portsTotal: d.ports_total,
      portsBlocked: d.ports_blocked,
      portsExposed: d.ports_exposed,
      criticalCount: d.critical_count,
      online: Date.now() - d.last_seen <= config.staleMinutes * 60000,
    },
    report,
    history,
  };
}

// ---------------- 静态文件 ----------------

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.woff2': 'font/woff2',
};

function serveStatic(req, res, pathname) {
  let rel = pathname === '/' ? 'index.html' : pathname.replace(/^\/+/, '');
  let file = path.normalize(path.join(PUBLIC_DIR, rel));
  if (!file.startsWith(PUBLIC_DIR)) {
    res.writeHead(403); res.end('forbidden'); return;
  }
  if (!fs.existsSync(file) || fs.statSync(file).isDirectory()) {
    file = path.join(PUBLIC_DIR, 'index.html');
  }
  const ext = path.extname(file).toLowerCase();
  const data = fs.readFileSync(file);
  res.writeHead(200, {
    'Content-Type': MIME[ext] || 'application/octet-stream',
    'Content-Length': data.length,
    'Cache-Control': ext === '.html' ? 'no-store' : 'no-cache',
  });
  res.end(data);
}

// ---------------- HTTP ----------------

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host || 'localhost'}`);
  const pathname = url.pathname;

  try {
    if (req.method === 'OPTIONS') {
      res.writeHead(204, {
        'Access-Control-Allow-Origin': '*',
        'Access-Control-Allow-Headers': 'Content-Type, Authorization, X-PST-Report-Id, X-PST-Computer, X-PST-Schema, X-Dashboard-Token',
        'Access-Control-Allow-Methods': 'GET, POST, OPTIONS',
      });
      res.end();
      return;
    }

    if (req.method === 'POST' && (
      pathname === config.ingestPath
      || pathname === '/api/v1/reports'
      || pathname === '/api/report'
      || pathname === '/report'
    )) {
      const body = await readBody(req);
      ingestReport(req, res, body);
      return;
    }

    if (req.method === 'GET' && pathname === '/api/health') {
      sendJson(res, 200, { ok: true, now: Date.now(), version: 1 });
      return;
    }

    if (req.method === 'GET' && pathname === '/api/config') {
      sendJson(res, 200, {
        authRequired: !!config.dashboardToken,
        staleMinutes: config.staleMinutes,
        schema: 'port-security-tool.device-report/1.0',
      });
      return;
    }

    if (pathname.startsWith('/api/')) {
      if (!dashboardAllowed(req, url)) {
        sendJson(res, 401, { ok: false, error: 'dashboard token required' });
        return;
      }

      if (req.method === 'GET' && pathname === '/api/settings') {
        sendJson(res, 200, settingsPayload(req));
        return;
      }

      if (req.method === 'POST' && pathname === '/api/settings') {
        const body = await readBody(req, 1024 * 1024);
        let input;
        try { input = JSON.parse(body); } catch { input = null; }
        if (!input || typeof input !== 'object') {
          sendJson(res, 400, { ok: false, errors: ['请求体不是有效 JSON'] });
          return;
        }
        const result = applySettings(input);
        if (!result.ok) {
          sendJson(res, 400, { ok: false, errors: result.errors });
          return;
        }
        sendJson(res, 200, {
          ok: true,
          restartRequired: result.restartRequired,
          dashboardTokenChanged: result.dashboardTokenChanged,
          settings: settingsPayload(req),
        });
        return;
      }

      if (req.method === 'GET' && pathname === '/api/summary') {
        sendJson(res, 200, getSummary());
        return;
      }
      if (req.method === 'GET' && pathname === '/api/hosts') {
        sendJson(res, 200, { hosts: listHosts(url) });
        return;
      }
      const hostMatch = pathname.match(/^\/api\/hosts\/([^/]+)$/);
      if (req.method === 'GET' && hostMatch) {
        const data = getHost(decodeURIComponent(hostMatch[1]));
        if (!data) sendJson(res, 404, { ok: false, error: 'host not found' });
        else sendJson(res, 200, data);
        return;
      }
      sendJson(res, 404, { ok: false, error: 'not found' });
      return;
    }

    if (req.method === 'GET' || req.method === 'HEAD') {
      serveStatic(req, res, pathname);
      return;
    }

    sendJson(res, 405, { ok: false, error: 'method not allowed' });
  } catch (err) {
    console.error('[http] ' + (err && err.stack ? err.stack : err));
    if (!res.headersSent) sendJson(res, 500, { ok: false, error: 'internal error' });
    else try { res.end(); } catch { }
  }
});

server.listen(config.port, config.host, () => {
  const shownHost = config.host === '0.0.0.0' ? 'localhost' : config.host;
  console.log('');
  console.log('  PST 终端安全运营中心');
  console.log('  ----------------------------------------');
  console.log('  数据大屏 : http://' + shownHost + ':' + config.port + '/');
  console.log('  上报接口 : POST ' + (config.publicBaseUrl || ('http://' + shownHost + ':' + config.port)) + config.ingestPath);
  console.log('  数据库   : ' + DB_PATH);
  console.log('  上报令牌 : ' + (config.ingestToken ? '已配置' : '未配置（上报会被拒绝）'));
  console.log('  大屏令牌 : ' + (config.dashboardToken ? '已启用' : '未启用（局域网可直接访问）'));
  console.log('  ----------------------------------------');
  console.log('');
});

process.on('SIGINT', () => {
  try { db.close(); } catch { }
  process.exit(0);
});
