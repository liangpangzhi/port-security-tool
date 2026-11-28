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
  settings: null,
  agents: [],
  pollStatus: null,
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

async function api(path, options = {}) {
  const headers = Object.assign({}, options.headers || {});
  if (state.token) headers['X-Dashboard-Token'] = state.token;
  if (options.body && !headers['Content-Type']) headers['Content-Type'] = 'application/json';

  const res = await fetch(path, Object.assign({}, options, { headers, cache: 'no-store' }));
  if (res.status === 401) {
    showLogin('访问令牌已失效，请重新输入。');
    throw new Error('unauthorized');
  }
  if (!res.ok) {
    let data = null;
    try { data = await res.json(); } catch { }
    const msg = data && (data.error || (Array.isArray(data.errors) ? data.errors.join('；') : ''));
    const err = new Error(msg || ('HTTP ' + res.status));
    err.status = res.status;
    err.data = data;
    throw err;
  }
  if (res.status === 204) return null;
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
  $('settingsBtn').addEventListener('click', () => { location.hash = '#/settings'; });
  $('agentsBtn').addEventListener('click', () => { location.hash = '#/agents'; });
  $('homeBtn').addEventListener('click', () => { location.hash = '#/'; });
  $('saveSettingsBtn').addEventListener('click', saveSettings);
  $('scanBtn').addEventListener('click', () => runAgentAction('scan'));
  $('pollBtn').addEventListener('click', () => runAgentAction('poll'));
  $('scanPollBtn').addEventListener('click', () => runAgentAction('scan-and-poll'));
  $('saveAgentCfgBtn').addEventListener('click', saveAgentSettings);
  $('agentBody').addEventListener('click', (e) => {
    const btn = e.target.closest('button[data-poll-ip]');
    if (!btn) return;
    pollSingleAgent(btn.dataset.pollIp, btn.dataset.pollPort || '18762');
  });
  $('copyDashboardUrl').addEventListener('click', () => {
    copyText($('setDashboardUrl').textContent, '大屏地址已复制');
  });
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
  updateHomeButton(location.hash || '#/');
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
  if (hash.startsWith('#/settings')) {
    if (!silent) await loadSettings();
    return;
  }
  if (hash.startsWith('#/agents')) {
    await loadAgents(silent);
    return;
  }
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
  $('agentsView').hidden = true;
  $('settingsView').hidden = true;
}
function showHostView() {
  $('summaryView').hidden = true;
  $('hostView').hidden = false;
  $('agentsView').hidden = true;
  $('settingsView').hidden = true;
}
function showSettingsView() {
  $('summaryView').hidden = true;
  $('hostView').hidden = true;
  $('agentsView').hidden = true;
  $('settingsView').hidden = false;
}
function showAgentsView() {
  $('summaryView').hidden = true;
  $('hostView').hidden = true;
  $('settingsView').hidden = true;
  $('agentsView').hidden = false;
}

async function loadAgents(silent) {
  try {
    const [data, status, settings] = await Promise.all([
      api('/api/agents'),
      api('/api/poll/status'),
      api('/api/settings'),
    ]);
    state.agents = data.agents || [];
    state.pollStatus = status;
    state.settings = settings;
    renderAgents();
    showAgentsView();
  } catch (e) {
    if (!silent) {
      $('agentMsg').className = 'form-msg err';
      $('agentMsg').textContent = '加载客户端列表失败：' + e.message;
    }
    showAgentsView();
  }
}

function renderAgents() {
  const agents = state.agents;
  const alive = agents.filter((a) => a.alive).length;
  const ok = agents.filter((a) => a.lastPollOk === true).length;
  const bad = agents.filter((a) => a.lastPollOk === false).length;
  const st = state.pollStatus || {};
  const cfg = state.settings || {};
  $('agentCount').textContent = `共 ${agents.length} 台`;
  $('agentPortHint').textContent = '发现端口 UDP ' + (st.discoveryPort || '—');
  $('agentScheduleTag').textContent = st.enabled
    ? `自动拉取 · 每 ${st.intervalMinutes} 分钟`
    : '自动拉取未启用';
  $('agentLastPoll').textContent = st.lastRunAt ? fmtFull(st.lastRunAt) : '—';

  // 模块内置配置项：定时拉取、间隔、发现端口、客户端拉取令牌
  $('setPollEnabled').checked = !!cfg.pollEnabled;
  $('setPollInterval').value = cfg.pollIntervalMinutes || '';
  $('setDiscoveryPort').value = cfg.discoveryPort || '';
  $('setAgentToken').value = '';
  $('clearAgentToken').checked = false;
  $('agentTokenState').textContent = cfg.agentTokenSet
    ? '当前已配置客户端拉取令牌（不显示原文），留空即保持不变；需与客户端「访问令牌」一致。'
    : '当前未配置客户端拉取令牌，客户端可不带令牌被拉取；建议设置。';

  $('agentKpis').innerHTML = [
    ['已发现客户端', agents.length, '局域网代理在线', ''],
    ['当前在线', alive, '10 分钟内被扫描到', 'ok'],
    ['拉取成功', ok, '最近一次拉取', 'cyan'],
    ['拉取失败', bad, '请检查令牌 / 防火墙', bad ? 'hot' : ''],
    ['发现端口', st.discoveryPort || '—', 'UDP 广播', ''],
    ['自动拉取', st.enabled ? '开启' : '关闭', st.enabled ? `${st.intervalMinutes} 分钟 / 次` : '可在本页开启', st.enabled ? 'ok' : 'warn'],
  ].map(([label, value, sub, cls]) => `
    <div class="kpi ${cls}">
      <div class="kpi-label">${esc(label)}</div>
      <div class="kpi-value">${esc(value)}</div>
      <div class="kpi-sub">${esc(sub)}</div>
    </div>`).join('');

  if (!agents.length) {
    $('agentBody').innerHTML = '<tr><td colspan="8"><div class="empty">尚未发现客户端。请确认客户端已开启「局域网代理」并放行防火墙，然后点击「一键扫描」。</div></td></tr>';
    return;
  }
  $('agentBody').innerHTML = agents.map((a) => `
    <tr>
      <td><div class="host-cell"><div class="mini-avatar">${hostInitial(a.computerName)}</div><div><b>${esc(a.computerName)}</b><small>${esc(a.currentUser || a.agentVersion || '')}</small></div></div></td>
      <td class="mono">${esc(a.ip)}</td>
      <td class="mono">${esc(a.port)}</td>
      <td class="mono" title="${esc(a.deviceId)}">${esc(String(a.deviceId || '—').slice(0, 16))}</td>
      <td>${a.tokenRequired ? '<span class="tag amber">需要</span>' : '<span class="tag">无需</span>'}</td>
      <td title="${esc(fmtFull(a.lastSeen))}">${esc(relTime(a.lastSeen))}</td>
      <td>${a.lastPollAt == null ? '<span class="tag">未拉取</span>'
        : (a.lastPollOk ? '<span class="tag green">成功</span>' : '<span class="tag red">失败</span>')
          + '<small style="display:block;color:#4d6d88;margin-top:3px" title="' + esc(a.lastPollMsg) + '">' + esc(a.lastPollMsg || '') + '</small>'}</td>
      <td class="num"><button class="small-btn" data-poll-ip="${esc(a.ip)}" data-poll-port="${esc(a.port)}">拉取</button></td>
    </tr>`).join('');
}

async function saveAgentSettings() {
  const btn = $('saveAgentCfgBtn');
  const msg = $('agentMsg');
  const payload = {
  };
  Object.keys(payload).forEach((k) => { if (payload[k] === undefined) delete payload[k]; });

  btn.disabled = true;
  msg.className = 'form-msg';
  msg.textContent = '正在保存…';
  try {
    await api('/api/settings', { method: 'POST', body: JSON.stringify(payload) });
    await loadAgents(true);
    msg.className = 'form-msg';
    msg.textContent = '发现与拉取配置已保存到 config.json。';
  } catch (e) {
    msg.className = 'form-msg err';
    msg.textContent = '保存失败：' + e.message;
  } finally {
    btn.disabled = false;
  }
}

async function runAgentAction(action) {
  const msg = $('agentMsg');
  msg.className = 'form-msg';
  msg.textContent = action === 'scan' ? '正在广播扫描…' : '正在拉取客户端数据…';
  const cidr = $('scanCidr').value.trim();
  const body = action === 'poll' ? null : { cidrs: cidr ? cidr.split(/[,，\s]+/).filter(Boolean) : [] };
  try {
    const result = await api('/api/agents/' + action, {
      method: 'POST',
      body: body ? JSON.stringify(body) : undefined,
    });
    state.agents = result.agents || state.agents;
    state.pollStatus = await api('/api/poll/status');
    renderAgents();
    let text = action === 'scan'
      ? `扫描完成，发现 ${result.count || 0} 台客户端。`
      : `拉取完成：共 ${result.total || 0} 台，成功 ${result.success || 0} 台，失败 ${result.failed || 0} 台。`;
    if (result.discovered != null) text += ` 新发现 ${result.discovered} 台。`;
    msg.className = 'form-msg';
    msg.textContent = text;
  } catch (e) {
    msg.className = 'form-msg err';
    msg.textContent = '操作失败：' + e.message;
  }
}

async function pollSingleAgent(ip, port) {
  const msg = $('agentMsg');
  msg.className = 'form-msg';
  msg.textContent = '正在拉取 ' + ip + ' …';
  try {
    const result = await api('/api/agents/poll', {
      method: 'POST',
      body: JSON.stringify({ only: ip, port: Number(port) }),
    });
    state.agents = result.agents || state.agents;
    renderAgents();
    msg.className = 'form-msg';
    msg.textContent = `拉取完成：成功 ${result.success || 0} 台，失败 ${result.failed || 0} 台。`;
  } catch (e) {
    msg.className = 'form-msg err';
    msg.textContent = '拉取失败：' + e.message;
  }
}

async function loadSettings(clearNotice = true) {
  try {
    const settings = await api('/api/settings');
    state.settings = settings;
    if (clearNotice) {
      $('settingsMsg').textContent = '';
      $('settingsMsg').classList.remove('err');
      $('settingsBanner').hidden = true;
    }
    renderSettings(settings);
    showSettingsView();
  } catch (e) {
    $('settingsMsg').className = 'form-msg err';
    $('settingsMsg').textContent = '加载配置失败：' + e.message;
    showSettingsView();
  }
}

function renderSettings(s) {
  $('setHost').value = s.host || '';
  $('setPort').value = s.port || '';
  $('setPublicBaseUrl').value = s.publicBaseUrl || '';
  $('setStaleMinutes').value = s.staleMinutes || '';
  $('setDashboardUrl').textContent = s.dashboardUrl || '—';
  $('settingsConfigShort').textContent = s.configPath || '—';
  $('dashboardTokenState').textContent = s.dashboardTokenSet
    ? '当前已启用（出于安全不显示原文），留空即保持不变。'
    : '当前未启用，局域网内可直接访问大屏。';
  $('setDashboardToken').value = '';
  $('clearDashboardToken').checked = false;

  const rows = [
    ['配置文件路径', s.configPath || '—', true],
    ['数据库文件', s.dbFile || '—', true],
    ['监听地址', (s.host || '') + ':' + (s.port || ''), true],
    ['大屏令牌', s.dashboardTokenSet ? '已启用' : '未启用'],
    ['离线阈值', (s.staleMinutes || '') + ' 分钟'],
  ];
  $('settingsInfo').innerHTML = rows.map(([label, value, mono]) => `
    <div class="info-row"><label>${esc(label)}</label><div class="${mono ? 'mono' : ''}" title="${esc(value)}">${esc(value)}</div></div>`).join('');
}

async function saveSettings() {
  const btn = $('saveSettingsBtn');
  const msg = $('settingsMsg');
  msg.className = 'form-msg';
  msg.textContent = '正在保存…';
  btn.disabled = true;

  const dashboardToken = $('setDashboardToken').value.trim();
  const clearDashboard = $('clearDashboardToken').checked;
  const payload = {
    host: $('setHost').value.trim(),
    port: Number($('setPort').value),
    publicBaseUrl: $('setPublicBaseUrl').value.trim(),
    staleMinutes: Number($('setStaleMinutes').value),
    dashboardToken,
    clearDashboardToken: clearDashboard,
  };
  Object.keys(payload).forEach((k) => { if (payload[k] === undefined) delete payload[k]; });

  try {
    const result = await api('/api/settings', {
      method: 'POST',
      body: JSON.stringify(payload),
    });
    if (clearDashboard) {
      state.token = '';
      localStorage.removeItem(TOKEN_KEY);
    } else if (dashboardToken) {
      state.token = dashboardToken;
      localStorage.setItem(TOKEN_KEY, dashboardToken);
    }
    await loadSettings(false);

    if (result.restartRequired) {
      $('settingsBanner').hidden = false;
      $('settingsBanner').textContent = '监听地址或端口已修改。当前服务仍在旧地址运行，请重启服务端后按新地址访问。';
    }
    msg.className = 'form-msg';
    msg.textContent = '配置已保存到 config.json' + (result.dashboardTokenChanged ? '，大屏令牌已更新。' : '。');
  } catch (e) {
    msg.className = 'form-msg err';
    msg.textContent = '保存失败：' + e.message;
  } finally {
    btn.disabled = false;
  }
}

async function copyText(text, okMessage) {
  const value = String(text || '').trim();
  if (!value || value === '—') return;
  try {
    await navigator.clipboard.writeText(value);
  } catch {
    const ta = document.createElement('textarea');
    ta.value = value;
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    try { document.execCommand('copy'); } catch { }
    document.body.removeChild(ta);
  }
  const msg = $('settingsMsg');
  msg.className = 'form-msg';
  msg.textContent = okMessage;
  setTimeout(() => { if (msg.textContent === okMessage) msg.textContent = ''; }, 1800);
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
    ['平均风险分', t.avgScore, `累计采集 ${t.reports} 次`, 'cyan'],
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
    <div class="trend-legend"><span><i style="background:#22d3ee"></i>拉取次数</span><span>峰值 ${max}/小时</span></div>`;
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
    $('recent').innerHTML = '<div class="empty">暂无采集数据</div>';
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
    <span>最后采集 ${esc(relTime(d.lastSeen))}</span>`;
  $('hostBadges').innerHTML = `
    <div class="big-risk ${d.riskLevel || 'low'}">${esc(d.riskScore || 0)}<small style="font-size:11px;color:#5f88a8"> 分</small></div>
    ${riskBadge(d.riskLevel, null)}
    <span class="tag ${d.online ? 'green' : ''}">${d.online ? '在线' : '离线'}</span>`;

  const kpis = [
    ['暴露端口', d.portsExposed, '当前未封堵', d.portsExposed > 0 ? 'hot' : 'ok'],
    ['严重暴露', d.criticalCount, '高危端口实例', d.criticalCount > 0 ? 'hot' : 'ok'],
    ['已封堵', d.portsBlocked, `策略总数 ${d.portsTotal}`, 'ok'],
    ['采集次数', d.reportCount, '历史快照数量', 'cyan'],
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
    ['首次采集', fmtFull(d.firstSeen)],
    ['最后采集', fmtFull(d.lastSeen)],
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

function updateHomeButton(hash) {
  const btn = $('homeBtn');
  if (!btn) return;
  const h = hash || '#/';
  btn.disabled = (h === '#/' || h === '#');
  btn.title = btn.disabled ? '已在首页' : '返回首页';
}

function route() {
  const hash = location.hash || '#/';
  updateHomeButton(hash);
  if (hash.startsWith('#/settings')) {
    loadSettings();
    return;
  }
  if (hash.startsWith('#/agents')) {
    loadAgents();
    return;
  }
  if (hash.startsWith('#/host/')) {
    const id = decodeURIComponent(hash.slice(7));
    if (id) loadHost(id);
  } else {
    loadAll();
  }
}

document.addEventListener('DOMContentLoaded', init);
