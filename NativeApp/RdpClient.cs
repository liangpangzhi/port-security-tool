using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PortSecurityTool
{
    // ============================================================
    //  远程桌面客户端（请求方）
    //
    //  · Connect：生成 .rdp 连接配置并拉起 mstsc（可直接连 3389 已开放的主机）
    //  · Request：向目标的本工具代理发起远程桌面请求，由目标用户确认后临时放行
    //  · Close  ：让目标立即恢复 3389 封堵
    // ============================================================
    internal static class RdpClient
    {
        private static readonly object _lock = new object();
        private static int _state;          // 0 空闲 / 1 等待中 / 2 成功 / 3 失败
        private static string _message = "";
        private static string _target = "";

        // ---------------- 配置 ----------------

        /// <summary>是否正在等待目标确认（供界面刷新用）。</summary>
        public static bool Pending { get { lock (_lock) { return _state == 1; } } }

        public static string Token { get { return AppConfig.Unprotect(AppConfig.Get("rdp.agent.token", "")); } }
        public static string LastUser { get { return AppConfig.Get("rdp.user", ""); } }
        public static int Width { get { return Clamp(AppConfig.GetInt("rdp.width", 1600), 800, 3840); } }
        public static int Height { get { return Clamp(AppConfig.GetInt("rdp.height", 900), 600, 2160); } }
        public static bool Clipboard { get { return AppConfig.GetBool("rdp.clip", true); } }
        public static bool Drives { get { return AppConfig.GetBool("rdp.drives", true); } }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        // ---------------- 远程请求（异步，界面轮询） ----------------

        /// <summary>向目标发起远程桌面请求；立即返回，结果由 ReqState() 轮询。</summary>
        public static string Request(string ip)
        {
            ip = (ip ?? "").Trim();
            if (ip.Length == 0) return "ERR|请先选择目标主机";
            var token = Token;
            if (token.Length == 0) return "ERR|未设置远程请求令牌，请先在「远程请求」中填写与目标一致的令牌";

            lock (_lock)
            {
                if (_state == 1) return "ERR|上一个请求还在等待目标确认";
                _state = 1;
                _message = "";
                _target = ip;
            }

            Task.Run(delegate
            {
                int status; string body;
                bool ok = HttpToAgent(ip, "POST", "/pst/v1/rdp-request", token, 40000, out status, out body);
                string msg;
                bool allowed = false;
                if (!ok)
                {
                    msg = "目标无响应（未安装本工具或未开启「响应远程桌面请求」）";
                }
                else if (status == 403)
                {
                    msg = "目标拒绝：访问令牌不一致";
                }
                else if (status != 200)
                {
                    msg = "目标返回 HTTP " + status;
                }
                else
                {
                    allowed = NetDiscovery.Pick(body, "allowed") == "1";
                    msg = NetDiscovery.Pick(body, "msg");
                    if (msg.Length == 0) msg = allowed ? "目标已允许连接" : "目标未允许";
                }

                lock (_lock)
                {
                    _state = allowed ? 2 : 3;
                    _message = msg;
                }
            });

            return "OK|请求已发送至 " + ip + "，等待对方确认…";
        }

        /// <summary>"0|" 空闲 / "1|ip" 等待中 / "2|消息" 允许 / "3|消息" 拒绝或失败。</summary>
        public static string ReqState()
        {
            lock (_lock)
            {
                return _state + "|" + (_state == 1 ? _target : _message);
            }
        }

        public static void ResetState()
        {
            lock (_lock) { _state = 0; _message = ""; _target = ""; }
        }

        /// <summary>让目标立即恢复 3389 封堵。</summary>
        public static string Close(string ip)
        {
            ip = (ip ?? "").Trim();
            if (ip.Length == 0) return "ERR|请先选择目标主机";
            var token = Token;
            if (token.Length == 0) return "ERR|未设置远程请求令牌";

            int status; string body;
            if (!HttpToAgent(ip, "POST", "/pst/v1/rdp-close", token, 12000, out status, out body))
                return "ERR|目标无响应";
            if (status == 403) return "ERR|令牌不一致，无法操作目标";
            if (status != 200) return "ERR|目标返回 HTTP " + status;
            var msg = NetDiscovery.Pick(body, "msg");
            return "OK|" + (msg.Length > 0 ? msg : "已通知目标恢复封堵");
        }

        // ---------------- 发起连接 ----------------

        public static string Connect(string ip, string user, bool clipboard, bool drives, int width, int height)
        {
            ip = (ip ?? "").Trim();
            if (ip.Length == 0) return "ERR|请先选择目标主机";

            try
            {
                AppConfig.Set("rdp.user", user ?? "");
                AppConfig.SetInt("rdp.width", Clamp(width <= 0 ? 1600 : width, 800, 3840));
                AppConfig.SetInt("rdp.height", Clamp(height <= 0 ? 900 : height, 600, 2160));
                AppConfig.SetBool("rdp.clip", clipboard);
                AppConfig.SetBool("rdp.drives", drives);

                int w = Clamp(width <= 0 ? 1600 : width, 800, 3840);
                int h = Clamp(height <= 0 ? 900 : height, 600, 2160);

                var sb = new StringBuilder();
                sb.AppendLine("screen mode id:i:1");
                sb.AppendLine("use multimon:i:0");
                sb.AppendLine("desktopwidth:i:" + w);
                sb.AppendLine("desktopheight:i:" + h);
                sb.AppendLine("session bpp:i:32");
                sb.AppendLine("full address:s:" + ip + ":3389");
                sb.AppendLine("prompt for credentials:i:1");
                sb.AppendLine("authentication level:i:2");
                sb.AppendLine("negotiate security layer:i:1");
                sb.AppendLine("redirectclipboard:i:" + (clipboard ? "1" : "0"));
                sb.AppendLine("redirectprinters:i:1");
                sb.AppendLine("redirectcomports:i:0");
                sb.AppendLine("redirectsmartcards:i:" + (drives ? "1" : "0"));
                sb.AppendLine("drivestoredirect:s:" + (drives ? "*" : ""));
                sb.AppendLine("audiomode:i:0");
                sb.AppendLine("disable wallpaper:i:1");
                sb.AppendLine("disable full window drag:i:1");
                sb.AppendLine("disable menu anims:i:1");
                sb.AppendLine("disable themes:i:1");
                sb.AppendLine("autoreconnection enabled:i:1");
                if (!string.IsNullOrWhiteSpace(user)) sb.AppendLine("username:s:" + user.Trim());

                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PortSecurityTool", "rdp");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, ip.Replace(':', '_') + ".rdp");
                // mstsc 对 Unicode 配置文件支持良好，用 UTF-16 保证中文用户名不乱码
                File.WriteAllText(file, sb.ToString(), Encoding.Unicode);

                Process.Start(new ProcessStartInfo("mstsc.exe", "\"" + file + "\"") { UseShellExecute = true });
                return "OK|已启动远程桌面：" + ip;
            }
            catch (Exception ex)
            {
                return "ERR|启动远程桌面失败：" + ex.Message;
            }
        }

        // ---------------- 极简 HTTP 客户端 ----------------

        private static bool HttpToAgent(string ip, string method, string path, string token,
                                        int timeoutMs, out int status, out string body)
        {
            status = 0; body = "";
            int port = RdpAgent.Port;
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(ip, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(2500)) return false;
                    c.EndConnect(ar);
                    c.ReceiveTimeout = timeoutMs;

                    var req = method + " " + path + " HTTP/1.1\r\n"
                            + "Host: " + ip + ":" + port + "\r\n"
                            + "X-Pst-Token: " + token + "\r\n"
                            + "Content-Length: 0\r\n"
                            + "Connection: close\r\n\r\n";
                    var bytes = Encoding.ASCII.GetBytes(req);
                    using (var ns = c.GetStream())
                    {
                        ns.Write(bytes, 0, bytes.Length);
                        ns.Flush();

                        var ms = new MemoryStream();
                        var buf = new byte[2048];
                        var sw = Stopwatch.StartNew();
                        while (sw.ElapsedMilliseconds < timeoutMs)
                        {
                            int n;
                            try { n = ns.Read(buf, 0, buf.Length); }
                            catch { break; }
                            if (n <= 0) break;
                            ms.Write(buf, 0, n);
                            if (ms.Length > 16384) break;
                        }
                        var text = Encoding.UTF8.GetString(ms.ToArray());
                        int sp = text.IndexOf(' ');
                        if (text.StartsWith("HTTP/1.", StringComparison.Ordinal) && sp > 0)
                        {
                            int sp2 = text.IndexOf(' ', sp + 1);
                            var code = sp2 > 0 ? text.Substring(sp + 1, sp2 - sp - 1) : "0";
                            int.TryParse(code, out status);
                        }
                        int sep = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        body = sep > 0 ? text.Substring(sep + 4) : "";
                        return status > 0;
                    }
                }
            }
            catch { return false; }
        }
    }
}
