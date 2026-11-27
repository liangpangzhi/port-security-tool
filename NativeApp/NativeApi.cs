using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PortSecurityTool
{
    // ============================================================
    //  原生能力补充：把原来靠启动 cmd/powershell/wmic/netstat/tasklist
    //  才能拿到的信息改成本进程内直接计算，消除进程启动开销
    // ============================================================
    public partial class Bridge
    {
        // ---------- 窗口拖动 ----------
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 2;

        /// <summary>
        /// 由页面顶栏 mousedown 触发：交出鼠标捕获后交给系统做原生窗口拖动。
        /// 这样拖动跟手、无抖动，也不需要逐帧调用 MoveWindow。
        /// </summary>
        public void StartDrag()
        {
            try
            {
                ReleaseCapture();
                SendMessage(_form.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
            catch { }
        }

        // ---------- 系统信息 ----------
        /// <summary>系统名称 + 版本 + 内部版本号，读取注册表，毫秒级。</summary>
        public string OsCaption()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k == null) return Environment.OSVersion.VersionString;
                    string name = k.GetValue("ProductName") as string ?? "Windows";
                    string ver = k.GetValue("DisplayVersion") as string;
                    string build = k.GetValue("CurrentBuild") as string;
                    int b = 0;
                    if (build != null) int.TryParse(build, out b);
                    // Win11 的注册表 ProductName 仍写着 Windows 10，按内部版本号纠正
                    if (b >= 22000) name = name.Replace("Windows 10", "Windows 11");
                    var sb = new StringBuilder(name);
                    if (!string.IsNullOrEmpty(ver)) sb.Append(" ").Append(ver);
                    if (b > 0) sb.Append(" (").Append(b).Append(")");
                    return sb.ToString();
                }
            }
            catch { return Environment.OSVersion.VersionString; }
        }

        /// <summary>防火墙状态：1=三档全开，0=未全开，-1=读取失败（由调用方回退 netsh）。</summary>
        public int FwState()
        {
            string[] profiles = { "DomainProfile", "StandardProfile", "PublicProfile" };
            int read = 0, on = 0;
            foreach (var p in profiles)
            {
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\" + p))
                    {
                        if (k == null) continue;
                        object v = k.GetValue("EnableFirewall");
                        if (v == null) continue;
                        read++;
                        if (Convert.ToInt32(v) == 1) on++;
                    }
                }
                catch { }
            }
            if (read == 0) return -1;
            return (on == read) ? 1 : 0;
        }

        // ---------- 防火墙规则远程地址 ----------
        private static Dictionary<string, string> _ruleCache;
        private static DateTime _ruleCacheAt = DateTime.MinValue;

        private static Dictionary<string, string> LoadRules()
        {
            if (_ruleCache != null && (DateTime.UtcNow - _ruleCacheAt).TotalSeconds < 60)
                return _ruleCache;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules"))
                {
                    if (key == null) return map;
                    foreach (var vn in key.GetValueNames())
                    {
                        var s = key.GetValue(vn) as string;
                        if (string.IsNullOrEmpty(s)) continue;

                        string name = null, ra4 = null, ra6 = null, dir = null, active = null;
                        foreach (var tok in s.Split('|'))
                        {
                            int eq = tok.IndexOf('=');
                            if (eq <= 0) continue;
                            string k = tok.Substring(0, eq), v = tok.Substring(eq + 1);
                            switch (k)
                            {
                                case "Name": name = v; break;
                                case "RA4": ra4 = v; break;
                                case "RA6": ra6 = v; break;
                                case "Dir": dir = v; break;
                                case "Active": active = v; break;
                            }
                        }
                        if (string.IsNullOrEmpty(name)) continue;
                        if (dir != null && !dir.Equals("In", StringComparison.OrdinalIgnoreCase)) continue;
                        if (active != null && !active.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) continue;

                        var parts = new List<string>();
                        if (!string.IsNullOrEmpty(ra4) && ra4 != "*") parts.Add(ra4);
                        if (!string.IsNullOrEmpty(ra6) && ra6 != "*") parts.Add(ra6);
                        string val = parts.Count == 0 ? "ANY" : string.Join(",", parts.ToArray());

                        string old;
                        if (map.TryGetValue(name, out old))
                        {
                            if (old == "ANY" || val == "ANY") map[name] = "ANY";
                            else
                            {
                                foreach (var p in val.Split(','))
                                    if (!("," + old + ",").Contains("," + p + ",")) old += "," + p;
                                map[name] = old;
                            }
                        }
                        else map[name] = val;
                    }
                }
            }
            catch { }
            _ruleCache = map;
            _ruleCacheAt = DateTime.UtcNow;
            return map;
        }

        /// <summary>当前生效的阻断端口列表（逗号分隔），来自 Block_Port_&lt;port&gt;_TCP|UDP_in 规则。</summary>
        public string BlockedPorts()
        {
            try
            {
                var set = new List<string>();
                foreach (var k in LoadRules().Keys)
                {
                    if (!k.StartsWith("Block_Port_", StringComparison.OrdinalIgnoreCase)) continue;
                    string rest = k.Substring("Block_Port_".Length);
                    int us = rest.IndexOf('_');
                    string port = us > 0 ? rest.Substring(0, us) : rest;
                    int n;
                    if (int.TryParse(port, out n) && !set.Contains(port)) set.Add(port);
                }
                return string.Join(",", set.ToArray());
            }
            catch { return ""; }
        }

        /// <summary>当前生效的阻断端口集合（整数），供数据采集模块直接比对。</summary>
        public static HashSet<int> BlockedPortSet()
        {
            var set = new HashSet<int>();
            try
            {
                foreach (var k in LoadRules().Keys)
                {
                    if (!k.StartsWith("Block_Port_", StringComparison.OrdinalIgnoreCase)) continue;
                    string rest = k.Substring("Block_Port_".Length);
                    int us = rest.IndexOf('_');
                    string port = us > 0 ? rest.Substring(0, us) : rest;
                    int n;
                    if (int.TryParse(port, out n)) set.Add(n);
                }
            }
            catch { }
            return set;
        }

        /// <summary>规则被修改后调用，丢弃缓存，保证界面立即读到新值。</summary>
        public void InvalidateRules()
        {
            _ruleCache = null;
            _ruleCacheAt = DateTime.MinValue;
        }

        /// <summary>等价于 PowerShell 的 Get-NetFirewallAddressFilter().RemoteAddress，毫秒级。</summary>
        public string RuleIPs(string ruleName)
        {
            try
            {
                string v;
                if (LoadRules().TryGetValue(ruleName ?? "", out v)) return v;
                return "";
            }
            catch { return ""; }
        }

        // ---------- 进程名 ----------
        private static readonly Dictionary<int, string> _procCache = new Dictionary<int, string>();

        /// <summary>按 PID 取进程名，避免每个端口都启动一次 tasklist。</summary>
        public string ProcName(int pid)
        {
            if (pid <= 0) return "";
            if (pid == 4) return "System";
            string name;
            if (_procCache.TryGetValue(pid, out name)) return name;
            try
            {
                using (var p = Process.GetProcessById(pid))
                    name = p.ProcessName;
            }
            catch { name = ""; }
            _procCache[pid] = name;
            return name;
        }

        // ---------- 监听端口 ----------
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize,
            bool bOrder, int ulAf, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize,
            bool bOrder, int ulAf, int tableClass, uint reserved);

        private const int AF_INET = 2;
        private const int AF_INET6 = 23;
        private const int TCP_TABLE_OWNER_PID_LISTENER = 3;
        private const int UDP_TABLE_OWNER_PID = 1;
        private const int MIB_TCP_STATE_LISTEN = 2;

        private static byte[] QueryTable(bool tcp, int af)
        {
            int size = 8192;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    uint rc = tcp
                        ? GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_LISTENER, 0)
                        : GetExtendedUdpTable(buf, ref size, false, af, UDP_TABLE_OWNER_PID, 0);
                    if (rc == 0)
                    {
                        var data = new byte[size];
                        Marshal.Copy(buf, data, 0, size);
                        return data;
                    }
                    if (rc != 122) return null; // 122 = ERROR_INSUFFICIENT_BUFFER
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return null;
        }

        private static string FmtAddrV4(uint addr)
        {
            var b = BitConverter.GetBytes(addr);
            return b[0] + "." + b[1] + "." + b[2] + "." + b[3];
        }

        private static string FmtAddrV6(byte[] d, int off)
        {
            var a = new byte[16];
            Array.Copy(d, off, a, 0, 16);
            return "[" + new IPAddress(a).ToString() + "]";
        }

        private static int HostPort(uint netPort)
        {
            return (int)(((netPort & 0xFF) << 8) | ((netPort >> 8) & 0xFF));
        }

        /// <summary>
        /// 返回与 netstat -ano 同格式的文本（TCP 仅 LISTENING，UDP 全部），
        /// 让页面上的原有解析逻辑无需改动。
        /// </summary>
        public string Listeners()
        {
            var sb = new StringBuilder();

            // ---- TCP v4 ----
            var t4 = QueryTable(true, AF_INET);
            if (t4 != null)
            {
                int n = BitConverter.ToInt32(t4, 0);
                for (int i = 0; i < n; i++)
                {
                    int o = 4 + i * 24;
                    uint state = BitConverter.ToUInt32(t4, o);
                    if (state != MIB_TCP_STATE_LISTEN) continue;
                    uint la = BitConverter.ToUInt32(t4, o + 4);
                    uint lp = BitConverter.ToUInt32(t4, o + 8);
                    int pid = BitConverter.ToInt32(t4, o + 20);
                    sb.AppendFormat("  TCP    {0}:{1}    0.0.0.0:0    LISTENING    {2}\n",
                        FmtAddrV4(la), HostPort(lp), pid);
                }
            }

            // ---- TCP v6 ----
            var t6 = QueryTable(true, AF_INET6);
            if (t6 != null)
            {
                int n = BitConverter.ToInt32(t6, 0);
                for (int i = 0; i < n; i++)
                {
                    int o = 4 + i * 56;
                    uint state = BitConverter.ToUInt32(t6, o + 48);
                    if (state != MIB_TCP_STATE_LISTEN) continue;
                    uint lp = BitConverter.ToUInt32(t6, o + 20);
                    int pid = BitConverter.ToInt32(t6, o + 52);
                    sb.AppendFormat("  TCP    {0}:{1}    [::]:0    LISTENING    {2}\n",
                        FmtAddrV6(t6, o), HostPort(lp), pid);
                }
            }

            // ---- UDP v4 ----
            var u4 = QueryTable(false, AF_INET);
            if (u4 != null)
            {
                int n = BitConverter.ToInt32(u4, 0);
                for (int i = 0; i < n; i++)
                {
                    int o = 4 + i * 12;
                    uint la = BitConverter.ToUInt32(u4, o);
                    uint lp = BitConverter.ToUInt32(u4, o + 4);
                    int pid = BitConverter.ToInt32(u4, o + 8);
                    sb.AppendFormat("  UDP    {0}:{1}    *:*    *    {2}\n",
                        FmtAddrV4(la), HostPort(lp), pid);
                }
            }

            // ---- UDP v6 ----
            var u6 = QueryTable(false, AF_INET6);
            if (u6 != null)
            {
                int n = BitConverter.ToInt32(u6, 0);
                for (int i = 0; i < n; i++)
                {
                    int o = 4 + i * 28;
                    uint lp = BitConverter.ToUInt32(u6, o + 20);
                    int pid = BitConverter.ToInt32(u6, o + 24);
                    sb.AppendFormat("  UDP    {0}:{1}    *:*    *    {2}\n",
                        FmtAddrV6(u6, o), HostPort(lp), pid);
                }
            }

            return sb.ToString();
        }
    }
}
