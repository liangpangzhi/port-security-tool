using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PortSecurityTool
{
    /// <summary>本机数据采集：硬件信息、计算机名、端口封堵情况，供局域网代理上报给服务端。</summary>
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
    }
}
