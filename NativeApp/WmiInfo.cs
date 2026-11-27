using System;
using System.Collections.Generic;
using System.Management;
using System.Text;

namespace PortSecurityTool
{
    /// <summary>
    /// 通过 WMI 采集与「采集端」一致的硬件字段（SN、内存条、硬盘型号等）。
    /// 采集失败（WMI 服务被禁用等）时 Ok=false，调用方回退到注册表方案。
    /// </summary>
    internal static class WmiInfo
    {
        public sealed class Hw
        {
            public bool Ok;
            public string ComputerName = "";
            public string UserName = "";

            public string BiosSn = "";
            public string OsCaption = "";
            public string OsVersion = "";

            public string CpuName = "";
            public string CpuCores = "";
            public string CpuLogical = "";
            public string CpuCurrent = "";
            public string CpuMax = "";

            public string MemCount = "";
            public string MemTotal = "";
            public string MemDetail = "";

            public string BoardMfr = "";
            public string BoardProduct = "";
            public string BoardSn = "";

            public string BiosMfr = "";
            public string BiosVersion = "";
            public string BiosDate = "";

            public string DiskCount = "";
            public string DiskDetail = "";

            public string NicDetail = "";
        }

        private static List<ManagementBaseObject> Q(string wql)
        {
            var list = new List<ManagementBaseObject>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(wql))
                using (var col = searcher.Get())
                {
                    foreach (ManagementBaseObject o in col) list.Add(o);
                }
            }
            catch { }
            return list;
        }

        private static string S(ManagementBaseObject o, string prop)
        {
            try
            {
                var v = o[prop];
                if (v == null) return "";
                return v.ToString().Trim();
            }
            catch { return ""; }
        }

        private static double D(ManagementBaseObject o, string prop)
        {
            double d;
            var s = S(o, prop);
            return double.TryParse(s, out d) ? d : 0;
        }

        /// <summary>压缩多余空格（WMI 的 CPU 名称常带连续空格，模板里是单空格）。</summary>
        private static string Tight(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            while (s.IndexOf("  ", StringComparison.Ordinal) >= 0) s = s.Replace("  ", " ");
            return s.Trim();
        }

        public static Hw Query()
        {
            var h = new Hw();
            try
            {
                h.ComputerName = Environment.MachineName;
                h.UserName = Environment.UserName;

                // ---- 操作系统 ----
                var os = Q("SELECT Caption, Version FROM Win32_OperatingSystem");
                if (os.Count > 0)
                {
                    h.OsCaption = S(os[0], "Caption").Replace("Microsoft ", "");
                    h.OsVersion = S(os[0], "Version");
                }

                // ---- CPU ----
                var cpu = Q("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, CurrentClockSpeed, MaxClockSpeed FROM Win32_Processor");
                if (cpu.Count > 0)
                {
                    h.CpuName = S(cpu[0], "Name");   // 保留 WMI 原始串（含连续空格），与模板一致
                    h.CpuCores = S(cpu[0], "NumberOfCores");
                    h.CpuLogical = S(cpu[0], "NumberOfLogicalProcessors");
                    h.CpuCurrent = S(cpu[0], "CurrentClockSpeed");
                    h.CpuMax = S(cpu[0], "MaxClockSpeed");
                }

                // ---- BIOS ----
                var bios = Q("SELECT Manufacturer, SMBIOSBIOSVersion, Version, ReleaseDate, SerialNumber FROM Win32_BIOS");
                if (bios.Count > 0)
                {
                    h.BiosSn = S(bios[0], "SerialNumber");
                    h.BiosMfr = S(bios[0], "Manufacturer");
                    h.BiosVersion = S(bios[0], "SMBIOSBIOSVersion");
                    if (h.BiosVersion.Length == 0) h.BiosVersion = S(bios[0], "Version");
                    h.BiosDate = S(bios[0], "ReleaseDate");   // 保留 WMI 原始串，与采集端一致
                }

                // ---- 主板 ----
                var board = Q("SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard");
                if (board.Count > 0)
                {
                    h.BoardMfr = S(board[0], "Manufacturer");
                    h.BoardProduct = S(board[0], "Product");
                    h.BoardSn = S(board[0], "SerialNumber");
                }

                // ---- 内存 ----
                var mem = Q("SELECT Capacity, Speed, Manufacturer FROM Win32_PhysicalMemory");
                var memParts = new List<string>();
                double totalBytes = 0;
                foreach (var m in mem)
                {
                    double cap = D(m, "Capacity");
                    double speed = D(m, "Speed");
                    var mfr = S(m, "Manufacturer");
                    totalBytes += cap;
                    var capG = cap / 1024.0 / 1024 / 1024;
                    var seg = capG.ToString("0.0") + "G";
                    if (speed > 0) seg += "-" + ((int)speed) + "MHz";
                    if (mfr.Length > 0) seg += "-" + mfr;
                    memParts.Add(seg);
                }
                h.MemCount = mem.Count.ToString();
                h.MemTotal = Math.Round(totalBytes / 1024.0 / 1024 / 1024, 0).ToString("0");
                h.MemDetail = string.Join(" | ", memParts.ToArray());

                // ---- 硬盘 ----
                var disk = Q("SELECT Model, Size, SerialNumber, MediaType FROM Win32_DiskDrive");
                var diskParts = new List<string>();
                foreach (var d in disk)
                {
                    var size = D(d, "Size");
                    var capG = size / 1024.0 / 1024 / 1024;   // 与模板一致：按 1024 进制换算 GiB
                    var model = S(d, "Model");
                    var sn = S(d, "SerialNumber");
                    var sb = new StringBuilder("[");
                    sb.Append(model.Length > 0 ? model : "未知磁盘");
                    sb.Append(" | ").Append(capG.ToString("0.0#")).Append("G");
                    if (sn.Length > 0) sb.Append(" | SN:").Append(sn);
                    sb.Append("]");
                    diskParts.Add(sb.ToString());
                }
                h.DiskCount = disk.Count.ToString();
                h.DiskDetail = string.Join(" ", diskParts.ToArray());

                // ---- 网卡（描述 + MAC，与采集端格式一致）----
                var nicParts = new List<string>();
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                    var mac = ni.GetPhysicalAddress();
                    if (mac == null || mac.ToString().Length < 12) continue;
                    var ms = mac.ToString();
                    var sb = new StringBuilder();
                    for (int i = 0; i < ms.Length; i += 2)
                    {
                        if (sb.Length > 0) sb.Append(':');
                        sb.Append(ms.Substring(i, 2));
                    }
                    var desc = ni.Description;
                    if (string.IsNullOrEmpty(desc)) desc = ni.Name;
                    nicParts.Add("[" + desc + " : " + sb + "]");
                }
                h.NicDetail = string.Join(" ", nicParts.ToArray());

                h.Ok = h.CpuName.Length > 0 || h.OsCaption.Length > 0;
            }
            catch { h.Ok = false; }
            return h;
        }

        /// <summary>分区使用情况：与采集端一致，形如 [C:\ 剩余191.34G/237.66G (19.5%)]。</summary>
        public static string PartitionDetail()
        {
            try
            {
                var parts = new List<string>();
                foreach (var d in System.IO.DriveInfo.GetDrives())
                {
                    if (d.DriveType != System.IO.DriveType.Fixed || !d.IsReady) continue;
                    double total = d.TotalSize / 1024.0 / 1024 / 1024;
                    double free = d.TotalFreeSpace / 1024.0 / 1024 / 1024;
                    if (total <= 0) continue;
                    double usedPct = (total - free) / total * 100;
                    parts.Add("[" + d.Name + " 剩余" + free.ToString("0.0#") + "G/" + total.ToString("0.0#") + "G (" + usedPct.ToString("0.0") + "%)]");
                }
                return string.Join(" ", parts.ToArray());
            }
            catch { return ""; }
        }
    }
}
