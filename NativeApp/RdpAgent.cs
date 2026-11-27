using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PortSecurityTool
{
    // ============================================================
    //  远程桌面请求代理（被请求方）
    //
    //  开启后在本机 0.0.0.0:18761 监听一个极简 HTTP 服务：
    //    GET  /pst/v1/ping         免令牌：供局域网发现识别「这台机器装了本工具」
    //    GET  /pst/v1/state        需令牌：RDP 开关、3389 监听、当前临时放行的 IP
    //    POST /pst/v1/rdp-request  需令牌：弹窗征得本机用户同意后，只对请求方 IP 临时放行 3389
    //    POST /pst/v1/rdp-close    需令牌：立即恢复封堵
    //
    //  安全约束：
    //   · 默认关闭；开启必须配置访问令牌（否则拒绝一切请求）
    //   · 放行只针对请求方的单个 IP，不做全网开放
    //   · 会临时移除 3389 的封堵规则（否则 Windows 防火墙 block 优先于 allow，放行无效）
    //   · 操作记录写入配置与界面日志，可一键恢复
    // ============================================================
    internal static class RdpAgent
    {
        public const int DefaultPort = 18761;
        private const string RuleName = "Allow_RDP_Remote";

        private static readonly object _lock = new object();
        private static TcpListener _srv;
        private static Thread _loop;
        private static volatile bool _run;
        private static MainForm _form;

        public static bool Running { get { return _run; } }

        public static int Port
        {
            get
            {
                int p = AppConfig.GetInt("rdp.agent.port", DefaultPort);
                if (p < 1024 || p > 65535) p = DefaultPort;
                return p;
            }
        }

        public static bool Enabled { get { return AppConfig.GetBool("rdp.agent.enabled", false); } }
        public static string Token { get { return AppConfig.Unprotect(AppConfig.Get("rdp.agent.token", "")); } }

        public static void AttachForm(MainForm f) { _form = f; }

        // ---------------- 启停 ----------------

        public static string Start()
        {
            lock (_lock)
            {
                if (_run) return "";
                if (Token.Length == 0) return "未设置访问令牌，远程请求功能不会响应（请先设置令牌）";
                try
                {
                    var l = new TcpListener(IPAddress.Any, Port);
                    l.Start(32);
                    _srv = l;
                    _run = true;
                    _loop = new Thread(AcceptLoop) { IsBackground = true, Name = "PstRdpAgent" };
                    _loop.Start();
                    return "";
                }
                catch (Exception ex)
                {
                    _run = false;
                    try { if (_srv != null) _srv.Stop(); } catch { }
                    _srv = null;
                    return "远程请求服务启动失败：" + ex.Message + "（端口 " + Port + " 可能被占用）";
                }
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                _run = false;
                try { if (_srv != null) _srv.Stop(); } catch { }
                _srv = null;
            }
        }

        public static void ApplyConfig()
        {
            if (Enabled) { if (!_run) Start(); }
            else Stop();
        }

        public static string StatusText()
        {
            return (_run ? "1" : "0") + "|" + Port + "|" + (Token.Length > 0 ? "1" : "0") + "|"
                 + AppConfig.Get("rdp.allowIp", "") + "|" + AppConfig.Get("rdp.allowTime", "");
        }

        // ---------------- HTTP ----------------

        private static void AcceptLoop()
        {
            while (_run)
            {
                TcpClient c = null;
                try { c = _srv.AcceptTcpClient(); }
                catch { break; }
                try { ThreadPool.QueueUserWorkItem(delegate { Handle(c); }); }
                catch { try { c.Close(); } catch { } }
            }
        }

        private static void Handle(object state)
        {
            var c = state as TcpClient;
            if (c == null) return;
            try
            {
                c.ReceiveTimeout = 3000;
                string reqText;
                using (var ns = c.GetStream())
                {
                    reqText = ReadRequest(ns);
                    if (reqText.Length == 0) return;

                    string body, contentType = "application/json; charset=utf-8";
                    int status = 200;
                    string method, path, tokenHdr;
                    ParseRequest(reqText, out method, out path, out tokenHdr);

                    string remoteIp = "";
                    try { remoteIp = ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString(); } catch { }

                    if (path == "/pst/v1/ping")
                    {
                        body = PingJson();
                    }
                    else if (path == "/pst/v1/state" || path == "/pst/v1/rdp-request" || path == "/pst/v1/rdp-close")
                    {
                        if (!AuthOk(tokenHdr)) { status = 403; body = "{\"ok\":\"0\",\"msg\":\"令牌校验失败\"}"; }
                        else if (path == "/pst/v1/state") body = StateJson();
                        else if (path == "/pst/v1/rdp-close") body = CloseJson();
                        else body = RequestJson(remoteIp);
                    }
                    else
                    {
                        status = 404; body = "{\"ok\":\"0\",\"msg\":\"未知接口\"}";
                    }

                    var bytes = Encoding.UTF8.GetBytes(body);
                    var head = "HTTP/1.1 " + status + " " + (status == 200 ? "OK" : status == 403 ? "Forbidden" : "Not Found") + "\r\n"
                             + "Content-Type: " + contentType + "\r\n"
                             + "Content-Length: " + bytes.Length + "\r\n"
                             + "Connection: close\r\n\r\n";
                    var hb = Encoding.ASCII.GetBytes(head);
                    ns.Write(hb, 0, hb.Length);
                    ns.Write(bytes, 0, bytes.Length);
                    ns.Flush();
                }
            }
            catch { }
            finally { try { c.Close(); } catch { } }
        }

        private static string ReadRequest(NetworkStream ns)
        {
            var sb = new StringBuilder();
            var buf = new byte[1024];
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2500)
            {
                int n;
                try { n = ns.Read(buf, 0, buf.Length); }
                catch { break; }
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                if (sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0) break;
                if (sb.Length > 8192) break;
            }
            return sb.ToString();
        }

        private static void ParseRequest(string req, out string method, out string path, out string token)
        {
            method = ""; path = ""; token = "";
            var lines = req.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return;
            var parts = lines[0].Split(' ');
            if (parts.Length >= 2) { method = parts[0].ToUpperInvariant(); path = parts[1]; }
            int q = path.IndexOf('?');
            if (q >= 0)
            {
                var query = path.Substring(q + 1);
                path = path.Substring(0, q);
                foreach (var kv in query.Split('&'))
                {
                    if (kv.StartsWith("token=", StringComparison.Ordinal))
                        token = Uri.UnescapeDataString(kv.Substring(6));
                }
            }
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) break;
                int c = lines[i].IndexOf(':');
                if (c <= 0) continue;
                var name = lines[i].Substring(0, c).Trim();
                var val = lines[i].Substring(c + 1).Trim();
                if (string.Equals(name, "X-Pst-Token", StringComparison.OrdinalIgnoreCase)) token = val;

            }
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "无返回信息";
            int i = s.IndexOfAny(new[] { '\r', '\n' });
            var t = i > 0 ? s.Substring(0, i) : s;
            return t.Trim();
        }

        private static bool AuthOk(string given)
        {
            var tok = Token;
            if (tok.Length == 0) return false;
            return string.Equals(given, tok, StringComparison.Ordinal);
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string PingJson()
        {
            return "{\"pst\":\"1\",\"schema\":\"" + ApiReport.SchemaVersion + "\","
                 + "\"name\":\"" + Esc(Environment.MachineName) + "\","
                 + "\"user\":\"" + Esc(Environment.UserName) + "\","
                 + "\"rdp\":\"" + (RdpEnabled() ? "1" : "0") + "\","
                 + "\"agent\":\"" + Esc(ApiReport.AgentVersionText) + "\"}";
        }

        private static string StateJson()
        {
            bool listen = NetDiscovery.TcpProbe("127.0.0.1", 3389, 350, out _);
            return "{\"ok\":\"1\","
                 + "\"rdp\":\"" + (RdpEnabled() ? "1" : "0") + "\","
                 + "\"listen\":\"" + (listen ? "1" : "0") + "\","
                 + "\"allow\":\"" + Esc(AppConfig.Get("rdp.allowIp", "")) + "\","
                 + "\"since\":\"" + Esc(AppConfig.Get("rdp.allowTime", "")) + "\"}";
        }

        private static string RequestJson(string requesterIp)
        {
            bool allowed = AskUser(requesterIp);
            if (!allowed)
            {
                Log("远程桌面请求被拒绝：" + requesterIp, "warn");
                return "{\"allowed\":\"0\",\"msg\":\"本机用户拒绝或超时未响应\"}";
            }

            string msg;
            bool ok = AllowFor(requesterIp, out msg);
            if (ok) Log("已为 " + requesterIp + " 临时放行远程桌面 3389", "ok");
            else Log("放行失败：" + msg, "err");
            return "{\"allowed\":\"" + (ok ? "1" : "0") + "\","
                 + "\"rdp\":\"" + (RdpEnabled() ? "1" : "0") + "\","
                 + "\"msg\":\"" + Esc(msg) + "\"}";
        }

        private static string CloseJson()
        {
            string msg;
            Restore(out msg);
            Log("已恢复 3389 封堵：" + msg, "ok");
            return "{\"ok\":\"1\",\"msg\":\"" + Esc(msg) + "\"}";
        }

        private static void Log(string text, string kind)
        {
            try { if (_form != null) _form.NotifyLog(kind, text); } catch { }
        }

        // ---------------- 征询本机用户 ----------------

        private static bool AskUser(string fromIp)
        {
            if (_form == null) return false;
            bool allowed = false;
            try
            {
                _form.Invoke(new Action(delegate
                {
                    try
                    {
                        using (var dlg = new RdpPromptForm(fromIp))
                            allowed = dlg.ShowDialog(_form) == DialogResult.OK;
                    }
                    catch { allowed = false; }
                }));
            }
            catch { return false; }
            return allowed;
        }

        // ---------------- 放行 / 恢复 ----------------

        public static bool AllowFor(string requesterIp, out string message)
        {
            message = "";
            try
            {
                // 记录原始开关，恢复时照原样还原
                string orig = AppConfig.Get("rdp.origDeny", "");
                if (orig.Length == 0) AppConfig.Set("rdp.origDeny", RdpEnabled() ? "0" : "1");

                // 1) 移除 3389 封堵规则：Windows 防火墙 block 优先于 allow，留着封堵放行不生效
                Netsh("advfirewall firewall delete rule name=\"Block_Port_3389_TCP_in\"");
                Netsh("advfirewall firewall delete rule name=\"Block_Port_3389_UDP_in\"");

                // 2) 只对请求方 IP 放行
                Netsh("advfirewall firewall delete rule name=\"" + RuleName + "\"");
                string netOut;
                int rc = Netsh("advfirewall firewall add rule name=\"" + RuleName +
                               "\" dir=in action=allow protocol=TCP localport=3389 remoteip=" + requesterIp,
                               out netOut);
                if (rc != 0)
                {
                    message = "防火墙放行失败（netsh 返回 " + rc + "）：" + FirstLine(netOut) + "，请确认程序以管理员身份运行";
                    return false;
                }

                // 3) 系统层面允许远程桌面
                bool regOk = SetRdpEnabled(true);

                // 4) 确保远程桌面服务在跑
                bool svcOk = StartTermService();

                // 5) 等 3389 起来
                bool listening = false;
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 4000)
                {
                    if (NetDiscovery.TcpProbe("127.0.0.1", 3389, 300, out _)) { listening = true; break; }
                    Thread.Sleep(250);
                }

                AppConfig.Set("rdp.allowIp", requesterIp);
                AppConfig.Set("rdp.allowTime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                if (!regOk)
                {
                    message = "远程桌面开关写入失败（需要管理员权限），请手动在系统设置中开启远程桌面";
                    return false;
                }
                if (!listening)
                {
                    message = "已放行 " + requesterIp + "，但 3389 尚未监听" + (svcOk ? "（服务已启动，稍候重试）" : "（TermService 启动失败）");
                    return false;
                }
                message = "已放行 " + requesterIp + "，3389 正在监听";
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        public static string Restore()
        {
            string msg;
            Restore(out msg);
            return msg;
        }

        public static void Restore(out string message)
        {
            message = "";
            try
            {
                Netsh("advfirewall firewall delete rule name=\"" + RuleName + "\"");
                Netsh("advfirewall firewall add rule name=\"Block_Port_3389_TCP_in\" dir=in action=block protocol=TCP localport=3389");
                Netsh("advfirewall firewall add rule name=\"Block_Port_3389_UDP_in\" dir=in action=block protocol=UDP localport=3389");

                string orig = AppConfig.Get("rdp.origDeny", "1");
                SetRdpEnabled(orig != "1");

                AppConfig.Set("rdp.allowIp", "");
                AppConfig.Set("rdp.allowTime", "");
                AppConfig.Set("rdp.origDeny", "");
                message = "3389 已恢复为封堵状态";
            }
            catch (Exception ex) { message = "恢复失败：" + ex.Message; }
        }

        // ---------------- 系统操作 ----------------

        private static int Netsh(string args) { string o; return Netsh(args, out o); }

        private static int Netsh(string args, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo("netsh", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return -1;
                    var so = p.StandardOutput.ReadToEnd();
                    var se = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return -1; }
                    output = (so + " " + se).Trim();
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { output = ex.Message; return -1; }
        }

        /// <summary>确保远程桌面服务运行（直接调 SCM，不依赖 sc.exe，避免被策略禁用）。</summary>
        private static bool StartTermService()
        {
            IntPtr scm = IntPtr.Zero, svc = IntPtr.Zero;
            try
            {
                scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
                if (scm == IntPtr.Zero) return false;
                svc = OpenService(scm, "TermService", SERVICE_START | SERVICE_QUERY_STATUS);
                if (svc == IntPtr.Zero) return false;

                var status = new SERVICE_STATUS();
                if (QueryServiceStatus(svc, ref status) && status.dwCurrentState == SERVICE_RUNNING) return true;

                if (!StartService(svc, 0, null))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ERROR_SERVICE_ALREADY_RUNNING) return false;
                }

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 6000)
                {
                    if (QueryServiceStatus(svc, ref status) && status.dwCurrentState == SERVICE_RUNNING) return true;
                    Thread.Sleep(250);
                }
                return false;
            }
            catch { return false; }
            finally
            {
                try { if (svc != IntPtr.Zero) CloseServiceHandle(svc); } catch { }
                try { if (scm != IntPtr.Zero) CloseServiceHandle(scm); } catch { }
            }
        }

        /// <summary>TermService 是否运行（原生查询，供界面与自检使用）。</summary>
        public static bool TermServiceRunning()
        {
            IntPtr scm = IntPtr.Zero, svc = IntPtr.Zero;
            try
            {
                scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
                if (scm == IntPtr.Zero) return false;
                svc = OpenService(scm, "TermService", SERVICE_QUERY_STATUS);
                if (svc == IntPtr.Zero) return false;
                var st = new SERVICE_STATUS();
                return QueryServiceStatus(svc, ref st) && st.dwCurrentState == SERVICE_RUNNING;
            }
            catch { return false; }
            finally
            {
                try { if (svc != IntPtr.Zero) CloseServiceHandle(svc); } catch { }
                try { if (scm != IntPtr.Zero) CloseServiceHandle(scm); } catch { }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public int dwServiceType;
            public int dwCurrentState;
            public int dwControlsAccepted;
            public int dwWin32ExitCode;
            public int dwServiceSpecificExitCode;
            public int dwCheckPoint;
            public int dwWaitHint;
        }

        private const int SC_MANAGER_CONNECT = 0x0001;
        private const int SERVICE_QUERY_STATUS = 0x0004;
        private const int SERVICE_START = 0x0010;
        private const int SERVICE_RUNNING = 0x00000004;
        private const int ERROR_SERVICE_ALREADY_RUNNING = 1056;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManager(string machine, string database, int access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenService(IntPtr scm, string name, int access);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool StartService(IntPtr svc, int argc, string[] argv);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatus(IntPtr svc, ref SERVICE_STATUS status);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        private const string TsKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";

        public static bool RdpEnabled()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(TsKey, false))
                {
                    if (k == null) return false;
                    var v = k.GetValue("fDenyTSConnections");
                    if (v == null) return false;
                    return Convert.ToInt32(v) == 0;
                }
            }
            catch { return false; }
        }

        private static bool SetRdpEnabled(bool on)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(TsKey, true))
                {
                    if (k == null) return false;
                    k.SetValue("fDenyTSConnections", on ? 0 : 1, RegistryValueKind.DWord);
                }
                return true;
            }
            catch { return false; }
        }
    }

    // ============================================================
    //  远程桌面请求确认框：被请求方看到的授权弹窗
    // ============================================================
    internal sealed class RdpPromptForm : Form
    {
        private int _left = 30;

        public RdpPromptForm(string fromIp)
        {
            Text = "远程桌面请求";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ShowInTaskbar = true;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(430, 234);
            BackColor = Color.FromArgb(11, 26, 42);
            ForeColor = Color.FromArgb(214, 234, 248);
            Font = new Font("Microsoft YaHei UI", 9F);

            var title = new Label
            {
                Text = "收到远程桌面请求",
                Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
                ForeColor = Color.FromArgb(34, 211, 238),
                AutoSize = true,
                Location = new Point(22, 18)
            };
            Controls.Add(title);

            var body = new Label
            {
                Text = "来自：" + fromIp + "\r\n\r\n同意后，本机将临时允许该 IP 连接远程桌面（3389），\r\n并自动开启远程桌面开关。仅放行此 IP，不影响其他终端。",
                AutoSize = false,
                Size = new Size(386, 108),
                Location = new Point(22, 54),
                ForeColor = Color.FromArgb(190, 214, 232)
            };
            Controls.Add(body);

            var countdown = new Label
            {
                Text = "",
                AutoSize = true,
                Location = new Point(22, 170),
                ForeColor = Color.FromArgb(120, 160, 190)
            };
            Controls.Add(countdown);

            var deny = new Button
            {
                Text = "拒绝",
                Size = new Size(96, 30),
                Location = new Point(206, 186),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 24, 34),
                ForeColor = Color.FromArgb(255, 170, 180),
                DialogResult = DialogResult.Cancel
            };
            deny.FlatAppearance.BorderColor = Color.FromArgb(150, 60, 75);
            Controls.Add(deny);
            CancelButton = deny;

            var allow = new Button
            {
                Text = "允许连接",
                Size = new Size(104, 30),
                Location = new Point(308, 186),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(22, 78, 62),
                ForeColor = Color.FromArgb(140, 240, 200),
                DialogResult = DialogResult.OK
            };
            allow.FlatAppearance.BorderColor = Color.FromArgb(40, 150, 110);
            Controls.Add(allow);
            AcceptButton = allow;

            var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += delegate
            {
                _left--;
                countdown.Text = _left > 0 ? (_left + " 秒后自动拒绝") : "已超时";
                if (_left <= 0)
                {
                    timer.Stop();
                    DialogResult = DialogResult.Cancel;
                    Close();
                }
            };
            timer.Start();
            FormClosed += delegate { timer.Stop(); timer.Dispose(); };

            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
        }
    }
}
