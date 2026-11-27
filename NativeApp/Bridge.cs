using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace PortSecurityTool
{
    // ============================================================
    //  COM 桥接层：让 HTA 中的 JScript 无需修改即可调用原生能力
    //  暴露为 window.external.*
    // ============================================================
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public partial class Bridge
    {
        private readonly MainForm _form;
        public Bridge(MainForm form) { _form = form; }

        // ---------- WScript.Shell 等价 ----------
        public int Run(string cmd, int windowStyle, bool waitOnReturn)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return -1;
                    if (waitOnReturn) { p.WaitForExit(); return p.ExitCode; }
                    return 0;
                }
            }
            catch { return -1; }
        }

        public string Capture(string cmd)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "sec_" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd + " > \"" + tmp + "\" 2>&1")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return "";
                    p.WaitForExit();
                }
                if (!File.Exists(tmp)) return "";
                string s = File.ReadAllText(tmp, Enc.Ansi);
                try { File.Delete(tmp); } catch { }
                return s;
            }
            catch { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } return ""; }
        }

        // WScript.Shell.Exec 等价：同步执行并返回带 StdOut/StdErr 的对象
        public ExecResult Exec(string cmd)
        {
            var r = new ExecResult();
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) { r.ExitCode = -1; return r; }
                    r._out = p.StandardOutput.ReadToEnd();
                    r._err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    r.ExitCode = p.ExitCode;
                }
            }
            catch { r.ExitCode = -1; }
            return r;
        }

        public string ExpandEnvironmentStrings(string s)
        {
            return string.IsNullOrEmpty(s) ? "" : Environment.ExpandEnvironmentVariables(s);
        }

        // ---------- Scripting.FileSystemObject 等价 ----------
        // 直接返回路径字符串：JS 里 `fso.GetSpecialFolder(2) + "\\xx"` 才能得到真实路径
        // （返回包装对象时，JScript 的字符串转换拿不到 Path，会拼出 "[object Object]\xx"）
        public string GetSpecialFolder(int n)
        {
            // 2 = TemporaryFolder
            return Path.GetTempPath().TrimEnd('\\');
        }

        public string GetTempName() { return Path.GetRandomFileName(); }
        public bool FileExists(string path) { try { return File.Exists(path); } catch { return false; } }

        // 原脚本里 OpenTextFile 有 3 参数和 4 参数两种写法。
        // 注意：.NET 的 AutoDispatch 不支持同名重载（会返回 DISP_E_BADPARAMCOUNT），
        // 所以这里只保留一个带默认值的签名，由 JS 侧适配器补齐参数。
        public TextStream OpenTextFile(string path, int mode, bool create = false, int format = 0)
        {
            return new TextStream(path, mode, create);
        }

        public void DeleteFile(string path, bool force)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ---------- Shell.Application 等价 ----------
        public void ShellExecute(string file, string args, string verb, int show)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = true,
                    Verb = string.IsNullOrEmpty(verb) ? "open" : verb,
                    WindowStyle = show == 0 ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
                };
                Process.Start(psi);
            }
            catch { }
        }

        public void OpenNotepad(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch { }
        }

        // ---------- 权限 / 提权 ----------
        public bool IsAdmin()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public void Elevate()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
            }
            catch { }
            Close();
        }

        // ---------- 版本 / 构建信息 ----------
        /// <summary>返回 "文件版本|.NET运行时|架构|构建时间|版权单位"，供「关于」页展示。</summary>
        public string BuildInfo()
        {
            string ver = "1.0.0.0", net = "-", arch = "-", built = "-", company = "-";
            try
            {
                var asm = typeof(Bridge).Assembly;
                var v = asm.GetName().Version;
                if (v != null) ver = v.ToString();
                net = Environment.Version.ToString();
                arch = Environment.Is64BitProcess ? "x64" : "x86";

                string bd = "", bt = "";
                foreach (var o in asm.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
                {
                    var m = o as System.Reflection.AssemblyMetadataAttribute;
                    if (m == null) continue;
                    if (m.Key == "BuildDate") bd = m.Value;
                    else if (m.Key == "BuildTime") bt = m.Value;
                }
                if (bd.Length > 0) built = (bt.Length > 0 ? bd + " " + bt : bd);

                foreach (var o in asm.GetCustomAttributes(typeof(System.Reflection.AssemblyCompanyAttribute), false))
                {
                    var c = o as System.Reflection.AssemblyCompanyAttribute;
                    if (c != null && !string.IsNullOrEmpty(c.Company)) company = c.Company;
                }
            }
            catch { }
            return ver + "|" + net + "|" + arch + "|" + built + "|" + company;
        }

        // ---------- 程序设置 / 数据采集 ----------
        /// <summary>所有设置一次性返回，字段用 | 分隔，界面拆包使用。</summary>
        public string GetSettings()
        {
            string pass = AppConfig.Get("collect.pass", "");
            var parts = new[]
            {
                SystemIntegration.AutoStartEnabled() ? "1" : "0",
                AppConfig.GetBool("ui.tray", true) ? "1" : "0",
                AppConfig.GetInt("collect.interval", 60).ToString(),
                AppConfig.Get("collect.ip", ""),
                AppConfig.Get("collect.share", ""),
                AppConfig.Get("collect.user", ""),
                pass.Length > 0 ? "1" : "0",
                AppConfig.GetBool("collect.auto", false) ? "1" : "0",
                AppConfig.Get("collect.lastTime", ""),
                AppConfig.Get("collect.lastResult", ""),
                Collector.NormalizeTarget(AppConfig.Get("collect.ip", ""), AppConfig.Get("collect.share", ""))
            };
            return string.Join("\u0001", parts);
        }

        public bool SetAutoStart(bool on)
        {
            bool ok = SystemIntegration.SetAutoStart(on);
            if (ok) AppConfig.SetBool("ui.autoStart", on);
            return ok;
        }

        public void SetTrayMode(bool on) { AppConfig.SetBool("ui.tray", on); }

        /// <summary>保存采集配置。pass 传空表示不修改，传 __CLEAR__ 表示清空密码。</summary>
        public string SaveCollectConfig(string ip, string share, string user, string pass, int interval, bool auto)
        {
            try
            {
                AppConfig.Set("collect.ip", (ip ?? "").Trim());
                AppConfig.Set("collect.share", (share ?? "").Trim());
                AppConfig.Set("collect.user", (user ?? "").Trim());
                if (pass == "__CLEAR__") AppConfig.Set("collect.pass", "");
                else if (!string.IsNullOrEmpty(pass)) AppConfig.Set("collect.pass", AppConfig.Protect(pass));
                if (interval < 1) interval = 1;
                if (interval > 10080) interval = 10080;
                AppConfig.SetInt("collect.interval", interval);
                AppConfig.SetBool("collect.auto", auto);
                // 定时采集要求程序常驻，开启时自动切到托盘模式，避免用户点关闭后采集停止
                if (auto) AppConfig.SetBool("ui.tray", true);
                _form.RestartAutoCollect();
                var target = Collector.NormalizeTarget(ip, share);
                return "OK|配置已保存。" + (auto ? "定时采集已开启，每 " + interval + " 分钟执行一次（已同时开启「关闭时最小化到托盘」）。" : "定时采集未开启。") +
                       (target.Length > 0 ? " 上报目标：" + target : " 尚未填写上报目标。");
            }
            catch (Exception ex) { return "ERR|保存失败：" + ex.Message; }
        }

        /// <summary>采集预览（不上报），行间 \u0001 分隔，字段间 \t 分隔。</summary>
        public string CollectPreview() { try { return Collector.PreviewText(); } catch (Exception ex) { return "计算机名\t采集失败：" + ex.Message; } }

        /// <summary>仅采集并在本地生成表格，返回 OK|路径。</summary>
        public string CollectLocal()
        {
            try { return "OK|" + Collector.BuildLocalOnly(); }
            catch (Exception ex) { return "ERR|" + ex.Message; }
        }

        /// <summary>立即采集并上报，返回 OK|消息|本地文件 或 ERR|消息|本地文件。</summary>
        public string CollectAndSend()
        {
            try
            {
                var r = Collector.CollectAndSend(
                    AppConfig.Get("collect.ip", ""),
                    AppConfig.Get("collect.share", ""),
                    AppConfig.Get("collect.user", ""),
                    AppConfig.Unprotect(AppConfig.Get("collect.pass", "")));

                AppConfig.Set("collect.lastTime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                AppConfig.Set("collect.lastResult", r.Message.Replace("\r", "").Replace("\n", " | "));
                return (r.Ok ? "OK|" : "ERR|") + r.Message.Replace("\r", "").Replace("\n", " ") + "|" + r.LocalFile;
            }
            catch (Exception ex) { return "ERR|" + ex.Message + "|"; }
        }

        /// <summary>打开最近一次生成的电子表格。</summary>
        public void OpenLastReport()
        {
            try
            {
                var dir = Collector.ReportDirectory;
                var files = Directory.GetFiles(dir, "*.xlsx");
                if (files.Length == 0) { Collector.BuildLocalOnly(); files = Directory.GetFiles(dir, "*.xlsx"); }
                if (files.Length == 0) return;
                Array.Sort(files);
                Process.Start(new ProcessStartInfo(files[files.Length - 1]) { UseShellExecute = true });
            }
            catch { }
        }

        /// <summary>打开上报目标共享目录。</summary>
        public void OpenTargetFolder()
        {
            try
            {
                var unc = Collector.NormalizeTarget(AppConfig.Get("collect.ip", ""), AppConfig.Get("collect.share", ""));
                if (unc.Length > 0) Process.Start(new ProcessStartInfo(unc) { UseShellExecute = true });
                else Process.Start(new ProcessStartInfo(Collector.ReportDirectory) { UseShellExecute = true });
            }
            catch { }
        }

        /// <summary>打开配置目录（配置文件与本地报表所在位置）。</summary>
        public void OpenConfigDir()
        {
            try
            {
                var cfg = AppConfig.ConfigPath;
                var dir = Path.GetDirectoryName(cfg);
                if (string.IsNullOrEmpty(dir)) dir = Collector.ReportDirectory;
                if (Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch { }
        }

        // ---------- 数据接口（API） ----------
        /// <summary>供 ApiReport 判断权限状态（静态，不暴露给 COM）。</summary>
        internal static bool IsRunningAsAdmin()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>
        /// 接口配置一次性返回，\\u0001 分隔：
        /// 0 推送开关 | 1 接口地址 | 2 是否已设令牌 | 3 超时 | 4 本地服务开关 | 5 本地端口
        /// | 6 是否已设本地令牌 | 7 本地服务地址 | 8 待发队列条数 | 9 上次推送时间 | 10 上次推送结果
        /// </summary>
        public string GetApiSettings()
        {
            var parts = new[]
            {
                ApiClient.Enabled ? "1" : "0",
                ApiClient.Url,
                AppConfig.Get("api.token", "").Length > 0 ? "1" : "0",
                ApiClient.TimeoutSec.ToString(),
                ApiServer.Enabled ? "1" : "0",
                ApiServer.ConfiguredPort.ToString(),
                AppConfig.Get("api.local.token", "").Length > 0 ? "1" : "0",
                ApiServer.BaseUrl,
                ApiClient.OutboxCount().ToString(),
                AppConfig.Get("api.lastTime", ""),
                AppConfig.Get("api.lastResult", "")
            };
            return string.Join("\u0001", parts);
        }

        /// <summary>保存接口配置。token 传空表示不修改，传 __CLEAR__ 表示清空。</summary>
        public string SaveApiConfig(string url, string token, int timeoutSec, bool pushEnabled,
                                    bool localEnabled, int localPort, string localToken)
        {
            try
            {
                var u = (url ?? "").Trim();
                if (u.Length > 0 && !(u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                   || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    return "ERR|接口地址需以 http:// 或 https:// 开头";

                AppConfig.Set("api.url", u);
                if (token == "__CLEAR__") AppConfig.Set("api.token", "");
                else if (!string.IsNullOrEmpty(token)) AppConfig.Set("api.token", AppConfig.Protect(token));

                if (timeoutSec < 3) timeoutSec = 3;
                if (timeoutSec > 120) timeoutSec = 120;
                AppConfig.SetInt("api.timeout", timeoutSec);

                if (localPort < 1024 || localPort > 65535) localPort = ApiServer.DefaultPort;
                AppConfig.SetInt("api.local.port", localPort);
                if (localToken == "__CLEAR__") AppConfig.Set("api.local.token", "");
                else if (!string.IsNullOrEmpty(localToken)) AppConfig.Set("api.local.token", AppConfig.Protect(localToken));

                AppConfig.SetBool("api.local.enabled", localEnabled);
                AppConfig.SetBool("api.enabled", pushEnabled);

                // 本地服务按新配置重启（端口可能已变）
                ApiServer.Stop();
                string srvErr = localEnabled ? ApiServer.Start() : "";

                var msg = "接口配置已保存。" + (pushEnabled ? "推送已开启。" : "推送未开启。");
                if (localEnabled) msg += srvErr.Length == 0 ? " 本地服务已启动：" + ApiServer.BaseUrl : " " + srvErr;
                else msg += " 本地服务未开启。";
                return "OK|" + msg;
            }
            catch (Exception ex) { return "ERR|保存失败：" + ex.Message; }
        }

        /// <summary>用界面上填写的地址做一次连通性测试（不入待发队列）。</summary>
        public string ApiTest(string url, string token)
        {
            try
            {
                var u = (url ?? "").Trim();
                if (u.Length == 0) u = ApiClient.Url;
                if (u.Length == 0) return "ERR|请先填写接口地址";
                var tk = string.IsNullOrEmpty(token) ? ApiClient.Token : token;
                var r = ApiClient.Test(u, tk, ApiClient.TimeoutSec);
                return (r.Ok ? "OK|" : "ERR|") + "HTTP " + r.Status + " " + r.Message
                     + "｜报文 " + ApiReport.SchemaName + "/" + ApiReport.SchemaVersion;
            }
            catch (Exception ex) { return "ERR|测试失败：" + ex.Message; }
        }

        /// <summary>立即按当前配置推送一次（仅接口，不走共享）。</summary>
        public string ApiPushNow()
        {
            try
            {
                if (ApiClient.Url.Length == 0) return "ERR|未配置接口地址";
                var s = Collector.Collect();
                var r = ApiClient.Push(s, null);
                AppConfig.Set("api.lastTime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                AppConfig.Set("api.lastResult",
                    (r.Ok ? "成功 HTTP " + r.Status : "失败 " + r.Message).Replace("\r", "").Replace("\n", " "));
                string extra = r.Queued ? "（已进入待发队列，共 " + ApiClient.OutboxCount() + " 条）" : "";
                return (r.Ok ? "OK|" : "ERR|") + "HTTP " + r.Status + " " + r.Message + extra;
            }
            catch (Exception ex) { return "ERR|推送失败：" + ex.Message; }
        }

        /// <summary>补发待发队列，返回 OK|已补发 x/y。</summary>
        public string ApiFlushOutbox()
        {
            try
            {
                if (!ApiClient.Enabled) return "ERR|推送未开启，无需补发";
                if (ApiClient.Url.Length == 0) return "ERR|未配置接口地址";
                string r = ApiClient.FlushOutbox(50);
                return "OK|补发结果 " + r + "，剩余 " + ApiClient.OutboxCount() + " 条";
            }
            catch (Exception ex) { return "ERR|补发失败：" + ex.Message; }
        }

        /// <summary>本地服务状态：running|port|baseUrl|enabled。</summary>
        public string ApiServerState()
        {
            return (ApiServer.Running ? "1" : "0") + "|" + ApiServer.Port + "|" + ApiServer.BaseUrl
                 + "|" + (ApiServer.Enabled ? "1" : "0");
        }

        /// <summary>把接口文档与契约文件导出到配置目录并打开，交给未来的服务端开发使用。</summary>
        public string ExportApiDoc()
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PortSecurityTool", "api");
                Directory.CreateDirectory(dir);

                string doc = ApiDoc.Text;
                File.WriteAllText(Path.Combine(dir, "API接口说明_v" + ApiReport.SchemaVersion + ".md"), doc, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(dir, "schema_" + ApiReport.SchemaVersion + ".json"), ApiReport.SchemaJson(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(dir, "example_" + ApiReport.SchemaVersion + ".json"),
                    ApiReport.ToJson(Collector.Collect(), null, true), new UTF8Encoding(false));

                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                return "OK|已导出到 " + dir;
            }
            catch (Exception ex) { return "ERR|导出失败：" + ex.Message; }
        }

        /// <summary>用系统默认浏览器打开地址（本地接口页等）。</summary>
        public void OpenUrl(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        public void CopyText(string text)
        {
            try
            {
                if (_form.InvokeRequired) _form.BeginInvoke(new Action(() => Clipboard.SetText(text ?? "")));
                else Clipboard.SetText(text ?? "");
            }
            catch { }
        }

        // ---------- 局域网远程桌面 ----------
        /// <summary>本机网段，首个为默认（\u0001 分隔）。</summary>
        public string RdpCidrs()
        {
            try
            {
                var list = NetDiscovery.LocalCidrs();
                if (list.Count == 0) list.Add(NetDiscovery.DefaultCidr());
                return string.Join("\u0001", list.ToArray());
            }
            catch { return NetDiscovery.DefaultCidr(); }
        }

        public string RdpScanStart(string spec)
        {
            try
            {
                var r = NetDiscovery.StartScan(spec);
                if (r.StartsWith("OK")) _form.BeginRdpTicks();
                return r;
            }
            catch (Exception ex) { return "ERR|" + ex.Message; }
        }

        public string RdpScanState()
        {
            try { return NetDiscovery.State(); } catch { return "0|100|0|"; }
        }

        public string RdpScanRows()
        {
            try { return NetDiscovery.Rows(); } catch { return ""; }
        }

        public void RdpScanStop() { try { NetDiscovery.Stop(); } catch { } }

        /// <summary>生成 .rdp 并拉起 mstsc。</summary>
        public string RdpConnect(string ip, string user, bool clipboard, bool drives, int width, int height)
        {
            try { return RdpClient.Connect(ip, user, clipboard, drives, width, height); }
            catch (Exception ex) { return "ERR|" + ex.Message; }
        }

        /// <summary>向目标发起远程桌面请求（异步，结果用 RdpReqState 轮询）。</summary>
        public string RdpRequest(string ip)
        {
            try
            {
                var r = RdpClient.Request(ip);
                if (r.StartsWith("OK")) _form.BeginRdpTicks();
                return r;
            }
            catch (Exception ex) { return "ERR|" + ex.Message; }
        }

        public string RdpReqState()
        {
            try { return RdpClient.ReqState(); } catch { return "0|"; }
        }

        public void RdpReqReset() { try { RdpClient.ResetState(); } catch { } }

        /// <summary>通知目标立即恢复 3389 封堵。</summary>
        public string RdpClose(string ip)
        {
            try { return RdpClient.Close(ip); }
            catch (Exception ex) { return "ERR|" + ex.Message; }
        }

        /// <summary>
        /// 远程桌面配置：running|端口|有令牌|放行IP|放行时间|本机RDP开关|用户|宽|高|剪贴板|磁盘
        /// </summary>
        public string GetRdpSettings()
        {
            var parts = new[]
            {
                RdpAgent.Enabled ? "1" : "0",
                RdpAgent.Port.ToString(),
                RdpAgent.Token.Length > 0 ? "1" : "0",
                AppConfig.Get("rdp.allowIp", ""),
                AppConfig.Get("rdp.allowTime", ""),
                RdpAgent.RdpEnabled() ? "1" : "0",
                RdpClient.LastUser,
                RdpClient.Width.ToString(),
                RdpClient.Height.ToString(),
                RdpClient.Clipboard ? "1" : "0",
                RdpClient.Drives ? "1" : "0",
                RdpAgent.Running ? "1" : "0"
            };
            return string.Join("\u0001", parts);
        }

        /// <summary>保存「响应远程请求」配置。令牌传空表示不修改，__CLEAR__ 表示清空。</summary>
        public string SaveRdpConfig(bool enabled, int port, string token)
        {
            try
            {
                if (port < 1024 || port > 65535) return "ERR|端口需在 1024~65535 之间";
                if (enabled && string.IsNullOrEmpty(token) && RdpAgent.Token.Length == 0)
                    return "ERR|开启响应前必须设置访问令牌（用于校验请求方身份）";

                AppConfig.SetBool("rdp.agent.enabled", enabled);
                AppConfig.SetInt("rdp.agent.port", port);
                if (token == "__CLEAR__") AppConfig.Set("rdp.agent.token", "");
                else if (!string.IsNullOrEmpty(token)) AppConfig.Set("rdp.agent.token", AppConfig.Protect(token));

                RdpAgent.Stop();
                string err = enabled ? RdpAgent.Start() : "";
                if (err.Length > 0) return "ERR|" + err;
                return "OK|" + (enabled ? "已开启远程请求响应，监听端口 " + port : "已关闭远程请求响应");
            }
            catch (Exception ex) { return "ERR|" + ex.Message; }
        }

        /// <summary>本机（被请求方）立即恢复 3389 封堵。</summary>
        public string RdpRestoreLocal()
        {
            try
            {
                RdpAgent.Restore(out string msg);
                return "OK|" + msg;
            }
            catch (Exception ex) { return "ERR|" + ex.Message; }
        }

        // ---------- 窗口控制 ----------
        public void ResizeWindow(int w, int h)
        {
            _form.BeginInvoke(new Action(() => _form.Size = new Size(w, h)));
        }
        public void MoveWindow(int x, int y)
        {
            _form.BeginInvoke(new Action(() => _form.Location = new Point(x, y)));
        }
        public void Minimize()
        {
            try
            {
                if (_form.InvokeRequired)
                    _form.BeginInvoke(new Action(() => _form.WindowState = FormWindowState.Minimized));
                else
                    _form.WindowState = FormWindowState.Minimized;
            }
            catch { }
        }
        public void Close()
        {
            try
            {
                // 关闭行为受「最小化到托盘」设置控制：开启时隐藏窗口，否则退出进程
                if (_form.InvokeRequired)
                    _form.BeginInvoke(new Action(() => _form.RequestClose()));
                else
                    _form.RequestClose();
            }
            catch
            {
                // 兜底：确保进程退出
                System.Windows.Forms.Application.ExitThread();
            }
        }
        public void HideToTray()
        {
            try
            {
                if (_form.InvokeRequired)
                    _form.BeginInvoke(new Action(() => _form.HideToTray()));
                else
                    _form.HideToTray();
            }
            catch { }
        }
        public void ShowMainWindow()
        {
            try
            {
                if (_form.InvokeRequired)
                    _form.BeginInvoke(new Action(() => _form.ShowMain()));
                else
                    _form.ShowMain();
            }
            catch { }
        }
        public int ScreenWidth { get { return Screen.PrimaryScreen.Bounds.Width; } }
        public int ScreenHeight { get { return Screen.PrimaryScreen.Bounds.Height; } }
    }

    // ============================================================
    //  WScript.Shell.Exec 返回对象
    // ============================================================
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class ExecResult
    {
        internal string _out = "";
        internal string _err = "";
        public int ExitCode { get; set; }

        public StdStream StdOut { get { return new StdStream(_out); } }
        public StdStream StdErr { get { return new StdStream(_err); } }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class StdStream
    {
        private readonly string _text;
        public StdStream(string text) { _text = text; }
        public string ReadAll() { return _text; }
        public bool AtEndOfStream { get { return true; } }
    }

    // ============================================================
    //  系统 ANSI 编码（中文系统为 GBK/936）
    //  .NET Core 默认 UTF-8，且未注册代码页提供程序时 GetEncoding 会抛异常，
    //  这里统一兜底，避免读写文本时静默失败
    // ============================================================
    internal static class Enc
    {
        private static Encoding _ansi;

        public static Encoding Ansi
        {
            get
            {
                if (_ansi != null) return _ansi;
                try { _ansi = Encoding.GetEncoding(0); }
                catch
                {
                    try { _ansi = Encoding.GetEncoding(936); }
                    catch { _ansi = Encoding.UTF8; }
                }
                return _ansi;
            }
        }
    }

    // ============================================================
    //  Scripting.Folder 对象
    // ============================================================
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class FolderObject
    {
        public string Path { get; set; }
        public string ShortPath { get { return Path; } }
    }

    // ============================================================
    //  Scripting.TextStream 对象（ReadAll/ReadLine/Write/WriteLine/Close）
    //  mode: 1=ForReading, 2=ForWriting, 8=ForAppending
    // ============================================================
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class TextStream
    {
        private readonly string _path;
        private readonly int _mode;
        private StreamReader _reader;
        private StreamWriter _writer;

        public TextStream(string path, int mode, bool create)
        {
            _path = path;
            _mode = mode;
            // 与原 FSO 行为一致：读写都用系统 ANSI 代码页（中文系统即 GBK），
            // 否则写出的 .cmd 批处理中文会乱码、读 netsh 中文输出也会乱码
            Encoding enc = Enc.Ansi;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                if (mode == 1) _reader = new StreamReader(path, enc);
                else _writer = new StreamWriter(path, mode == 8 && create, enc);
            }
            catch { }
        }

        public bool AtEndOfStream
        {
            get { return _reader == null || _reader.EndOfStream; }
        }

        public string ReadAll()
        {
            try { return _reader == null ? "" : _reader.ReadToEnd(); }
            catch { return ""; }
        }

        public string ReadLine()
        {
            try { return _reader == null ? null : _reader.ReadLine(); }
            catch { return null; }
        }

        public void Write(string s)
        {
            try { if (_writer != null) _writer.Write(s); }
            catch { }
        }

        public void WriteLine(string s)
        {
            try { if (_writer != null) _writer.WriteLine(s); }
            catch { }
        }

        public void Close()
        {
            try { if (_reader != null) { _reader.Close(); _reader = null; } } catch { }
            try
            {
                if (_writer != null) { _writer.Flush(); _writer.Close(); _writer = null; }
            }
            catch { }
        }
    }
}
