#!/usr/bin/env node
'use strict';

/*
 * 生成演示数据：向本地服务端推送 3 台主机的多份快照。
 * 用法：node test/seed.js
 * 环境变量：PST_SERVER（默认 http://127.0.0.1:8080）
 *           PST_INGEST_TOKEN（默认读 ../config.json）
 */

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

const ROOT = path.join(__dirname, '..');
let cfg = {};
try { cfg = JSON.parse(fs.readFileSync(path.join(ROOT, 'config.json'), 'utf8')); } catch { }

const SERVER = (process.env.PST_SERVER || `http://127.0.0.1:${cfg.port || 8080}`).replace(/\/+$/, '');
const TOKEN = process.env.PST_INGEST_TOKEN || cfg.ingestToken || '';
const EXAMPLE = path.join(ROOT, '..', 'API接口文档_v1.0', 'example_1.0.json');

if (!TOKEN) {
  console.error('缺少 ingestToken，请设置 PST_INGEST_TOKEN 或修改 config.json');
  process.exit(1);
}
if (!fs.existsSync(EXAMPLE)) {
  console.error('找不到示例报文：' + EXAMPLE);
  process.exit(1);
}

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

async function post(report) {
  const res = await fetch(SERVER + '/api/v1/reports', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json; charset=utf-8',
      'Authorization': 'Bearer ' + TOKEN,
      'X-PST-Report-Id': report.reportId,
      'X-PST-Computer': report.device.computerName,
      'X-PST-Schema': 'port-security-tool.device-report/1.0',
    },
    body: JSON.stringify(report),
  });
  const text = await res.text();
  console.log(`POST ${report.device.computerName} -> HTTP ${res.status} ${text}`);
  return res.ok;
}

async function main() {
  let ok = 0;
  for (const h of hosts) {
    // 每台主机先推一份较早的快照，再推当前快照，形成历史
    if (h.level !== 'low') {
      const oldExposed = h.exposedPorts.slice(0, Math.max(1, h.exposedPorts.length - 1));
      const old = makeReport({
        ...h,
        minutesAgo: h.minutesAgo + 55,
        score: Math.max(0, h.score - 21),
        exposedPorts: oldExposed,
        // 早期快照只暴露普通端口，避免出现“无严重端口但等级仍是高危”的矛盾
        criticalPorts: [],
        level: h.level === 'high' ? 'medium' : 'low',
      });
      if (await post(old)) ok++;
    }
    const cur = makeReport(h);
    if (await post(cur)) ok++;
  }
  console.log(`\n完成：成功 ${ok} 份。打开 ${SERVER}/ 查看大屏。`);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
