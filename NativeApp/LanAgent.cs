using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PortSecurityTool
{
    // ============================================================
    //  局域网代理：让同网段的服务端可以「一键扫描 + 定时拉取」
    //
    //  UDP 18762  收到 PST-DISCOVER-V1 广播后回一台主机的身份信息
    //  TCP 18762  GET /pst/v1/ping    轻量健康检查（免令牌）
    //             GET /pst/v1/device 完整采集报文（需令牌，?refresh=1 强制重采）
    //
    //  不依赖 HttpListener / URL ACL，直接用 TcpListener 绑定 0.0.0.0，
    //  管理员运行时无需额外注册 URL 保留即可在局域网内提供读取接口。
    // ============================================================
    internal static class LanAgent
    {
        public const int DefaultPort = 18762;
        public const string Magic = "PST-DISCOVER-V1";

        private static readonly object _lock = new object();
        private static UdpClient _udp;
        private static TcpListener _tcp;
        private static Thread _udpThread;
        private static Thread _tcpThread;
        private static volatile bool _running;
        private static int _port;

        private static Collector.Snapshot _snap;
        private static DateTime _snapAt = DateTime.MinValue;

        public static bool Running { get { return _running; } }

        /// <summary>默认开启：只要本工具在跑，服务端就能扫到并拉取数据。</summary>
        public static bool Enabled { get { return AppConfig.GetBool("api.lan.enabled", true); } }

        public static int Port
        {
            get
            {
                int p = AppConfig.GetInt("api.lan.port", DefaultPort);
                if (p < 1024 || p > 65535) p = DefaultPort;
                return p;
            }
        }

        /// <summary>与「本地服务」共用访问令牌；服务端「客户端拉取令牌」需填同一个值。</summary>
        public static string Token { get { return AppConfig.Unprotect(AppConfig.Get("api.local.token", "")); } }

        public static string BaseUrl
        {
            get { return "http://" + LanIp() + ":" + (_running && _port > 0 ? _port : Port); }
        }

        // ---------------- 启停 ----------------

        public static string Start()
        {
            lock (_lock)
            {
                if (_running) return "";
                int port = Port;
                UdpClient udp = null;
                TcpListener tcp = null;
                try
                {
                    udp = new UdpClient(port);
                    udp.EnableBroadcast = true;
                    tcp = new TcpListener(IPAddress.Any, port);
                    tcp.Start();

                    _udp = udp;
                    _tcp = tcp;
                    _port = port;
                    _running = true;

                    _udpThread = new Thread(UdpLoop) { IsBackground = true, Name = "PstLanDiscover" };
                    _tcpThread = new Thread(TcpLoop) { IsBackground = true, Name = "PstLanAgent" };
                    _udpThread.Start();
                    _tcpThread.Start();

                    // 自动放行入站端口，避免系统防火墙拦掉服务端的发现与拉取
                    try
                    {
                        var t = new Thread(new ThreadStart(delegate { try { AllowFirewall(); } catch { } }))
                        { IsBackground = true, Name = "PstLanFw" };
                        t.Start();
                    }
                    catch { }
                    return "";
                }
                catch (Exception ex)
                {
                    _running = false;
                    try { if (udp != null) udp.Close(); } catch { }
                    try { if (tcp != null) tcp.Stop(); } catch { }
                    _udp = null;
                    _tcp = null;
                    _port = 0;
                    string hint = "";
                    if (ex is SocketException && ((SocketException)ex).ErrorCode == 10048)
                        hint = "（端口 " + port + " 已被占用，请换一个端口）";
                    return "局域网代理启动失败：" + ex.Message + hint;
                }
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                _running = false;
                try { if (_udp != null) _udp.Close(); } catch { }
                try { if (_tcp != null) _tcp.Stop(); } catch { }
                _udp = null;
                _tcp = null;
                _port = 0;
            }
        }

        public static void ApplyConfig()
        {
            if (Enabled) { if (!_running) Start(); }
            else Stop();
        }

        /// <summary>为发现/拉取端口添加入站放行规则（程序本身以管理员身份运行）。</summary>
        public static string AllowFirewall()
        {
            try
            {
                int p = Port;
                RunNetsh("netsh advfirewall firewall delete rule name=\"PST_LAN_Agent_UDP\" >nul 2>&1");
                RunNetsh("netsh advfirewall firewall add rule name=\"PST_LAN_Agent_UDP\" dir=in action=allow protocol=UDP localport=" + p + " >nul 2>&1");
                RunNetsh("netsh advfirewall firewall delete rule name=\"PST_LAN_Agent_TCP\" >nul 2>&1");
                RunNetsh("netsh advfirewall firewall add rule name=\"PST_LAN_Agent_TCP\" dir=in action=allow protocol=TCP localport=" + p + " >nul 2>&1");
                return "OK|已放行 UDP/TCP " + p + " 端口的入站连接。";
            }
            catch (Exception ex) { return "ERR|防火墙放行失败：" + ex.Message; }
        }

        private static void RunNetsh(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psi))
                {
                    if (p != null) p.WaitForExit(8000);
                }
            }
            catch { }
        }

        // ---------------- UDP 发现 ----------------

        private static void UdpLoop()
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] data;
                try { data = _udp.Receive(ref ep); }
                catch { break; }

                try
                {
                    string text = Encoding.UTF8.GetString(data);
                    if (text.IndexOf(Magic, StringComparison.Ordinal) < 0) continue;
                    var bytes = Encoding.UTF8.GetBytes(DiscoveryJson());
                    _udp.Send(bytes, bytes.Length, ep);
                }
                catch { }
            }
        }

        private static string DiscoveryJson()
        {
            string computer = Environment.MachineName;
            string deviceId = "";
            string user = "";
            try
            {
                var s = Snap();
                if (s != null)
                {
                    computer = string.IsNullOrEmpty(s.Computer) ? computer : s.Computer;
                    deviceId = ApiReport.DeviceId(s);
                    user = s.User ?? "";
                }
            }
            catch { }

            return new JObj()
                .Put("pst", "pst-agent")
                .Put("v", "1")
                .Put("ip", LanIp())
                .PutNum("port", _running && _port > 0 ? _port : Port)
                .Put("name", computer)
                .Put("computer", computer)
                .Put("deviceId", deviceId)
                .Put("user", user)
                .Put("version", ApiReport.AgentVersionText)
                .PutBool("token", Token.Length > 0)
                .ToJson(false);
        }

        // ---------------- TCP 读取 ----------------

        private static void TcpLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _tcp.AcceptTcpClient(); }
                catch { break; }
                try { ThreadPool.QueueUserWorkItem(delegate { HandleClient(client); }); }
                catch { try { client.Close(); } catch { } }
            }
        }

        private static void HandleClient(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 6000;
                client.SendTimeout = 6000;
                using (client)
                using (var ns = client.GetStream())
                {
                    var ms = new System.IO.MemoryStream();
                    var buf = new byte[2048];
                    int n;
                    while (ms.Length < 16384)
                    {
                        n = ns.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        ms.Write(buf, 0, n);
                        var head = Encoding.ASCII.GetString(ms.ToArray());
                        if (head.IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0) break;
                    }

                    var request = Encoding.UTF8.GetString(ms.ToArray());
                    int end = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (end < 0) { WriteHttp(ns, 400, Err("bad_request", "请求头不完整")); return; }

                    var lines = request.Substring(0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
                    if (lines.Length == 0) { WriteHttp(ns, 400, Err("bad_request", "空请求")); return; }
                    var first = lines[0].Split(' ');
                    string method = first.Length > 0 ? first[0].ToUpperInvariant() : "GET";
                    string target = first.Length > 1 ? first[1] : "/";
                    int q = target.IndexOf('?');
                    string path = (q < 0 ? target : target.Substring(0, q)).TrimEnd('/').ToLowerInvariant();
                    string query = q < 0 ? "" : target.Substring(q + 1);

                    if (method != "GET" && method != "HEAD")
                    {
                        WriteHttp(ns, 405, Err("method_not_allowed", "仅支持 GET"));
                        return;
                    }

                    switch (path)
                    {
                        case "/pst/v1/ping":
                            WriteHttp(ns, 200, PingJson());
                            return;

                        case "/pst/v1/health":
                            WriteHttp(ns, 200, new JObj()
                                .Put("status", "ok")
                                .Put("pst", "pst-agent")
                                .Put("version", ApiReport.AgentVersionText)
                                .Put("computer", Environment.MachineName)
                                .ToJson(true));
                            return;

                        case "/pst/v1/device":
                            {
                                if (!Authorized(lines, query))
                                {
                                    WriteHttp(ns, 401, Err("unauthorized", "缺少或错误的 X-Api-Token"));
                                    return;
                                }
                                bool refresh = query.IndexOf("refresh=1", StringComparison.Ordinal) >= 0;
                                var s = Snap(refresh);
                                string reportId = "poll-" + Guid.NewGuid().ToString("N");
                                WriteHttp(ns, 200, ApiReport.ToJson(s, reportId, true));
                                return;
                            }

                        default:
                            WriteHttp(ns, 404, Err("not_found", "未知接口：" + path));
                            return;
                    }
                }
            }
            catch { }
        }

        private static string PingJson()
        {
            string computer = Environment.MachineName;
            string deviceId = "";
            try
            {
                var s = Snap();
                if (s != null)
                {
                    computer = string.IsNullOrEmpty(s.Computer) ? computer : s.Computer;
                    deviceId = ApiReport.DeviceId(s);
                }
            }
            catch { }
            return new JObj()
                .Put("pst", "pst-agent")
                .Put("name", computer)
                .Put("computer", computer)
                .Put("deviceId", deviceId)
                .Put("version", ApiReport.AgentVersionText)
                .PutBool("token", Token.Length > 0)
                .PutNum("port", _running && _port > 0 ? _port : Port)
                .ToJson(false);
        }

        private static bool Authorized(string[] lines, string query)
        {
            string token = Token;
            if (string.IsNullOrEmpty(token)) return true;
            string given = "";
            foreach (var line in lines)
            {
                if (line.StartsWith("X-Api-Token:", StringComparison.OrdinalIgnoreCase))
                    given = line.Substring(line.IndexOf(':') + 1).Trim();
                else if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                {
                    var v = line.Substring(line.IndexOf(':') + 1).Trim();
                    if (v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) given = v.Substring(7).Trim();
                }
            }
            if (string.IsNullOrEmpty(given) && query.Length > 0)
            {
                foreach (var part in query.Split('&'))
                {
                    if (part.StartsWith("token=", StringComparison.OrdinalIgnoreCase))
                    { given = Uri.UnescapeDataString(part.Substring(6)); break; }
                }
            }
            return string.Equals(given, token, StringComparison.Ordinal);
        }

        private static void WriteHttp(NetworkStream ns, int status, string json)
        {
            var body = new UTF8Encoding(false).GetBytes(json ?? "{}");
            string reason = status == 200 ? "OK" : status == 401 ? "Unauthorized"
                : status == 404 ? "Not Found" : status == 405 ? "Method Not Allowed" : "Error";
            var head = "HTTP/1.1 " + status + " " + reason + "\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + "Access-Control-Allow-Origin: *\r\n"
                + "Cache-Control: no-store\r\n"
                + "Content-Length: " + body.Length + "\r\n"
                + "Connection: close\r\n\r\n";
            var hb = Encoding.ASCII.GetBytes(head);
            ns.Write(hb, 0, hb.Length);
            ns.Write(body, 0, body.Length);
            ns.Flush();
        }

        private static string Err(string code, string message)
        {
            return new JObj().PutBool("ok", false).Put("code", code).Put("message", message).ToJson(false);
        }

        // ---------------- 数据 ----------------

        private static Collector.Snapshot Snap(bool refresh = false)
        {
            lock (_lock)
            {
                if (!refresh && _snap != null && (DateTime.Now - _snapAt).TotalSeconds < 60)
                    return _snap;
                _snap = Collector.Collect();
                _snapAt = DateTime.Now;
                return _snap;
            }
        }

        /// <summary>自动上传定时器调用：立刻重新采集并刷新缓存，服务端随时可取到最新数据。</summary>
        public static string RefreshCache()
        {
            try
            {
                lock (_lock)
                {
                    _snap = Collector.Collect();
                    _snapAt = DateTime.Now;
                    return "OK|采集完成";
                }
            }
            catch (Exception ex) { return "ERR|采集异常：" + ex.Message; }
        }

        /// <summary>缓存的采集时间，供界面显示「上次上传时间」。</summary>
        public static string CachedAt
        {
            get { lock (_lock) { return _snap == null ? "" : _snapAt.ToString("yyyy-MM-dd HH:mm:ss"); } }
        }

        private static string LanIp()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var ip = ua.Address.ToString();
                        if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                        return ip;
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }
    }
}
