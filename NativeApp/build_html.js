// 将原 HTA 转换为原生 WebBrowser 内嵌 HTML
// 仅添加兼容层，界面与业务逻辑不改
const fs = require('fs');
const path = require('path');

// 路径相对仓库根目录解析，换机器/换目录无需改代码（可用 HTA_SRC 环境变量覆盖）
const ROOT = path.resolve(__dirname, '..');
const src = process.env.HTA_SRC || path.join(ROOT, '网络信息采集和端口管理工具_v1.0_GUI.hta');
const dst = path.join(__dirname, 'ui.html');

let html = fs.readFileSync(src, 'utf8').replace(/^\uFEFF/, '');

// 1) 移除 HTA:APPLICATION 标签（原生 Form 负责窗口）
html = html.replace(/<HTA:APPLICATION[\s\S]*?\/>/i, '');

// 2) 注入兼容层：ActiveXObject -> window.external，窗口控制 -> 原生方法
// 注意：WebBrowser 中 window.close / window.minimize 是宿主只读方法，
// 直接覆盖赋值会静默失败；窗口按钮的 onclick 在下方被改写为直接调用 window.external。
const shim = `
// ====== 原生宿主兼容层（自动生成，请勿手改）======
// 说明：.NET 的 COM 自动派发不支持同名重载、也不容忍参数个数不一致，
// 因此这里用适配器统一补齐参数个数，业务脚本无需改动。
var __extRef = null;
function __ext(){ if (!__extRef) __extRef = window.external; return __extRef; }
var __host = {
  // ---- WScript.Shell ----
  Run: function(cmd, style, wait){
    return __ext().Run(cmd, style === undefined ? 0 : style, wait === undefined ? true : wait);
  },
  Exec: function(cmd){ return __ext().Exec(cmd); },
  Capture: function(cmd){ return __ext().Capture(cmd); },
  ExpandEnvironmentStrings: function(s){ return __ext().ExpandEnvironmentStrings(s); },
  OpenNotepad: function(p){ return __ext().OpenNotepad(p); },
  IsAdmin: function(){ return __ext().IsAdmin(); },
  // ---- 原生加速接口（无进程启动）----
  OsCaption: function(){ return __ext().OsCaption(); },
  FwState: function(){ return __ext().FwState(); },
  RuleIPs: function(n){ return __ext().RuleIPs(n); },
  BlockedPorts: function(){ return __ext().BlockedPorts(); },
  InvalidateRules: function(){ return __ext().InvalidateRules(); },
  ProcName: function(pid){ return __ext().ProcName(pid); },
  Listeners: function(){ return __ext().Listeners(); },
  StartDrag: function(){ return __ext().StartDrag(); },
  BuildInfo: function(){ return __ext().BuildInfo(); },
  // ---- 数据采集 / 程序设置 ----
  GetSettings: function(){ return __ext().GetSettings(); },
  SetAutoStart: function(on){ return __ext().SetAutoStart(!!on); },
  SetTrayMode: function(on){ return __ext().SetTrayMode(!!on); },
  SaveCollectConfig: function(ip, share, user, pass, iv, auto){
    return __ext().SaveCollectConfig(ip, share, user, pass, iv, !!auto);
  },
  CollectPreview: function(){ return __ext().CollectPreview(); },
  CollectLocal: function(){ return __ext().CollectLocal(); },
  CollectAndSend: function(){ return __ext().CollectAndSend(); },
  OpenLastReport: function(){ return __ext().OpenLastReport(); },
  OpenTargetFolder: function(){ return __ext().OpenTargetFolder(); },
  OpenConfigDir: function(){ return __ext().OpenConfigDir(); },
  // ---- 数据接口 ----
  GetApiSettings: function(){ return __ext().GetApiSettings(); },
  SaveApiConfig: function(url, token, to, push, local, port, ltoken){
    return __ext().SaveApiConfig(url === undefined ? "" : url,
                                 token === undefined ? "" : token,
                                 to === undefined ? 8 : to,
                                 !!push, !!local,
                                 port === undefined ? 18760 : port,
                                 ltoken === undefined ? "" : ltoken);
  },
  ApiTest: function(url, token){ return __ext().ApiTest(url || "", token || ""); },
  ApiPushNow: function(){ return __ext().ApiPushNow(); },
  ApiFlushOutbox: function(){ return __ext().ApiFlushOutbox(); },
  ApiServerState: function(){ return __ext().ApiServerState(); },
  ExportApiDoc: function(){ return __ext().ExportApiDoc(); },
  CopyText: function(t){ return __ext().CopyText(t); },
  OpenUrl: function(u){ return __ext().OpenUrl(u); },
  // ---- 远程桌面（SubnetDesk）----
  OpenSubnetDesk: function(target){ return __ext().OpenSubnetDesk(target || ""); },
  CloseSubnetDesk: function(){ return __ext().CloseSubnetDesk(); },
  // ---- Scripting.FileSystemObject ----
  GetSpecialFolder: function(n){ return __ext().GetSpecialFolder(n); },
  GetTempName: function(){ return __ext().GetTempName(); },
  FileExists: function(p){ return __ext().FileExists(p); },
  DeleteFile: function(p, f){ return __ext().DeleteFile(p, f === undefined ? true : f); },
  OpenTextFile: function(p, m, c, f){
    return __ext().OpenTextFile(p, m, c === undefined ? false : c, f === undefined ? 0 : f);
  },
  // ---- Shell.Application ----
  ShellExecute: function(file, args, verb, show){
    return __ext().ShellExecute(file, args, verb === undefined ? "open" : verb, show === undefined ? 1 : show);
  }
};
var ActiveXObject = function (name) { return __host; };
(function () {
  try {
    window.resizeTo = function (w, h) { window.external.ResizeWindow(w, h); };
    window.moveTo   = function (x, y) { window.external.MoveWindow(x, y); };
  } catch (e) {}
})();
// ===================================================
`;

const tag = '<script language="JScript">';
if (!html.includes(tag)) throw new Error('未找到 script 标签');
html = html.replace(tag, tag + '\n' + shim);

// 3) 窗口按钮：直接调用原生桥接层，绕过只读的 window.close/minimize
html = html.replace(/onclick="window\.minimize\(\)"/g, 'onclick="window.external.Minimize()"');
html = html.replace(/onclick="window\.close\(\)"/g, 'onclick="window.external.Close()"');
console.log('window buttons rewritten to window.external');

// 4) 替换 elevate()：原生 exe 直接以 runas 重启自身
// 4) 替换 elevate()：原生 exe 直接以 runas 重启自身（用正则，避免行尾差异导致漏替换）
const newElevate = `function elevate(){
  try { window.external.Elevate(); }
  catch(e){ log("提权失败: " + e.message, "err"); }
}`;

const reElevate = /function elevate\(\)\{[\s\S]*?\r?\n\}/;
if (reElevate.test(html)) {
  html = html.replace(reElevate, newElevate);
  console.log('elevate replaced');
} else {
  console.warn('WARN: elevate block not matched');
}

fs.writeFileSync(dst, '\uFEFF' + html, 'utf8');
console.log('written', dst, fs.statSync(dst).size, 'bytes');
