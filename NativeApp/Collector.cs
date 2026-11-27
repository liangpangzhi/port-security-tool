using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PortSecurityTool
{
    /// <summary>
    /// 本机数据采集 + 电子表格生成 + 向指定 IP 的共享目录发送。
    /// 表格版式对齐单位「采集端」模板：单行记录的横向宽表，表头蓝底白字加粗居中、无边框。
    /// </summary>
    internal static class Collector
    {
        /// <summary>与界面「预置高危端口清单」保持一致的 84 个高危端口。</summary>
        public static readonly int[] HighRiskPorts = new int[]
        {
            20,21,22,23,25,53,69,80,110,111,
            135,137,138,139,143,161,162,389,443,445,
            465,512,513,514,636,873,993,995,1194,1352,
            1433,1434,1521,2049,2181,2375,2376,3260,3306,3389,
            3690,4444,4848,4899,5432,5631,5632,5900,5901,5902,
            5903,5904,5905,5906,5907,5908,5909,5984,5985,5986,
            6129,6379,7000,7001,7002,7199,7778,8000,8069,8080,
            8443,9042,9090,9160,9200,9300,10050,10051,11211,27017,
            27018,50000,50050,50070
        };

        private static readonly Dictionary<int, string> PortNames = new Dictionary<int, string>
        {
            {20,"FTP-DATA"},{21,"FTP"},{22,"SSH"},{23,"Telnet"},{25,"SMTP"},{53,"DNS"},{69,"TFTP"},
            {80,"HTTP"},{110,"POP3"},{111,"RPCBind"},{135,"RPC"},{137,"NetBIOS-NS"},{138,"NetBIOS-DGM"},
            {139,"NetBIOS-SSN"},{143,"IMAP"},{161,"SNMP"},{162,"SNMP-Trap"},{389,"LDAP"},{443,"HTTPS"},
            {445,"SMB"},{465,"SMTPS"},{512,"rexec"},{513,"rlogin"},{514,"rsh"},{636,"LDAPS"},
            {873,"rsync"},{993,"IMAPS"},{995,"POP3S"},{1194,"OpenVPN"},{1352,"Lotus Notes"},
            {1433,"SQL Server"},{1434,"SQL Browser"},{1521,"Oracle"},{2049,"NFS"},{2181,"ZooKeeper"},
            {2375,"Docker"},{2376,"Docker TLS"},{3260,"iSCSI"},{3306,"MySQL"},{3389,"RDP 远程桌面"},
            {3690,"SVN"},{4444,"木马常用"},{4848,"GlassFish"},{4899,"Radmin"},{5432,"PostgreSQL"},
            {5631,"pcAnywhere"},{5632,"pcAnywhere-DATA"},{5900,"VNC"},{5901,"VNC-1"},{5902,"VNC-2"},
            {5903,"VNC-3"},{5904,"VNC-4"},{5905,"VNC-5"},{5906,"VNC-6"},{5907,"VNC-7"},
            {5908,"VNC-8"},{5909,"VNC-9"},{5984,"CouchDB"},{5985,"WinRM"},{5986,"WinRM TLS"},
            {6129,"DameWare"},{6379,"Redis"},{7000,"WebLogic"},{7001,"WebLogic-SSL"},{7002,"WebLogic"},
            {7199,"Cassandra JMX"},{7778,"恶意程序常用"},{8000,"HTTP 备用"},{8069,"Odoo"},
            {8080,"HTTP 代理"},{8443,"HTTPS 备用"},{9042,"Cassandra"},{9090,"管理面板"},
            {9160,"Cassandra Thrift"},{9200,"Elasticsearch"},{9300,"Elasticsearch 集群"},
            {10050,"Zabbix Agent"},{10051,"Zabbix Server"},{11211,"Memcached"},{27017,"MongoDB"},
            {27018,"MongoDB 副本"},{50000,"SAP"},{50050,"SAP 管理"},{50070,"HDFS NameNode"}
        };

        public static string PortName(int port)
        {
            string v;
            return PortNames.TryGetValue(port, out v) ? v : "未知服务";
        }

        /// <summary>表头：与「采集端」模板的 25 列顺序一致，末尾追加端口封堵统计。</summary>
        public static readonly string[] Headers = new string[]
        {
            "计算机名", "当前用户", "整机SN号", "操作系统", "系统版本", "IP地址", "采集时间",
            "CPU型号", "CPU物理核心数", "CPU逻辑核心数", "CPU当前频率(MHz)", "CPU最大频率(MHz)",
            "内存条数量", "内存总容量(GB)", "内存详细信息",
            "主板厂商", "主板型号", "主板序列号", "BIOS厂商", "BIOS版本", "BIOS日期",
            "硬盘数量", "硬盘详细信息", "分区使用情况", "网卡MAC信息",
            "高危端口总数", "已封堵端口数", "未封堵端口数", "未封堵端口明细"
        };

        /// <summary>列宽（字符数）：取自「采集端」模板的真实列宽，逐列对齐；末尾四列为端口统计。</summary>
        public static readonly int[] ColWidths = new int[]
        {
            8, 8, 11, 14, 14, 17, 23, 44, 12, 12, 16, 16,
            9, 13, 47, 13, 10, 28, 13, 10, 29, 8, 80, 80, 63,
            12, 12, 12, 40
        };

        // 模板中以数值写入的列（右对齐）：内存条数量、内存总容量、硬盘数量 + 端口统计
        private static readonly int[] NumericCols = new int[] { 12, 13, 21, 25, 26, 27 };

        // ---------------- 采集 ----------------
        public sealed class Snapshot
        {
            public string Computer = "—";
            public string User = "—";
            public string Sn = "";
            public string Os = "—";
            public string OsVer = "—";
            public string Ip = "—";
            public string Time = "—";

            public string CpuName = "—";
            public string CpuCores = "";
            public string CpuLogical = "";
            public string CpuCur = "";
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
            public string Partition = "";
            public string Nic = "";

            public List<int> Blocked = new List<int>();
            public List<int> Exposed = new List<int>();

            public int Total { get { return HighRiskPorts.Length; } }
            public string BlockedText { get { return Blocked.Count == 0 ? "无" : string.Join(",", Blocked.ToArray()); } }
            public string ExposedText { get { return Exposed.Count == 0 ? "无" : string.Join(",", Exposed.ToArray()); } }
        }

        public static Snapshot Collect()
        {
            var s = new Snapshot();
            try
            {
                s.Computer = Environment.MachineName;
                s.User = Environment.UserName;
                s.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                s.Ip = SysInfo.PrimaryIp();

                // 首选 WMI：SN、内存条、硬盘型号等字段只有它能取全
                var w = WmiInfo.Query();
                if (w.Ok)
                {
                    s.Sn = w.BiosSn;
                    s.Os = w.OsCaption.Length > 0 ? w.OsCaption : SysInfo.OsName();
                    s.OsVer = w.OsVersion.Length > 0 ? w.OsVersion : "—";
                    s.CpuName = w.CpuName;
                    s.CpuCores = w.CpuCores;
                    s.CpuLogical = w.CpuLogical;
                    s.CpuCur = w.CpuCurrent;
                    s.CpuMax = w.CpuMax;
                    s.MemCount = w.MemCount;
                    s.MemTotal = w.MemTotal;
                    s.MemDetail = w.MemDetail;
                    s.BoardMfr = w.BoardMfr;
                    s.BoardProduct = w.BoardProduct;
                    s.BoardSn = w.BoardSn;
                    s.BiosMfr = w.BiosMfr;
                    s.BiosVersion = w.BiosVersion;
                    s.BiosDate = w.BiosDate;
                    s.DiskCount = w.DiskCount;
                    s.DiskDetail = w.DiskDetail;
                    s.Nic = w.NicDetail;
                }
                else
                {
                    // WMI 不可用时回退到注册表 / 系统 API
                    s.Os = SysInfo.OsName();
                    s.OsVer = Environment.OSVersion.Version.ToString();
                    s.CpuName = SysInfo.CpuName();
                    s.CpuCores = "";
                    s.CpuLogical = Environment.ProcessorCount.ToString();
                    s.CpuCur = "";
                    s.CpuMax = "";
                    s.MemCount = "";
                    s.MemTotal = Math.Round(SysInfo.MemoryTotalGB(), 0).ToString("0");
                    s.MemDetail = "";
                    s.BoardMfr = "";
                    s.BoardProduct = SysInfo.BoardProduct();
                    s.BiosVersion = SysInfo.BiosVersion();
                    s.DiskCount = "";
                    s.DiskDetail = SysInfo.Disks();
                    s.Nic = SysInfo.PrimaryMac();
                }

                s.Partition = WmiInfo.PartitionDetail();

                var blocked = Bridge.BlockedPortSet();
                foreach (var p in HighRiskPorts)
                {
                    if (blocked.Contains(p)) s.Blocked.Add(p);
                    else s.Exposed.Add(p);
                }
            }
            catch (Exception ex)
            {
                s.CpuName = "采集异常：" + ex.Message;
            }
            return s;
        }

        // ---------------- 生成电子表格 ----------------
        public static string ReportDirectory
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PortSecurityTool", "reports");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string BuildFile(Snapshot s, string outDir)
        {
            if (string.IsNullOrEmpty(outDir)) outDir = ReportDirectory;
            Directory.CreateDirectory(outDir);
            var safeName = MakeSafe(s.Computer);
            // 文件名：被采集端计算机名 + 采集日期（同一天内重复采集覆盖，按天留存历史）
            var day = DateTime.Now.ToString("yyyy-MM-dd");
            if (!string.IsNullOrEmpty(s.Time) && s.Time.Length >= 10 && s.Time[4] == '-')
                day = s.Time.Substring(0, 10);
            var path = Path.Combine(outDir, safeName + "_" + day + ".xlsx");

            // Sheet1：横向宽表，一行一台机器（版式同「采集端」模板）
            var main = new XlsxWriter.Sheet("本机硬件详情") { ColWidths = ColWidths };
            foreach (var n in NumericCols) main.NumericCols.Add(n);
            main.Add(Headers);
            main.Add(
                s.Computer, s.User, s.Sn, s.Os, s.OsVer, s.Ip, s.Time,
                s.CpuName, s.CpuCores, s.CpuLogical, s.CpuCur, s.CpuMax,
                s.MemCount, s.MemTotal, s.MemDetail,
                s.BoardMfr, s.BoardProduct, s.BoardSn,
                s.BiosMfr, s.BiosVersion, s.BiosDate,
                s.DiskCount, s.DiskDetail, s.Partition, s.Nic,
                s.Total.ToString(), s.Blocked.Count.ToString(), s.Exposed.Count.ToString(), s.ExposedText);

            // Sheet2：端口封堵明细（表头沿用同一版式）
            var ports = new XlsxWriter.Sheet("端口封堵情况") { ColWidths = new[] { 10, 26, 12 } };
            ports.NumericCols.Add(0);
            ports.Add("端口", "常见服务", "封堵状态");
            foreach (var p in HighRiskPorts)
                ports.Add(p.ToString(), PortName(p), s.Blocked.Contains(p) ? "已封堵" : "未封堵");

            XlsxWriter.Write(path, new List<XlsxWriter.Sheet> { main, ports });
            return path;
        }

        private static string MakeSafe(string name)
        {
            if (string.IsNullOrEmpty(name)) return "未知计算机";
            foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            return name.Trim();
        }

        // ---------------- 网络共享发送 ----------------
        [StructLayout(LayoutKind.Sequential)]
        private class NetResourceRaw
        {
            public int dwScope;
            public int dwType;
            public int dwDisplayType;
            public int dwUsage;
            public string lpLocalName;
            public string lpRemoteName;
            public string lpComment;
            public string lpProvider;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetAddConnection2([In] NetResourceRaw lpNetResource, string lpPassword, string lpUserName, int dwFlags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetCancelConnection2(string lpName, int dwFlags, bool fForce);

        /// <summary>把用户填写的 IP / 共享名 / 完整 UNC 统一成 \\server\share 形式。</summary>
        public static string NormalizeTarget(string ip, string share)
        {
            var s = (ip ?? "").Trim();
            if (s.Length == 0) return "";
            s = s.Replace('/', '\\');
            if (s.StartsWith("\\\\")) return s.TrimEnd('\\');
            s = s.Trim('\\');
            var sh = (share ?? "").Trim().Trim('\\');
            if (sh.Length == 0) sh = "设备信息采集";
            return "\\\\" + s + "\\" + sh;
        }

        private static void Connect(string unc, string user, string pass)
        {
            if (string.IsNullOrEmpty(user)) return;
            var nr = new NetResourceRaw
            {
                dwType = 1,
                dwScope = 0,
                dwDisplayType = 0,
                dwUsage = 0,
                lpRemoteName = unc,
                lpProvider = null,
                lpLocalName = null,
                lpComment = null
            };
            int rc = WNetAddConnection2(nr, pass, user, 4); // CONNECT_TEMPORARY
            if (rc != 0 && rc != 1219)
                throw new IOException("共享认证失败（错误码 " + rc + "）：" + MapNetError(rc));
        }

        private static void Disconnect(string unc)
        {
            try { WNetCancelConnection2(unc, 0, true); } catch { }
        }

        private static string MapNetError(int code)
        {
            switch (code)
            {
                case 5: return "拒绝访问，账号无写入权限";
                case 53: return "找不到网络路径，请确认 IP 可达且已开启共享";
                case 67: return "找不到网络名，请确认共享文件夹名称正确";
                case 86: return "登录失败，用户名或密码错误";
                case 1326: return "用户名或密码错误";
                case 1219: return "该服务器已使用其他凭据连接";
                case 1272: return "无法访问，可能被安全策略拦截";
                default: return "系统错误";
            }
        }

        public sealed class SendResult
        {
            public bool Ok;
            public string Message = "";
            public string LocalFile = "";
            public string RemoteFile = "";
        }

        /// <summary>采集一次，然后按两条通道分发：SMB 共享（表格）+ HTTP 接口（JSON）。</summary>
        public static SendResult CollectAndSend(string ip, string share, string user, string pass)
        {
            Snapshot s;
            try { s = Collect(); }
            catch (Exception ex) { return new SendResult { Message = "采集失败：" + ex.Message }; }
            return SendSnapshot(s, ip, share, user, pass);
        }

        /// <summary>
        /// 把已采集的快照分发到共享目录与数据接口（两者可独立启用）。
        /// 表格仍本地留档；接口推送失败会自动进入待发队列，不丢数据。
        /// </summary>
        public static SendResult SendSnapshot(Snapshot s, string ip, string share, string user, string pass)
        {
            var r = new SendResult();
            try { r.LocalFile = BuildFile(s, null); }
            catch (Exception ex) { r.Message = "生成电子表格失败：" + ex.Message; return r; }

            string fileName = Path.GetFileName(r.LocalFile);
            var parts = new List<string>();
            bool shareOk = false, shareConfigured = false, apiOk = false;
            bool apiEnabled = ApiClient.Enabled;
            string shareErr = "", apiInfo = "";
            bool apiQueued = false;

            // ---- 通道一：SMB 共享（电子表格）----
            var unc = NormalizeTarget(ip, share);
            if (unc.Length > 0)
            {
                shareConfigured = true;
                bool connected = false;
                try
                {
                    if (!string.IsNullOrEmpty(user)) { Connect(unc, user, pass); connected = true; }

                    if (!Directory.Exists(unc))
                    {
                        var parent = Path.GetDirectoryName(unc);
                        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                            throw new IOException("目标不可访问 " + unc + "（请确认共享已开启、网络可达、权限足够）");
                    }

                    var remote = Path.Combine(unc, fileName);
                    File.Copy(r.LocalFile, remote, true);
                    r.RemoteFile = remote;
                    shareOk = true;
                }
                catch (UnauthorizedAccessException)
                {
                    shareErr = "目标共享拒绝写入，请检查共享权限或账号权限";
                }
                catch (IOException ex) { shareErr = ex.Message; }
                catch (Exception ex) { shareErr = ex.Message; }
                finally
                {
                    if (connected) Disconnect(unc);
                }
            }

            // ---- 通道二：HTTP 数据接口（JSON）----
            if (apiEnabled)
            {
                var pr = ApiClient.Push(s, null);
                apiOk = pr.Ok;
                apiInfo = pr.Ok ? "推送成功（HTTP " + pr.Status + "）" : "推送失败：" + pr.Message;
                apiQueued = pr.Queued;
                try
                {
                    AppConfig.Set("api.lastTime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    AppConfig.Set("api.lastResult",
                        (pr.Ok ? "成功 HTTP " + pr.Status : "失败 " + pr.Message).Replace("\r", "").Replace("\n", " "));
                }
                catch { }
            }

            if (!shareConfigured && !apiEnabled)
            {
                r.Message = "未配置上报目标与数据接口，文件已保存到本地：" + r.LocalFile;
                return r;
            }

            if (shareConfigured)
                parts.Add(shareOk ? "共享 成功（" + r.RemoteFile + "）" : "共享 失败（" + shareErr + "）");
            else
                parts.Add("共享 未配置");

            if (apiEnabled)
                parts.Add("接口 " + (apiOk ? "成功" : apiInfo + (apiQueued ? "，已入待发队列" : "")));
            else
                parts.Add("接口 未启用");

            bool anyOk = shareOk || apiOk;
            bool allOk = (!shareConfigured || shareOk) && (!apiEnabled || apiOk);
            string head = allOk && anyOk ? "上报成功" : (anyOk ? "部分上报成功" : "上报失败");

            r.Ok = allOk && anyOk;
            r.Message = head + "：" + string.Join("｜", parts.ToArray())
                      + "\n" + fileName
                      + "\n（已封堵 " + s.Blocked.Count + " / " + s.Total + " 个高危端口）";

            // 顺带补发离线期间积压的报文
            if (apiEnabled && apiOk) { try { ApiClient.FlushOutbox(10); } catch { } }
            return r;
        }

        /// <summary>供界面预览用的精简快照（制表符分隔，行间 \u0001）。</summary>
        public static string PreviewText()
        {
            var s = Collect();
            var sb = new StringBuilder();
            Action<string, string> add = (k, v) => sb.Append(k).Append('\t').Append(v).Append('\u0001');
            add("计算机名", s.Computer);
            add("当前用户", s.User);
            add("整机SN号", s.Sn);
            add("操作系统", s.Os);
            add("系统版本", s.OsVer);
            add("IP地址", s.Ip);
            add("采集时间", s.Time);
            add("CPU型号", s.CpuName);
            add("CPU核心", s.CpuCores + " 物理 / " + s.CpuLogical + " 逻辑");
            add("CPU频率", s.CpuCur + " / " + s.CpuMax + " MHz");
            add("内存", s.MemTotal + " GB（" + s.MemCount + " 条）");
            add("内存详情", s.MemDetail);
            add("主板", (s.BoardMfr + " " + s.BoardProduct).Trim());
            add("主板序列号", s.BoardSn);
            add("BIOS", (s.BiosMfr + " " + s.BiosVersion).Trim());
            add("BIOS日期", s.BiosDate);
            add("硬盘数量", s.DiskCount);
            add("硬盘详情", s.DiskDetail);
            add("分区使用", s.Partition);
            add("网卡MAC", s.Nic);
            add("高危端口总数", s.Total.ToString());
            add("已封堵", s.Blocked.Count + " 个");
            add("未封堵", s.Exposed.Count + " 个");
            add("未封堵明细", s.ExposedText);
            return sb.ToString();
        }

        /// <summary>生成一份本地表格但不上报，返回文件路径。</summary>
        public static string BuildLocalOnly()
        {
            var s = Collect();
            return BuildFile(s, null);
        }
    }
}
