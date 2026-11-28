'use strict';

const TOKEN_KEY = 'pst_dashboard_token';

const state = {
  token: localStorage.getItem(TOKEN_KEY) || '',
  cfg: null,
  summary: null,
  hosts: [],
  host: null,
  hostFilter: 'all',
  search: '',
  risk: 'all',
  status: 'all',
  timer: null,
};

function $(id) { return document.getElementById(id); }

function esc(v) {
  return String(v == null ? '' : v)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

function pad(n) { return String(n).padStart(2, '0'); }
function fmtTime(ts) {
  if (!ts) return '—';
  const d = new Date(Number(ts));
  if (Number.isNaN(d.getTime())) return '—';
  return `${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
function fmtFull(ts) {
  if (!ts) return '—';
  const d = new Date(Number(ts));
  if (Number.isNaN(d.getTime())) return '—';
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}
function relTime(ts) {
  if (!ts) return '—';
  const diff = Date.now() - Number(ts);
  if (diff < 0) return '刚刚';
  const s = Math.floor(diff / 1000);
  if (s < 60) return s + ' 秒前';
  const m = Math.floor(s / 60);
  if (m < 60) return m + ' 分钟前';
  const h = Math.floor(m / 60);
  if (h < 24) return h + ' 小时前';
  return Math.floor(h / 24) + ' 天前';
}

function riskLabel(level) {
  return level === 'high' ? '高危' : level === 'medium' ? '中危' : '低危';
}
function riskBadge(level, score) {
  const lv = level === 'high' || level === 'medium' ? level : 'low';
  return `<span class="risk ${lv}">${riskLabel(lv)}${score == null ? '' : ' ' + esc(score)}</span>`;
}
function statusChip(online) {
  return online
    ? '<span class="dot on"></span>在线'
    : '<span class="dot off"></span>离线';
}
function hostInitial(name) {
  const s = String(name || 'PC').replace(/[^A-Za-z0-9\u4e00-\u9fa5]/g, '');
  return esc((s.slice(0, 2) || 'PC').toUpperCase());
}

async function api(path) {
  const headers = {};
  if (state.token) headers['X-Dashboard-Token'] = state.token;
  const res = await fetch(path, { headers, cache: 'no-store' });
  if (res.status === 401) {
    showLogin('访问令牌已失效，请重新输入。');
    throw new Error('unauthorized');
  }
  if (!res.ok) throw new Error('HTTP ' + res.status);
  return res.json();
}

function tickClock() {
  const d = new Date();
  const el = $('clock');
  if (el) el.textContent = `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

function bindEvents() {
  $('refreshBtn').addEventListener('click', async () => {
    const b = $('refreshBtn');
    b.classList.add('spin');
    try { await refresh(); } finally { setTimeout(() => b.classList.remove('spin'), 500); }
  });
  $('search').addEventListener('input', (e) => { state.search = e.target.value.trim().toLowerCase(); renderHosts(); });
  $('riskFilter').addEventListener('change', (e) => { state.risk = e.target.value; renderHosts(); });
  $('statusFilter').addEventListener('change', (e) => { state.status = e.target.value; renderHosts(); });
  $('backBtn').addEventListener('click', () => { location.hash = '#/'; });
  $('hostBody').addEventListener('click', (e) => {
    const tr = e.target.closest('tr[data-id]');
    if (tr) location.hash = '#/host/' + encodeURIComponent(tr.dataset.id);
  });
  $('recent').addEventListener('click', (e) => {
    const row = e.target.closest('[data-id]');
    if (row) location.hash = '#/host/' + encodeURIComponent(row.dataset.id);
  });
  $('portFilter').addEventListener('click', (e) => {
    const btn = e.target.closest('button[data-f]');
    if (!btn) return;
    state.hostFilter = btn.dataset.f;
    document.querySelectorAll('#portFilter button').forEach((b) => b.classList.toggle('active', b === btn));
    if (state.host) renderPorts(state.host.report);
  });
  $('loginBtn').addEventListener('click', tryLogin);
  $('loginToken').addEventListener('keydown', (e) => { if (e.key === 'Enter') tryLogin(); });
  window.addEventListener('hashchange', route);
}

async function tryLogin() {
  const input = $('loginToken');
  const token = input.value.trim();
  if (!token) { $('loginErr').textContent = '请输入令牌'; return; }
  state.token = token;
  try {
    await api('/api/summary');
    localStorage.setItem(TOKEN_KEY, token);
    hideLogin();
    await refresh();
  } catch (e) {
    state.token = '';
    $('loginErr').textContent = '令牌不正确或服务端不可达';
  }
}

function showLogin(msg) {
  $('loginMask').hidden = false;
  if (msg) $('loginErr').textContent = msg;
  setTimeout(() => $('loginToken').focus(), 50);
}
function hideLogin() { $('loginMask').hidden = true; }

async function init() {
  bindEvents();
  tickClock();
  setInterval(tickClock, 1000);
  try {
    state.cfg = await fetch('/api/config', { cache: 'no-store' }).then((r) => r.json());
    if (state.cfg.authRequired && !state.token) { showLogin(''); return; }
    await refresh();
    state.timer = setInterval(() => { if (!document.hidden) refresh(true); }, 15000);
  } catch (e) {
    renderFatal(e);
  }
}

async function refresh(silent) {
  const hash = location.hash || '#/';
  if (hash.startsWith('#/host/')) {
    const id = decodeURIComponent(hash.slice(7));
    await loadHost(id, silent);
  } else {
    await loadAll(silent);
  }
}

async function loadAll(silent) {
  try {
    const [summary, hosts] = await Promise.all([api('/api/summary'), api('/api/hosts')]);
    state.summary = summary;
    state.hosts = hosts.hosts || [];
    renderSummary();
    renderHosts();
    showSummaryView();
  } catch (e) {
    if (!silent) renderFatal(e);
  }
}

async function loadHost(id, silent) {
  try {
    const data = await api('/api/hosts/' + encodeURIComponent(id));
    state.host = data;
    renderHost(data);
    showHostView();
  } catch (e) {
    if (!silent) {
      if (String(e.message).includes('404')) location.hash = '#/';
      else renderFatal(e);
    }
  }
}

function showSummaryView() {
  $('summaryView').hidden = false;
  $('hostView').hidden = true;
}
function showHostView() {
  $('summaryView').hidden = true;
  $('hostView').hidden = false;
}

function renderFatal(err) {
  const body = $('hostBody');
  if (body) body.innerHTML = `<tr><td colspan="8"><div class="empty">加载失败：${esc(err.message || err)}</div></td></tr>`;
}

function renderSummary() {
  const s = state.summary;
  if (!s) return;
  const t = s.totals;

  $('chipOnline').textContent = t.online;
  $('chipOffline').textContent = t.offline;
  $('chipHigh').textContent = s.risk.high;
  $('lastSeen').textContent = s.lastSeen ? fmtTime(s.lastSeen) : '—';

  const kpis = [
    ['主机总数', t.hosts, '已纳管终端', ''],
    ['在线主机', t.online, `离线 ${t.offline} 台`, 'ok'],
    ['高危主机', s.risk.high, `中危 ${s.risk.medium} · 低危 ${s.risk.low}`, 'hot'],
    ['暴露端口', t.exposedPorts, '当前所有主机暴露总数', 'warn'],
    ['严重暴露', t.criticalExposed, '高危端口暴露实例', 'hot'],
    ['平均风险分', t.avgScore, `累计上报 ${t.reports} 次`, 'cyan'],
  ];
  $('kpis').innerHTML = kpis.map(([label, value, sub, cls]) => `
    <div class="kpi ${cls}">
      <div class="kpi-label">${esc(label)}</div>
      <div class="kpi-value">${esc(value)}</div>
      <div class="kpi-sub">${esc(sub)}</div>
    </div>`).join('');

  renderRisk(s.risk);
  renderTrend(s.trend || []);
  renderTopPorts(s.topPorts || []);
  renderRecent(s.recent || []);
}

function renderRisk(risk) {
  const high = Number(risk.high) || 0;
  const medium = Number(risk.medium) || 0;
  const low = Number(risk.low) || 0;
  const total = high + medium + low;
  $('riskTotalTag').textContent = total + ' 台';

  let grad = 'conic-gradient(#2a4760 0% 100%)';
  if (total > 0) {
    const h = high / total * 100;
    const m = (high + medium) / total * 100;
    grad = `conic-gradient(var(--red) 0% ${h}%, var(--amber) ${h}% ${m}%, var(--green) ${m}% 100%)`;
  }

  $('riskChart').innerHTML = `
    <div class="donut-wrap">
      <div class="donut" style="background:${grad}"></div>
      <div class="donut-center"><b>${total}</b><span>台主机</span></div>
    </div>
    <div class="legend">
      <div class="legend-row"><i class="legend-dot" style="background:var(--red)"></i>高危 <span>存在严重暴露</span><b>${high}</b></div>
      <div class="legend-row"><i class="legend-dot" style="background:var(--amber)"></i>中危 <span>暴露面偏高</span><b>${medium}</b></div>
      <div class="legend-row"><i class="legend-dot" style="background:var(--green)"></i>低危 <span>封堵良好</span><b>${low}</b></div>
    </div>`;
}

function renderTrend(trend) {
  const total = trend.reduce((n, b) => n + Number(b.count || 0), 0);
  $('trendTotalTag').textContent = total + ' 次';

  const W = 640, H = 196;
  const L = 32, R = 12, T = 14, B = 28;
  const iw = W - L - R, ih = H - T - B;
  const max = Math.max(1, ...trend.map((b) => Number(b.count) || 0));
  const pts = trend.map((b, i) => {
    const x = L + (trend.length <= 1 ? 0 : i * iw / (trend.length - 1));
    const y = T + ih - (Number(b.count) || 0) / max * ih;
    return [x, y, b];
  });
  const line = pts.map(([x, y]) => `${x.toFixed(1)},${y.toFixed(1)}`).join(' ');
  const area = `M ${L},${T + ih} L ${pts.map(([x, y]) => `${x.toFixed(1)},${y.toFixed(1)}`).join(' L ')} L ${L + iw},${T + ih} Z`;
  const grid = [0, 0.5, 1].map((f) => {
    const y = T + ih - f * ih;
    return `<line x1="${L}" y1="${y}" x2="${L + iw}" y2="${y}" stroke="rgba(86,140,180,.12)" stroke-width="1"/>
            <text x="${L - 6}" y="${y + 3}" fill="#45647f" font-size="9" text-anchor="end">${Math.round(max * f)}</text>`;
  }).join('');
  const labels = pts.filter((_, i) => i % 4 === 0).map(([x, , b]) => {
    const d = new Date(Number(b.t));
    return `<text x="${x}" y="${H - 10}" fill="#45647f" font-size="9" text-anchor="middle">${pad(d.getHours())}:00</text>`;
  }).join('');

  $('trendChart').innerHTML = `
    <svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none">
      <defs>
        <linearGradient id="trendFill" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stop-color="#22d3ee" stop-opacity=".28"/>
          <stop offset="1" stop-color="#22d3ee" stop-opacity="0"/>
        </linearGradient>
      </defs>
      ${grid}
      <path d="${area}" fill="url(#trendFill)"/>
      <polyline points="${line}" fill="none" stroke="#22d3ee" stroke-width="1.8" stroke-linejoin="round" stroke-linecap="round"/>
      ${pts.filter((_, i) => i === pts.length - 1).map(([x, y]) => `<circle cx="${x}" cy="${y}" r="3" fill="#22d3ee"/>`).join('')}
      ${labels}
    </svg>
    <div class="trend-legend"><span><i style="background:#22d3ee"></i>上报次数</span><span>峰值 ${max}/小时</span></div>`;
}

function renderTopPorts(ports) {
  if (!ports.length) {
    $('topPorts').innerHTML = '<div class="empty">当前没有暴露端口</div>';
    return;
  }
  const max = Math.max(1, ...ports.map((p) => Number(p.hosts) || 0));
  $('topPorts').innerHTML = ports.map((p) => `
    <div class="port-row">
      <div class="port-name">${esc(p.port)}<em>${esc(p.service)}</em></div>
      <div class="port-bar"><i class="${p.critical ? '' : 'safe'}" style="width:${Math.max(6, (Number(p.hosts) || 0) / max * 100)}%"></i></div>
      <div class="port-count">${esc(p.hosts)} 台</div>
    </div>`).join('');
}

function renderRecent(rows) {
  if (!rows.length) {
    $('recent').innerHTML = '<div class="empty">暂无上报数据</div>';
    return;
  }
  $('recent').innerHTML = rows.map((r) => `
    <div class="recent-row" data-id="${esc(r.device_id)}">
      <div class="recent-host">${esc(r.computer_name)}<small>${esc(r.report_id || '')}</small></div>
      <div>${riskBadge(r.risk_level, r.risk_score)}</div>
      <div class="recent-time">${esc(relTime(r.collected_ts))}</div>
    </div>`).join('');
}

function filteredHosts() {
  return state.hosts.filter((h) => {
    if (state.risk !== 'all' && h.riskLevel !== state.risk) return false;
    if (state.status === 'online' && !h.online) return false;
    if (state.status === 'offline' && h.online) return false;
    if (state.search) {
      const hay = [h.computerName, h.primaryIp, h.primaryMac, h.currentUser, h.domain, h.serialNumber, h.osCaption]
        .join(' ').toLowerCase();
      if (!hay.includes(state.search)) return false;
    }
    return true;
  });
}

function renderHosts() {
  const rows = filteredHosts();
  $('hostCount').textContent = `共 ${rows.length} 台` + (rows.length !== state.hosts.length ? ` / ${state.hosts.length}` : '');
  if (!rows.length) {
    $('hostBody').innerHTML = '<tr><td colspan="8"><div class="empty">没有匹配的主机</div></td></tr>';
    return;
  }
  $('hostBody').innerHTML = rows.map((h) => `
    <tr class="clickable" data-id="${esc(h.deviceId)}">
      <td>
        <div class="host-cell">
          <div class="mini-avatar">${hostInitial(h.computerName)}</div>
          <div><b>${esc(h.computerName)}</b><small>${esc(h.domain || '')}${h.currentUser ? ' · ' + esc(h.currentUser) : ''}</small></div>
        </div>
      </td>
      <td class="mono">${esc(h.primaryIp || '—')}</td>
      <td>${statusChip(h.online)}</td>
      <td>${riskBadge(h.riskLevel, h.riskScore)}</td>
      <td class="num" style="${h.portsExposed > 0 ? 'color:#ff8fa2;font-weight:600' : ''}">${esc(h.portsExposed)}</td>
      <td class="num">${esc(h.portsBlocked)}</td>
      <td>${esc(h.osCaption || '—')}</td>
      <td title="${esc(fmtFull(h.lastSeen))}">${esc(relTime(h.lastSeen))}</td>
    </tr>`).join('');
}

function renderHost(data) {
  const d = data.device;
  const r = data.report || {};
  const device = r.device || {};
  const ports = r.ports || {};
  const items = Array.isArray(ports.items) ? ports.items : [];

  $('hostAvatar').textContent = hostInitial(d.computerName);
  $('hostName').textContent = d.computerName || '未知主机';
  $('hostMeta').innerHTML = `
    <span class="mono">${esc(d.primaryIp || '—')}</span>
    <span>${esc(d.currentUser || '—')}${d.domain ? '@' + esc(d.domain) : ''}</span>
    <span>${esc(d.osCaption || '—')}</span>
    <span>最后上报 ${esc(relTime(d.lastSeen))}</span>`;
  $('hostBadges').innerHTML = `
    <div class="big-risk ${d.riskLevel || 'low'}">${esc(d.riskScore || 0)}<small style="font-size:11px;color:#5f88a8"> 分</small></div>
    ${riskBadge(d.riskLevel, null)}
    <span class="tag ${d.online ? 'green' : ''}">${d.online ? '在线' : '离线'}</span>`;

  const kpis = [
    ['暴露端口', d.portsExposed, '当前未封堵', d.portsExposed > 0 ? 'hot' : 'ok'],
    ['严重暴露', d.criticalCount, '高危端口实例', d.criticalCount > 0 ? 'hot' : 'ok'],
    ['已封堵', d.portsBlocked, `策略总数 ${d.portsTotal}`, 'ok'],
    ['上报次数', d.reportCount, '历史快照数量', 'cyan'],
    ['内存', d.memoryGb ? Number(d.memoryGb).toFixed(0) : '—', 'GB', ''],
    ['风险分', d.riskScore, riskLabel(d.riskLevel), d.riskLevel === 'high' ? 'hot' : d.riskLevel === 'medium' ? 'warn' : 'ok'],
  ];
  $('hostKpis').innerHTML = kpis.map(([label, value, sub, cls]) => `
    <div class="kpi ${cls}">
      <div class="kpi-label">${esc(label)}</div>
      <div class="kpi-value">${esc(value)}${label === '内存' && value !== '—' ? '<small>GB</small>' : ''}</div>
      <div class="kpi-sub">${esc(sub)}</div>
    </div>`).join('');

  renderPorts(r);
  renderInfo(d, device, r);
  renderHistory(data.history || []);
}

function renderPorts(report) {
  const items = report && report.ports && Array.isArray(report.ports.items) ? report.ports.items : [];
  const filtered = items.filter((p) => {
    if (state.hostFilter === 'exposed') return p.state === 'exposed';
    if (state.hostFilter === 'critical') return p.critical && p.state === 'exposed';
    return true;
  });
  if (!filtered.length) {
    $('portGrid').innerHTML = '<div class="empty" style="grid-column:1/-1">没有匹配的端口</div>';
    return;
  }
  $('portGrid').innerHTML = filtered.map((p) => {
    const cls = ['port', p.state === 'exposed' ? 'exposed' : 'blocked',
      (p.critical && p.state === 'exposed') ? 'critical' : ''].join(' ');
    return `<div class="${cls}" title="${esc(p.port + ' ' + (p.service || '') + ' · ' + (p.state === 'exposed' ? '暴露' : '已封堵'))}">
      <b>${esc(p.port)}</b><span>${esc(p.service || '-')}</span>
    </div>`;
  }).join('');
}

function renderInfo(d, device, report) {
  const board = device.board || {};
  const bios = device.bios || {};
  const cpu = device.cpu || {};
  const memory = device.memory || {};
  const rows = [
    ['设备 ID', d.deviceId, true],
    ['计算机名', d.computerName],
    ['当前用户', d.currentUser || '—'],
    ['域', d.domain || '—'],
    ['序列号', d.serialNumber || '—', true],
    ['操作系统', d.osCaption || '—'],
    ['系统版本', d.osVersion || '—', true],
    ['CPU', cpu.model || d.cpuModel || '—'],
    ['内存', (memory.totalGB || d.memoryGb || '—') + ' GB'],
    ['主板', [board.manufacturer, board.product].filter(Boolean).join(' ') || '—'],
    ['BIOS', [bios.version, bios.releaseDate].filter(Boolean).join(' / ') || '—'],
    ['主 MAC', d.primaryMac || '—', true],
    ['客户端版本', (report.agent && report.agent.version) || d.agentVersion || '—'],
    ['首次上报', fmtFull(d.firstSeen)],
    ['最后上报', fmtFull(d.lastSeen)],
    ['采集时间', report.collectedAtText || report.collectedAt || '—'],
  ];
  $('hostInfo').innerHTML = rows.map(([label, value, mono]) => `
    <div class="info-row"><label>${esc(label)}</label><div class="${mono ? 'mono' : ''}" title="${esc(value)}">${esc(value)}</div></div>`).join('');
  $('hostReportTag').textContent = report.reportId ? '快照 ' + String(report.reportId).slice(0, 8) : '—';
}

function renderHistory(history) {
  $('historyTag').textContent = history.length + ' 条';
  if (!history.length) {
    $('historyChart').innerHTML = '<div class="empty">暂无历史记录</div>';
    $('historyBody').innerHTML = '<tr><td colspan="7"><div class="empty">暂无历史记录</div></td></tr>';
    return;
  }

  const W = 640, H = 150, L = 30, R = 12, T = 12, B = 20;
  const iw = W - L - R, ih = H - T - B;
  const n = history.length;
  const pts = history.map((h, i) => {
    const x = L + (n <= 1 ? iw / 2 : i * iw / (n - 1));
    const y = T + ih - (Number(h.risk_score) || 0) / 100 * ih;
    return [x, y];
  });
  const line = pts.map(([x, y]) => `${x.toFixed(1)},${y.toFixed(1)}`).join(' ');
  const area = `M ${L},${T + ih} L ${pts.map(([x, y]) => `${x.toFixed(1)},${y.toFixed(1)}`).join(' L ')} L ${L + iw},${T + ih} Z`;
  const grid = [0, 50, 100].map((v) => {
    const y = T + ih - v / 100 * ih;
    return `<line x1="${L}" y1="${y}" x2="${L + iw}" y2="${y}" stroke="rgba(86,140,180,.12)"/>
            <text x="${L - 6}" y="${y + 3}" fill="#45647f" font-size="9" text-anchor="end">${v}</text>`;
  }).join('');
  $('historyChart').innerHTML = `
    <svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none">
      <defs><linearGradient id="histFill" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="#f43f5e" stop-opacity=".20"/>
        <stop offset="1" stop-color="#f43f5e" stop-opacity="0"/>
      </linearGradient></defs>
      ${grid}
      <path d="${area}" fill="url(#histFill)"/>
      <polyline points="${line}" fill="none" stroke="#fb7185" stroke-width="1.6" stroke-linejoin="round"/>
    </svg>`;

  $('historyBody').innerHTML = history.slice().reverse().map((h) => `
    <tr>
      <td>${esc(fmtFull(h.collected_ts))}</td>
      <td class="num">${esc(h.risk_score)}</td>
      <td>${riskBadge(h.risk_level, null)}</td>
      <td class="num" style="${h.ports_exposed > 0 ? 'color:#ff8fa2' : ''}">${esc(h.ports_exposed)}</td>
      <td class="num">${esc(h.ports_blocked)}</td>
      <td class="num">${esc(h.critical_count)}</td>
      <td class="mono">${esc(String(h.report_id || '').slice(0, 12))}</td>
    </tr>`).join('');
}

function route() {
  const hash = location.hash || '#/';
  if (hash.startsWith('#/host/')) {
    const id = decodeURIComponent(hash.slice(7));
    if (id) loadHost(id);
  } else {
    loadAll();
  }
}

document.addEventListener('DOMContentLoaded', init);
