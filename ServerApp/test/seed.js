#!/usr/bin/env node
'use strict';

/*
 * 生成演示数据：直接写入服务端的 SQLite 库。
 * 服务端已取消「客户端主动上传」通道，数据只来自扫描拉取，
 * 因此演示数据改为直接落库（表结构与服务端一致）。
 *
 * 用法：node test/seed.js
 * 环境变量：PST_DB（数据库文件路径，默认 data/pst.db）
 *           PST_CONFIG（配置文件路径，默认 ../config.json）
 */

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { DatabaseSync } = require('node:sqlite');

const ROOT = path.join(__dirname, '..');
const CONFIG_PATH = process.env.PST_CONFIG || path.join(ROOT, 'config.json');

let cfg = {};
try { cfg = JSON.parse(fs.readFileSync(CONFIG_PATH, 'utf8')); } catch { }

const DB_FILE = process.env.PST_DB || cfg.dbFile || 'data/pst.db';
const DB_PATH = path.isAbsolute(DB_FILE) ? DB_FILE : path.join(ROOT, DB_FILE);
const EXAMPLE = path.join(ROOT, '..', 'API接口文档_v1.0', 'example_1.0.json');

if (!fs.existsSync(EXAMPLE)) {
  console.error('找不到示例报文：' + EXAMPLE);
  process.exit(1);
}

fs.mkdirSync(path.dirname(DB_PATH), { recursive: true });
const db = new DatabaseSync(DB_PATH);
db.exec(`
  PRAGMA journal_mode = WAL;
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

const bumpDeviceStmt = db.prepare('UPDATE devices SET report_count = report_count + 1 WHERE device_id = ?');
const updateDeviceStmt = db.prepare(`
  UPDATE devices SET
    computer_name = ?, domain = ?, current_user = ?, serial_number = ?, os_caption = ?,
    os_version = ?, cpu_model = ?, memory_gb = ?, primary_ip = ?, primary_mac = ?,
    agent_version = ?, last_seen = ?, last_report_id = ?, risk_score = ?, risk_level = ?,
    ports_total = ?, ports_blocked = ?, ports_exposed = ?, critical_count = ?
  WHERE device_id = ?
`);

const base = JSON.parse(fs.readFileSync(EXAMPLE, 'utf8'));

function clone(v) { return JSON.parse(JSON.stringify(v)); }

function makeReport(opts) {
  const r = clone(base);
  const now = new Date(Date.now() - (opts.minutesAgo || 0) * 60000);
  const pad = (n) => String(n).padStart(2, '0');
  const iso = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}T${pad(now.getHours())}:${pad(now.getMinutes())}:${pad(now.getSeconds())}+08:00`;

  r.reportId = crypto.randomUUID().replace(/-/g, '');
  r.collectedAt = iso;
  r.collectedAtText = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())} ${pad(now.getHours())}:${pad(now.getMinutes())}:${pad(now.getSeconds())}`;

  r.device.deviceId = opts.deviceId;
  r.device.computerName = opts.computerName;
  r.device.currentUser = opts.user;
  r.device.domain = opts.domain;
  r.device.serialNumber = opts.serial;
  r.device.osCaption = opts.os;
  r.device.osVersion = opts.osVersion;
  r.device.cpu.model = opts.cpu;
  r.device.memory.totalGB = opts.memoryGb;
  r.device.network.primaryMac = opts.mac;
  r.device.network.addresses = [{
    adapter: '以太网适配器',
    address: opts.ip,
    prefixLength: 24,
    primary: true,
    mac: opts.mac,
  }];
  r.device.network.adapters = `[以太网适配器 : ${opts.mac}]`;

  const exposed = new Set(opts.exposedPorts || []);
  for (const item of r.ports.items) {
    item.state = exposed.has(item.port) ? 'exposed' : 'blocked';
  }
  r.ports.total = r.ports.items.length;
  r.ports.exposed = exposed.size;
  r.ports.blocked = r.ports.total - exposed.size;
  r.risk.score = opts.score;
  r.risk.level = opts.level;
  r.risk.criticalCount = opts.criticalPorts ? opts.criticalPorts.length : 0;
  r.risk.criticalExposed = opts.criticalPorts || [];
  r.risk.exposedPorts = [...exposed].sort((a, b) => a - b);
  return r;
}

const hosts = [
  {
    deviceId: 'pst-demo-ops-01',
    computerName: 'OPS-PC-01',
    user: 'ops.zhang',
    domain: 'CORP',
    serial: 'SN-OPS-0001',
    os: 'Windows 10 专业版',
    osVersion: '10.0.19045',
    cpu: 'Intel(R) Core(TM) i5-10400 @ 2.90GHz',
    memoryGb: 16,
    ip: '192.168.1.31',
    mac: '00:1A:2B:3C:4D:01',
    exposedPorts: [],
    criticalPorts: [],
    score: 0,
    level: 'low',
    minutesAgo: 4,
  },
  {
    deviceId: 'pst-demo-fin-db-02',
    computerName: 'FIN-DB-02',
    user: 'sqlsvc',
    domain: 'CORP',
    serial: 'SN-FIN-0042',
    os: 'Windows Server 2019',
    osVersion: '10.0.17763',
    cpu: 'Intel(R) Xeon(R) Silver 4210 @ 2.20GHz',
    memoryGb: 64,
    ip: '192.168.1.42',
    mac: '00:1A:2B:3C:4D:42',
    exposedPorts: [445, 1433, 3306, 3389],
    criticalPorts: [445, 1433, 3306, 3389],
    score: 86,
    level: 'high',
    minutesAgo: 2,
  },
  {
    deviceId: 'pst-demo-hr-07',
    computerName: 'HR-PC-07',
    user: 'hr.li',
    domain: 'CORP',
    serial: 'SN-HR-0007',
    os: 'Windows 11 专业版',
    osVersion: '10.0.22631',
    cpu: 'Intel(R) Core(TM) i7-12700 @ 2.10GHz',
    memoryGb: 32,
    ip: '192.168.1.57',
    mac: '00:1A:2B:3C:4D:57',
    exposedPorts: [139, 445],
    criticalPorts: [139, 445],
    score: 42,
    level: 'medium',
    minutesAgo: 7,
  },
];

function store(report) {
  const device = report.device || {};
  const risk = report.risk || {};
  const ports = report.ports || {};
  const network = device.network || {};
  const addresses = Array.isArray(network.addresses) ? network.addresses : [];
  const primary = addresses.find((a) => a && a.primary) || addresses[0] || {};

  const collectedTs = Date.parse(report.collectedAt) || Date.now();
  const now = Date.now();
  const bodyText = JSON.stringify(report);

  db.exec('BEGIN IMMEDIATE');
  try {
    const inserted = insertReportStmt.run(
      report.reportId, device.deviceId, device.computerName, report.collectedAt, collectedTs, now,
      primary.address || '', risk.score || 0, risk.level || 'low', ports.total || 0,
      ports.blocked || 0, ports.exposed || 0, risk.criticalCount || 0, bodyText
    );
    if (inserted.changes === 0) {
      db.exec('COMMIT');
      console.log(`跳过（已存在）${device.computerName}`);
      return false;
    }

    const existing = db.prepare('SELECT last_seen FROM devices WHERE device_id = ?').get(device.deviceId);
    if (!existing) {
      insertDeviceStmt.run(
        device.deviceId, device.computerName, device.domain || '', device.currentUser || '',
        device.serialNumber || '', device.osCaption || '', device.osVersion || '',
        (device.cpu && device.cpu.model) || '', (device.memory && device.memory.totalGB) || 0,
        primary.address || '', network.primaryMac || '', (report.agent && report.agent.version) || '',
        collectedTs, collectedTs, report.reportId, risk.score || 0, risk.level || 'low',
        ports.total || 0, ports.blocked || 0, ports.exposed || 0, risk.criticalCount || 0
      );
    } else {
      bumpDeviceStmt.run(device.deviceId);
      if (collectedTs >= existing.last_seen) {
        updateDeviceStmt.run(
          device.computerName, device.domain || '', device.currentUser || '', device.serialNumber || '',
          device.osCaption || '', device.osVersion || '', (device.cpu && device.cpu.model) || '',
          (device.memory && device.memory.totalGB) || 0, primary.address || '',
          network.primaryMac || '', (report.agent && report.agent.version) || '',
          collectedTs, report.reportId, risk.score || 0, risk.level || 'low',
          ports.total || 0, ports.blocked || 0, ports.exposed || 0, risk.criticalCount || 0,
          device.deviceId
        );
      }
    }
    db.exec('COMMIT');
  } catch (err) {
    try { db.exec('ROLLBACK'); } catch { }
    console.error('写入失败：' + err.message);
    return false;
  }
  console.log(`已写入 ${device.computerName}（${primary.address || '-'}）风险 ${risk.score}/${risk.level}`);
  return true;
}

function main() {
  let ok = 0;
  for (const h of hosts) {
    if (h.level !== 'low') {
      const oldExposed = h.exposedPorts.slice(0, Math.max(1, h.exposedPorts.length - 1));
      const old = makeReport({
        ...h,
        minutesAgo: h.minutesAgo + 55,
        score: Math.max(0, h.score - 21),
        exposedPorts: oldExposed,
        criticalPorts: [],
        level: h.level === 'high' ? 'medium' : 'low',
      });
      if (store(old)) ok++;
    }
    if (store(makeReport(h))) ok++;
  }
  console.log(`\n完成：写入 ${ok} 份快照。数据库：${DB_PATH}`);
  console.log('启动服务端后打开大屏即可看到效果。');
  try { db.close(); } catch { }
}

main();
