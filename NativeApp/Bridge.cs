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

        // ---------- 程序设置 ----------
        /// <summary>程序设置一次性返回：0 开机自启 | 1 关闭时最小化到托盘。</summary>
        public string GetSettings()
        {
            var parts = new[]
            {
                SystemIntegration.AutoStartEnabled() ? "1" : "0",
                AppConfig.GetBool("ui.tray", true) ? "1" : "0",
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

        /// <summary>打开配置目录（配置文件所在位置）。</summary>
        public void OpenConfigDir()
        {
            try
            {
                var cfg = AppConfig.ConfigPath;
                var dir = Path.GetDirectoryName(cfg);
                if (string.IsNullOrEmpty(dir)) dir = Path.GetDirectoryName(AppConfig.ConfigPath);
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

                                /// <summary>局域网代理状态：running|port|enabled|tokenSet|baseUrl。</summary>
        public string GetLanAgentState()
        {
            return (LanAgent.Running ? "1" : "0") + "|" + LanAgent.Port + "|"
                 + (LanAgent.Enabled ? "1" : "0") + "|"
                 + (LanAgent.Token.Length > 0 ? "1" : "0") + "|" + LanAgent.BaseUrl;
        }

        /// <summary>保存局域网发现配置。token 传空表示不修改，传 __CLEAR__ 表示清空。</summary>
        public string SaveLanAgentConfig(bool enabled, int port, string token)
        {
            try
            {
                if (port < 1024 || port > 65535) port = LanAgent.DefaultPort;
                AppConfig.SetInt("api.lan.port", port);
                if (token == "__CLEAR__") AppConfig.Set("api.local.token", "");
                else if (!string.IsNullOrEmpty(token)) AppConfig.Set("api.local.token", AppConfig.Protect(token));
                AppConfig.SetBool("api.lan.enabled", enabled);

                LanAgent.Stop();
                string err = enabled ? LanAgent.Start() : "";
                var msg = enabled
                    ? (err.Length == 0 ? "局域网代理已启动：" + LanAgent.BaseUrl : err)
                    : "局域网代理已关闭。";
                return (err.Length == 0 ? "OK|" : "ERR|") + msg
                    + " 服务端需填写同一令牌才能拉取数据。";
            }
            catch (Exception ex) { return "ERR|保存失败：" + ex.Message; }
        }

        public string LanAgentAllowFirewall() { return LanAgent.AllowFirewall(); }

        /// <summary>立即执行一次自动同步口径的采集推送（同「立即推送」，供界面按钮使用）。</summary>
        public string ApiSyncNow()
        {
            try { return _form.UploadNow(); }
            catch (Exception ex) { return "ERR|同步失败：" + ex.Message; }
        }

        /// <summary>
        /// 「数据上传」页配置一次性返回，\\u0001 分隔：
        /// 0 自动上传开关 | 1 自动上传间隔(分钟) | 2 上次上传时间 | 3 局域网代理运行中
        /// | 4 局域网端口 | 5 局域网开关 | 6 是否已设拉取令牌 | 7 局域网代理地址 | 8 缓存采集时间
        /// </summary>
        public string GetUploadSettings()
        {
            var parts = new[]
            {
                AppConfig.GetBool("api.sync.enabled", true) ? "1" : "0",
                AppConfig.GetInt("api.sync.interval", 15).ToString(),
                AppConfig.Get("api.sync.lastTime", ""),
                LanAgent.Running ? "1" : "0",
                LanAgent.Port.ToString(),
                LanAgent.Enabled ? "1" : "0",
                LanAgent.Token.Length > 0 ? "1" : "0",
                LanAgent.BaseUrl,
                LanAgent.CachedAt
            };
            return string.Join("\u0001", parts);
        }

        /// <summary>保存「数据上传」页配置：自动上传开关与间隔、局域网代理开关与端口、访问令牌。</summary>
        public string SaveUploadConfig(bool autoEnabled, int interval, bool lanEnabled, int lanPort, string lanToken)
        {
            try
            {
                if (interval < 1) interval = 1;
                if (interval > 10080) interval = 10080;
                AppConfig.SetInt("api.sync.interval", interval);
                AppConfig.SetBool("api.sync.enabled", autoEnabled);

                if (lanPort < 1024 || lanPort > 65535) lanPort = LanAgent.DefaultPort;
                AppConfig.SetInt("api.lan.port", lanPort);
                if (lanToken == "__CLEAR__") AppConfig.Set("api.local.token", "");
                else if (!string.IsNullOrEmpty(lanToken)) AppConfig.Set("api.local.token", AppConfig.Protect(lanToken));
                AppConfig.SetBool("api.lan.enabled", lanEnabled);

                LanAgent.Stop();
                string err = lanEnabled ? LanAgent.Start() : "";
                try { _form.RestartApiSync(); } catch { }

                var msg = "配置已保存。";
                msg += autoEnabled ? " 自动上传已开启，每 " + interval + " 分钟刷新一次数据。" : " 自动上传未开启。";
                msg += lanEnabled
                    ? (err.Length == 0 ? " 局域网代理运行中：" + LanAgent.BaseUrl : " " + err)
                    : " 局域网代理已关闭，服务端将扫描不到本机。";
                return (err.Length == 0 ? "OK|" : "ERR|") + msg;
            }
            catch (Exception ex) { return "ERR|保存失败：" + ex.Message; }
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

                // ---------- 远程桌面（SubnetDesk） ----------
        /// <summary>启动内置的 SubnetDesk 远程桌面；target 为空则打开首页。</summary>
        public string OpenSubnetDesk(string target)
        {
            try { return EmbeddedRemote.Open(target); }
            catch (Exception ex) { return "ERR|" + ex.Message; }
        }

        /// <summary>关闭由本工具启动的 SubnetDesk 进程。</summary>
        public string CloseSubnetDesk()
        {
            try { return EmbeddedRemote.CloseAll(); }
            catch (Exception ex) { return "ERR|" + ex.Message; }
        }

        // ---------- 局域网远程桌面（旧实现，保留但不再由界面调用） ----------
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
