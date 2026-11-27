using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PortSecurityTool
{
    /// <summary>
    /// 本机硬件与系统信息采集。全部走注册表 / Win32 API / .NET 自带类，
    /// 不依赖 WMI 与外部进程，单次采集耗时通常在毫秒级。
    /// </summary>
    internal static class SysInfo
    {
        // ---------------- 内存 ----------------
        [StructLayout(LayoutKind.Sequential)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        public static double MemoryTotalGB()
        {
            try
            {
                var m = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(m)) return Math.Round(m.ullTotalPhys / 1024.0 / 1024 / 1024, 2);
            }
            catch { }
            return 0;
        }

        // ---------------- 注册表读取助手 ----------------
        private static string R(string hive, string path, string name)
        {
            try
            {
                var v = Registry.GetValue(hive + "\\" + path, name, null);
                if (v == null) return "";
                if (v is string[]) return string.Join(" ", (string[])v).Trim();
                return v.ToString().Trim();
            }
            catch { return ""; }
        }

        public static string ComputerName
        {
            get { try { return Environment.MachineName; } catch { return "—"; } }
        }

        public static string UserName
        {
            get { try { return Environment.UserName; } catch { return "—"; } }
        }

        public static string OsName()
        {
            const string cv = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            string product = R("HKEY_LOCAL_MACHINE", cv, "ProductName");
            string disp = R("HKEY_LOCAL_MACHINE", cv, "DisplayVersion");
            string build = R("HKEY_LOCAL_MACHINE", cv, "CurrentBuild");
            string ubr = R("HKEY_LOCAL_MACHINE", cv, "UBR");
            if (product.Length == 0) product = Environment.OSVersion.VersionString;
            var sb = new StringBuilder(product);
            if (disp.Length > 0) sb.Append(" ").Append(disp);
            if (build.Length > 0) sb.Append(" (Build ").Append(build);
            if (ubr.Length > 0) sb.Append(".").Append(ubr);
            if (build.Length > 0) sb.Append(")");
            return sb.ToString();
        }

        public static string OsArch
        {
            get { return Environment.Is64BitOperatingSystem ? "64 位" : "32 位"; }
        }

        public static string CpuName()
        {
            var s = R("HKEY_LOCAL_MACHINE", @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString");
            if (s.Length == 0) s = "未知处理器";
            return s.Replace("  ", " ").Trim();
        }

        public static string CpuCores()
        {
            try { return Environment.ProcessorCount + " 逻辑处理器"; }
            catch { return "—"; }
        }

        public static string BoardProduct()
        {
            var p = R("HKEY_LOCAL_MACHINE", @"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardProduct");
            var m = R("HKEY_LOCAL_MACHINE", @"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardManufacturer");
            if (p.Length == 0 && m.Length == 0) return "—";
            return (m.Length > 0 ? m + " " : "") + p;
        }

        public static string SystemModel()
        {
            var p = R("HKEY_LOCAL_MACHINE", @"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName");
            var m = R("HKEY_LOCAL_MACHINE", @"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer");
            if (p.Length == 0 && m.Length == 0) return "—";
            return ((m.Length > 0 ? m + " " : "") + p).Replace("System Product Name", "").Trim();
        }

        public static string BiosVersion()
        {
            var v = R("HKEY_LOCAL_MACHINE", @"HARDWARE\DESCRIPTION\System\BIOS", "BIOSVersion");
            var d = R("HKEY_LOCAL_MACHINE", @"HARDWARE\DESCRIPTION\System\BIOS", "BIOSReleaseDate");
            if (v.Length == 0) return "—";
            return d.Length > 0 ? v + " (" + d + ")" : v;
        }

        /// <summary>显卡型号：遍历注册表显示适配器类，取第一个有效 DriverDesc。</summary>
        public static string GpuName()
        {
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls != null)
                    {
                        foreach (var sub in cls.GetSubKeyNames())
                        {
                            if (sub.Length != 4) continue; // 只认 0000/0001 这类实例键
                            using (var k = cls.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                var desc = k.GetValue("DriverDesc") as string;
                                if (!string.IsNullOrEmpty(desc)) return desc.Trim();
                            }
                        }
                    }
                }
            }
            catch { }
            return "—";
        }

        /// <summary>固定磁盘汇总：型号取不到时至少给出总容量与可用空间。</summary>
        public static string Disks()
        {
            try
            {
                double total = 0, free = 0;
                int n = 0;
                foreach (var d in System.IO.DriveInfo.GetDrives())
                {
                    if (d.DriveType != System.IO.DriveType.Fixed || !d.IsReady) continue;
                    total += d.TotalSize;
                    free += d.TotalFreeSpace;
                    n++;
                }
                if (n == 0) return "—";
                return string.Format("{0} 个磁盘 · 总容量 {1} GB · 可用 {2} GB",
                    n,
                    Math.Round(total / 1024.0 / 1024 / 1024, 1),
                    Math.Round(free / 1024.0 / 1024 / 1024, 1));
            }
            catch { return "—"; }
        }

        /// <summary>已连接的物理网卡型号（用于识别本机硬件）。</summary>
        public static string NetAdapters()
        {
            try
            {
                var list = new List<string>();
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    var name = ni.Description;
                    if (string.IsNullOrEmpty(name)) name = ni.Name;
                    if (!list.Contains(name)) list.Add(name);
                }
                return list.Count > 0 ? string.Join(" / ", list) : "—";
            }
            catch { return "—"; }
        }

        public static string PrimaryIp()
        {
            try
            {
                // 优先取有默认网关的网卡地址（真实业务地址），否则回退第一个非回环地址
                string fallback = "";
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var props = ni.GetIPProperties();
                    bool hasGw = false;
                    foreach (var gw in props.GatewayAddresses)
                        if (gw.Address != null && !gw.Address.Equals(IPAddress.Any)) { hasGw = true; break; }
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var ip = ua.Address.ToString();
                        if (hasGw) return ip;
                        if (fallback.Length == 0) fallback = ip;
                    }
                }
                return fallback.Length > 0 ? fallback : "—";
            }
            catch { return "—"; }
        }

        public static string AllIpv4()
        {
            try
            {
                var list = new List<string>();
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !list.Contains(ua.Address.ToString()))
                            list.Add(ua.Address.ToString());
                }
                return list.Count > 0 ? string.Join(", ", list) : "—";
            }
            catch { return "—"; }
        }

        public static string PrimaryMac()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var mac = ni.GetPhysicalAddress();
                    if (mac != null && mac.ToString().Length >= 12)
                    {
                        var s = mac.ToString();
                        var sb = new StringBuilder();
                        for (int i = 0; i < s.Length; i += 2)
                        {
                            if (sb.Length > 0) sb.Append('-');
                            sb.Append(s.Substring(i, 2));
                        }
                        return sb.ToString();
                    }
                }
            }
            catch { }
            return "—";
        }

        public static string Uptime()
        {
            try
            {
                var ts = TimeSpan.FromMilliseconds(Environment.TickCount64);
                return string.Format("{0} 天 {1} 小时 {2} 分", (int)ts.TotalDays, ts.Hours, ts.Minutes);
            }
            catch { return "—"; }
        }
    }
}
