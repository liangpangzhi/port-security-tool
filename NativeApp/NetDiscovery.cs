using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PortSecurityTool
{
    // ============================================================
    //  局域网发现
    //
    //  做法：ARP 判活取 MAC + 并行 TCP 探测（3389 远程桌面 / 18761 本工具代理），
    //  命中的主机再做反向名称解析；装了本工具的主机直接从代理接口取计算机名与用户，
    //  比反向 DNS 更准。全程不依赖外部进程，254 个地址约 3~6 秒。
    // ============================================================
    internal static class NetDiscovery
    {
        public sealed class HostInfo
        {
            public string Ip = "";
            public string Mac = "";
            public string Name = "";
            public string User = "";
            public bool RdpOpen;        // 3389 端口可连
            public bool AgentOnline;    // 本工具代理在线
            public bool RdpEnabled;     // 目标系统层面允许远程桌面
            public int Latency = -1;
        }

        private static readonly object _lock = new object();
        private static List<HostInfo> _results = new List<HostInfo>();
        private static volatile bool _scanning;
        private static volatile bool _cancel;
        private static volatile int _done;
        private static volatile int _total;
        private static string _label = "";
        private static DateTime _startedAt;

        public static bool Scanning { get { return _scanning; } }

        // ---------------- 网段识别 ----------------

        /// <summary>本机所有 IPv4 网段（按掩码截断到前缀长度），形如 192.168.1.0/24。</summary>
        public static List<string> LocalCidrs()
        {
            var list = new List<string>();
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
                        if (ip.StartsWith("169.254.")) continue;      // 自动私有地址，不是真实网段
                        int prefix = ua.PrefixLength;
                        if (prefix < 8 || prefix > 30) prefix = 24;
                        uint uip = IpToUint(ip);
                        uint mask = prefix == 0 ? 0 : 0xFFFFFFFFu << (32 - prefix);
                        uint net = uip & mask;
                        var cidr = UintToIp(net) + "/" + prefix;
                        if (!list.Contains(cidr)) list.Add(cidr);
                    }
                }
            }
            catch { }
            return list;
        }

        public static string DefaultCidr()
        {
            var l = LocalCidrs();
            return l.Count > 0 ? l[0] : "192.168.1.0/24";
        }

        // ---------------- 地址范围解析 ----------------

        private static bool ParseRange(string spec, out uint start, out uint end, out string label)
        {
            start = end = 0; label = "";
            var s = (spec ?? "").Trim().Replace(" ", "");
            if (s.Length == 0) return false;

            try
            {
                // 192.168.1.0/24
                int slash = s.IndexOf('/');
                if (slash > 0)
                {
                    uint ip;
                    if (!TryIpToUint(s.Substring(0, slash), out ip)) return false;
                    int prefix;
                    if (!int.TryParse(s.Substring(slash + 1), out prefix)) return false;
                    if (prefix < 8) prefix = 24;
                    if (prefix > 32) prefix = 32;
                    uint mask = prefix == 32 ? 0xFFFFFFFFu : (0xFFFFFFFFu << (32 - prefix));
                    start = ip & mask;
                    end = start | (~mask);
                    if (prefix <= 30) { start += 1; end -= 1; }   // 去掉网络号与广播
                    label = UintToIp(start) + " ~ " + UintToIp(end);
                    return true;
                }

                // 192.168.1.1-192.168.1.50 或 192.168.1.1-50
                int dash = s.IndexOf('-');
                if (dash > 0)
                {
                    var a = s.Substring(0, dash);
                    var b = s.Substring(dash + 1);
                    if (!TryIpToUint(a, out start)) return false;
                    if (b.IndexOf('.') < 0)
                    {
                        uint baseIp;
                        if (!TryIpToUint(a, out baseIp)) return false;
                        int last;
                        if (!int.TryParse(b, out last) || last < 0 || last > 255) return false;
                        end = (baseIp & 0xFFFFFF00u) | (uint)last;
                    }
                    else if (!TryIpToUint(b, out end)) return false;
                    if (end < start) { var t = start; start = end; end = t; }
                    label = UintToIp(start) + " ~ " + UintToIp(end);
                    return true;
                }

                // 单个地址
                uint one;
                if (!TryIpToUint(s, out one)) return false;
                start = end = one;
                label = s;
                return true;
            }
            catch { return false; }
        }

        // ---------------- 扫描 ----------------

        /// <summary>启动后台扫描。返回 "OK|说明" 或 "ERR|原因"。</summary>
        public static string StartScan(string spec)
        {
            if (_scanning) return "ERR|扫描已在进行中";
            uint start, end;
            string label;
            if (!ParseRange(spec, out start, out end, out label))
                return "ERR|网段格式不正确，示例：192.168.1.0/24 或 192.168.1.1-254";

            long count = (long)end - (long)start + 1;
            if (count <= 0) return "ERR|地址范围为空";
            if (count > 1024) return "ERR|一次最多扫描 1024 个地址，请缩小范围";

            lock (_lock) { _results = new List<HostInfo>(); }
            _done = 0;
            _total = (int)count;
            _label = label;
            _cancel = false;
            _scanning = true;
            _startedAt = DateTime.Now;

            Task.Run(delegate
            {
                try { ScanCore(start, (int)count); }
                catch { }
                finally { _scanning = false; }
            });
            return "OK|开始扫描 " + label + "，共 " + count + " 个地址";
        }

        public static void Stop() { _cancel = true; }

        private static void ScanCore(uint start, int count)
        {
            int agentPort = RdpAgent.Port;
            // 用独立线程而不是 Parallel.For：探测以等待网络为主，线程池爬坡会让首轮并发上不去
            int workers = Math.Min(96, Math.Max(8, count));
            int next = -1;
            var threads = new List<Thread>(workers);
            for (int w = 0; w < workers; w++)
            {
                var t = new Thread(delegate ()
                {
                    while (!_cancel)
                    {
                        int i = Interlocked.Increment(ref next);
                        if (i >= count) break;
                        uint addr = start + (uint)i;
                        byte last = (byte)(addr & 0xFF);
                        if (last == 0 || last >= 224) { Interlocked.Increment(ref _done); continue; }  // 网络号/广播/多播不是主机
                        var ip = UintToIp(addr);
                        try
                        {
                            var h = Probe(ip, agentPort);
                            if (h != null) { lock (_lock) { _results.Add(h); } }
                        }
                        catch { }
                        Interlocked.Increment(ref _done);
                    }
                });
                t.IsBackground = true;
                t.Name = "PstScan" + w;
                t.Start();
                threads.Add(t);
            }
            foreach (var t in threads) { try { t.Join(); } catch { } }

            // 排好序、补名称（反向 DNS 放到最后，避免扫描阶段被 DNS 拖慢）
            List<HostInfo> snapshot;
            lock (_lock) { snapshot = new List<HostInfo>(_results); }
            snapshot.Sort(delegate (HostInfo a, HostInfo b) { return IpToUint(a.Ip).CompareTo(IpToUint(b.Ip)); });
            var need = new List<HostInfo>();
            foreach (var h in snapshot) if (h.Name.Length == 0) need.Add(h);
            if (need.Count > 0)
            {
                // 名称解析并发做，否则十来台机器串行等待会把总耗时拖长
                Parallel.For(0, need.Count, new ParallelOptions { MaxDegreeOfParallelism = 24 }, delegate (int i)
                {
                    try { need[i].Name = ReverseName(need[i].Ip); } catch { }
                });
            }
            lock (_lock) { _results = snapshot; }
            _done = _total;   // 中途取消时也让进度条走到头
        }

        private static HostInfo Probe(string ip, int agentPort)
        {
            var h = new HostInfo { Ip = ip };

            uint uip;
            if (TryIpToUint(ip, out uip))
            {
                string mac;
                if (SendArp(uip, out mac)) h.Mac = mac;
            }

            int ms;
            bool rdp = TcpProbe(ip, 3389, 320, out ms);
            if (rdp) { h.RdpOpen = true; h.Latency = ms; }

            string name, user;
            bool enabled;
            bool agent = AgentPing(ip, agentPort, out name, out user, out enabled);
            if (agent)
            {
                h.AgentOnline = true;
                h.RdpEnabled = enabled;
                h.Name = name;
                h.User = user;
            }

            if (!rdp && !agent && h.Mac.Length == 0) return null;   // 完全没响应
            return h;
        }

        private static string ReverseName(string ip)
        {
            try
            {
                var t = Task.Run(delegate { return Dns.GetHostEntry(ip).HostName; });
                if (t.Wait(700) && t.Result != null) return t.Result;
            }
            catch { }
            return "";
        }

        // ---------------- 结果输出 ----------------

        /// <summary>"扫描中|百分比|在线数|范围"，供界面轮询。</summary>
        public static string State()
        {
            int total = _total, done = _done;
            int pct = total <= 0 ? ( _scanning ? 0 : 100) : (int)(done * 100L / total);
            if (!_scanning && done >= total) pct = 100;
            int online;
            lock (_lock) { online = _results.Count; }
            return (_scanning ? "1" : "0") + "|" + pct + "|" + online + "|" + _label;
        }

        /// <summary>结果行：ip \t 名称 \t MAC \t rdp \t agent \t enabled \t 用户 \t 延迟；行间 \u0001。</summary>
        public static string Rows()
        {
            List<HostInfo> list;
            lock (_lock) { list = new List<HostInfo>(_results); }
            var sb = new StringBuilder();
            foreach (var h in list)
            {
                if (sb.Length > 0) sb.Append('\u0001');
                sb.Append(h.Ip).Append('\t')
                  .Append(h.Name.Length > 0 ? h.Name : "—").Append('\t')
                  .Append(h.Mac.Length > 0 ? h.Mac : "—").Append('\t')
                  .Append(h.RdpOpen ? "1" : "0").Append('\t')
                  .Append(h.AgentOnline ? "1" : "0").Append('\t')
                  .Append(h.RdpEnabled ? "1" : "0").Append('\t')
                  .Append(h.User.Length > 0 ? h.User : "—").Append('\t')
                  .Append(h.Latency >= 0 ? h.Latency.ToString() : "—");
            }
            return sb.ToString();
        }

        // ---------------- 底层探测 ----------------

        private static bool SendArp(uint ip, out string mac)
        {
            mac = "";
            try
            {
                var buf = new byte[6];
                int len = buf.Length;
                int rc = SendARP(ip, 0, buf, ref len);
                if (rc != 0 || len < 6) return false;
                if (buf[0] == 0 && buf[1] == 0 && buf[2] == 0 && buf[3] == 0 && buf[4] == 0 && buf[5] == 0) return false;
                var sb = new StringBuilder();
                for (int i = 0; i < 6; i++)
                {
                    if (sb.Length > 0) sb.Append(':');
                    sb.Append(buf[i].ToString("X2"));
                }
                mac = sb.ToString();
                return true;
            }
            catch { return false; }
        }

        internal static bool TcpProbe(string ip, int port, int timeoutMs, out int ms)
        {
            ms = -1;
            var sw = Stopwatch.StartNew();
            TcpClient c = null;
            try
            {
                c = new TcpClient();
                var ar = c.BeginConnect(ip, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                c.EndConnect(ar);
                ms = (int)sw.ElapsedMilliseconds;
                return true;
            }
            catch { return false; }
            finally
            {
                try { if (c != null) c.Close(); } catch { }
            }
        }

        /// <summary>向目标的本工具代理要一份基本信息（免令牌，只回名称/用户/RDP 状态）。</summary>
        private static bool AgentPing(string ip, int port, out string name, out string user, out bool rdpEnabled)
        {
            name = ""; user = ""; rdpEnabled = false;
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(ip, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(300)) return false;
                    c.EndConnect(ar);
                    c.ReceiveTimeout = 800;

                    var req = "GET /pst/v1/ping HTTP/1.1\r\nHost: " + ip + "\r\nConnection: close\r\n\r\n";
                    var bytes = Encoding.ASCII.GetBytes(req);
                    using (var ns = c.GetStream())
                    {
                        ns.Write(bytes, 0, bytes.Length);
                        ns.Flush();
                        var ms = new System.IO.MemoryStream();
                        var buf = new byte[1024];
                        int n;
                        while ((n = ns.Read(buf, 0, buf.Length)) > 0)
                        {
                            ms.Write(buf, 0, n);
                            if (ms.Length > 8192) break;
                        }
                        var text = Encoding.UTF8.GetString(ms.ToArray());
                        if (text.IndexOf("\"pst\"", StringComparison.Ordinal) < 0) return false;
                        name = Pick(text, "name");
                        user = Pick(text, "user");
                        rdpEnabled = Pick(text, "rdp") == "1" || Pick(text, "rdp") == "true";
                        return true;
                    }
                }
            }
            catch { return false; }
        }

        /// <summary>从响应文本里抠出 "key":"value" / "key":1 的值（轻量，够用即可）。</summary>
        internal static string Pick(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";
            var token = "\"" + key + "\":";
            int i = json.IndexOf(token, StringComparison.Ordinal);
            if (i < 0) return "";
            i += token.Length;
            while (i < json.Length && json[i] == ' ') i++;
            if (i >= json.Length) return "";
            if (json[i] == '"')
            {
                int j = json.IndexOf('"', i + 1);
                return j < 0 ? "" : json.Substring(i + 1, j - i - 1);
            }
            int k = i;
            while (k < json.Length && json[k] != ',' && json[k] != '}' && json[k] != '\r' && json[k] != '\n') k++;
            return json.Substring(i, k - i).Trim();
        }

        // ---------------- IP 工具 ----------------

        internal static uint IpToUint(string ip)
        {
            uint v;
            return TryIpToUint(ip, out v) ? v : 0;
        }

        internal static bool TryIpToUint(string ip, out uint v)
        {
            v = 0;
            IPAddress a;
            if (!IPAddress.TryParse(ip, out a)) return false;
            var b = a.GetAddressBytes();
            if (b.Length != 4) return false;
            v = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            return true;
        }

        internal static string UintToIp(uint v)
        {
            return ((v >> 24) & 0xFF) + "." + ((v >> 16) & 0xFF) + "." + ((v >> 8) & 0xFF) + "." + (v & 0xFF);
        }

        [DllImport("iphlpapi.dll")]
        private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref int phyAddrLen);
    }
}
