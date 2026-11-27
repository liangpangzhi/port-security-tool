using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace PortSecurityTool
{
    // ============================================================
    //  对外数据契约（API v1）
    //
    //  统一出口：本机采集结果 → 标准 JSON 文档。
    //  HTTP 推送、本地查询服务、文件导出三条通道共用同一份结构，
    //  服务端只需按 schemaVersion 解析，不必关心采集方式。
    //
    //  兼容原则：字段只增不改；新增字段一律可选，删除或改义必须升 schemaVersion。
    // ============================================================
    internal static class ApiReport
    {
        public const string SchemaName = "port-security-tool.device-report";
        public const string SchemaVersion = "1.0";
        public const string PortPolicy = "high-risk-84";

        /// <summary>「严重」级端口：暴露即判定高风险（勒索、远程控制、数据库、弱口令服务）。</summary>
        private static readonly HashSet<int> CriticalPorts = new HashSet<int>
        {
            21, 23, 135, 139, 445, 512, 513, 514, 1433, 1521,
            3306, 3389, 4444, 4899, 5432, 5900, 5985, 6379, 11211, 27017
        };

        // ---------------- 工具方法 ----------------

        private static long L(string s)
        {
            long v;
            return long.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static double? D(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            double v;
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : (double?)null;
        }

        /// <summary>稳定设备标识：SN + 主 MAC + 计算机名 的哈希，服务端可用它做去重与历史串联。</summary>
        public static string DeviceId(Collector.Snapshot s)
        {
            try
            {
                string seed = (s.Sn ?? "") + "|" + (s.Nic ?? "") + "|" + (s.Computer ?? "");
                using (var sha = SHA1.Create())
                {
                    var h = sha.ComputeHash(Encoding.UTF8.GetBytes(seed.ToUpperInvariant()));
                    var sb = new StringBuilder("pst-");
                    for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return "pst-unknown"; }
        }

        /// <summary>工具版本号（如 1.0.0），供界面与接口共用。</summary>
        public static string AgentVersionText { get { return AgentVersion(); } }

        private static string AgentVersion()
        {            try
            {
                var asm = typeof(ApiReport).Assembly;
                var info = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    asm, typeof(AssemblyInformationalVersionAttribute));
                if (info != null && !string.IsNullOrEmpty(info.InformationalVersion))
                {
                    // 去掉可能的 +commit 后缀
                    int plus = info.InformationalVersion.IndexOf('+');
                    return plus > 0 ? info.InformationalVersion.Substring(0, plus) : info.InformationalVersion;
                }
                var v = asm.GetName().Version;
                return v == null ? "0.0.0" : v.ToString(3);
            }
            catch { return "0.0.0"; }
        }

        private static string AgentBuild()
        {
            try
            {
                string bd = "", bt = "";
                foreach (var o in typeof(ApiReport).Assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false))
                {
                    var m = o as AssemblyMetadataAttribute;
                    if (m == null) continue;
                    if (m.Key == "BuildDate") bd = m.Value;
                    else if (m.Key == "BuildTime") bt = m.Value;
                }
                if (bd.Length == 0) return "";
                return bt.Length > 0 ? bd + " " + bt : bd;
            }
            catch { return ""; }
        }

        /// <summary>本机网卡 → {描述, IPv4, MAC}，优先返回带默认网关的那张。</summary>
        private static JArr CollectNics(out string primaryMac)
        {
            primaryMac = "";
            var arr = new JArr();
            try
            {
                // 先定位主网卡（有网关的），保证 primary 标记稳定
                string primaryDesc = "";
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var gw in ni.GetIPProperties().GatewayAddresses)
                    {
                        if (gw.Address != null && !gw.Address.Equals(System.Net.IPAddress.Any)) { primaryDesc = ni.Description; break; }
                    }
                    if (primaryDesc.Length > 0) break;
                }

                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    var mac = ni.GetPhysicalAddress();
                    string macText = "";
                    if (mac != null && mac.ToString().Length >= 12)
                    {
                        var ms = mac.ToString();
                        var sb = new StringBuilder();
                        for (int i = 0; i < ms.Length; i += 2)
                        {
                            if (sb.Length > 0) sb.Append(':');
                            sb.Append(ms.Substring(i, 2));
                        }
                        macText = sb.ToString();
                    }

                    string desc = string.IsNullOrEmpty(ni.Description) ? ni.Name : ni.Description;
                    bool isPrimary = primaryDesc.Length > 0 && ni.Description == primaryDesc;

                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        var o = new JObj()
                            .Put("adapter", desc)
                            .Put("address", ua.Address.ToString())
                            .PutNum("prefixLength", ua.PrefixLength)
                            .PutBool("primary", isPrimary);
                        if (macText.Length > 0) o.Put("mac", macText);
                        arr.AddObj(o);
                        if (isPrimary && primaryMac.Length == 0 && macText.Length > 0) primaryMac = macText;
                    }
                }
                if (primaryMac.Length == 0)
                {
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        var mac = ni.GetPhysicalAddress();
                        if (mac != null && mac.ToString().Length >= 12)
                        {
                            var ms = mac.ToString();
                            var sb = new StringBuilder();
                            for (int i = 0; i < ms.Length; i += 2) { if (sb.Length > 0) sb.Append(':'); sb.Append(ms.Substring(i, 2)); }
                            primaryMac = sb.ToString();
                            break;
                        }
                    }
                }
            }
            catch { }
            return arr;
        }

        // ---------------- 主结构 ----------------

        /// <summary>把一次采集快照转换为标准接口文档。</summary>
        public static JObj Build(Collector.Snapshot s, string reportId)
        {
            if (string.IsNullOrEmpty(reportId)) reportId = Guid.NewGuid().ToString("N");
            string mac;
            var nics = CollectNics(out mac);
            if (mac.Length == 0 && !string.IsNullOrEmpty(s.Nic)) mac = s.Nic;

            DateTime dt;
            if (!DateTime.TryParse(s.Time, out dt)) dt = DateTime.Now;
            var dto = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local));
            string iso = dto.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

            // --- 端口部分 ---
            var blocked = new HashSet<int>(s.Blocked);
            var items = new JArr();
            var exposedAll = new JArr();
            var criticalExposed = new JArr();
            foreach (var p in Collector.HighRiskPorts)
            {
                bool isBlocked = blocked.Contains(p);
                items.AddObj(new JObj()
                    .PutNum("port", p)
                    .Put("service", Collector.PortName(p))
                    .Put("state", isBlocked ? "blocked" : "exposed")
                    .PutBool("critical", CriticalPorts.Contains(p)));
                if (!isBlocked)
                {
                    exposedAll.Add(p);
                    if (CriticalPorts.Contains(p)) criticalExposed.Add(p);
                }
            }

            // --- 风险评分：暴露占比 + 严重端口加权，服务端可自行重算 ---
            int total = Collector.HighRiskPorts.Length;
            int exposed = s.Exposed.Count;
            int score = total <= 0 ? 0 : (int)Math.Round(exposed * 100.0 / total);
            score = Math.Min(100, score + criticalExposed.Count * 8);
            string level = criticalExposed.Count > 0 ? "high" : (score >= 60 ? "medium" : "low");

            var root = new JObj()
                .Put("schema", SchemaName)
                .Put("schemaVersion", SchemaVersion)
                .Put("reportId", reportId);

            var agent = new JObj()
                .Put("name", "网络信息采集和端口管理工具")
                .Put("version", AgentVersion())
                .PutBool("elevated", Bridge.IsRunningAsAdmin());
            string build = AgentBuild();
            if (build.Length > 0) agent.Put("build", build);
            agent.Put("osArch", Environment.Is64BitOperatingSystem ? "x64" : "x86");
            agent.Put("runtime", Environment.Version.ToString());
            root.PutObj("agent", agent);

            root.Put("collectedAt", iso);
            root.Put("collectedAtText", s.Time);

            var device = new JObj()
                .Put("deviceId", DeviceId(s))
                .Put("computerName", s.Computer)
                .Put("currentUser", s.User)
                .Put("domain", SafeDomain())
                .Put("serialNumber", s.Sn)
                .Put("osCaption", s.Os)
                .Put("osVersion", s.OsVer)
                .PutObj("cpu", new JObj()
                    .Put("model", s.CpuName)
                    .PutNum("physicalCores", L(s.CpuCores))
                    .PutNum("logicalCores", L(s.CpuLogical))
                    .PutNum("currentMHz", D(s.CpuCur))
                    .PutNum("maxMHz", D(s.CpuMax)))
                .PutObj("memory", new JObj()
                    .PutNum("moduleCount", L(s.MemCount))
                    .PutNum("totalGB", D(s.MemTotal))
                    .Put("detail", s.MemDetail))
                .PutObj("board", new JObj()
                    .Put("manufacturer", s.BoardMfr)
                    .Put("product", s.BoardProduct)
                    .Put("serialNumber", s.BoardSn))
                .PutObj("bios", new JObj()
                    .Put("manufacturer", s.BiosMfr)
                    .Put("version", s.BiosVersion)
                    .Put("releaseDate", NormalizeWmiDate(s.BiosDate)))
                .PutObj("disks", new JObj()
                    .PutNum("count", L(s.DiskCount))
                    .Put("detail", s.DiskDetail)
                    .Put("partitions", s.Partition))
                .PutObj("network", new JObj()
                    .Put("primaryMac", mac)
                    .PutArr("addresses", nics)
                    .Put("adapters", s.Nic));
            root.PutObj("device", device);

            root.PutObj("ports", new JObj()
                .Put("policy", PortPolicy)
                .PutNum("total", total)
                .PutNum("blocked", s.Blocked.Count)
                .PutNum("exposed", exposed)
                .PutArr("items", items));

            root.PutObj("risk", new JObj()
                .PutNum("score", score)
                .Put("level", level)
                .PutNum("criticalCount", criticalExposed.Count)
                .PutArr("criticalExposed", criticalExposed)
                .PutArr("exposedPorts", exposedAll));

            return root;
        }

        private static string SafeDomain()
        {
            try { return Environment.UserDomainName ?? ""; } catch { return ""; }
        }

        /// <summary>WMI 日期（20150629000000.000000+000）→ 2015-06-29，便于服务端入库。</summary>
        private static string NormalizeWmiDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var t = s.Trim();
            if (t.Length >= 8)
            {
                DateTime d;
                if (DateTime.TryParseExact(t.Substring(0, 8), "yyyyMMdd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                    return d.ToString("yyyy-MM-dd");
            }
            return t;   // 非标准格式原样返回
        }

        /// <summary>生成推送用 JSON（紧凑，单行）。</summary>
        public static string ToJson(Collector.Snapshot s, string reportId, bool pretty)
        {
            return Build(s, reportId).ToJson(pretty);
        }

        // ---------------- JSON Schema（供服务端校验/生成模型） ----------------

        public static string SchemaJson()
        {
            return @"{
  ""$schema"": ""http://json-schema.org/draft-07/schema#"",
  ""$id"": ""port-security-tool.device-report/1.0"",
  ""title"": ""本机安全数据上报文档"",
  ""type"": ""object"",
  ""required"": [""schema"", ""schemaVersion"", ""reportId"", ""collectedAt"", ""agent"", ""device"", ""ports"", ""risk""],
  ""properties"": {
    ""schema"":        { ""type"": ""string"", ""const"": ""port-security-tool.device-report"" },
    ""schemaVersion"": { ""type"": ""string"", ""example"": ""1.0"" },
    ""reportId"":      { ""type"": ""string"", ""description"": ""本次上报唯一标识（32 位十六进制），服务端据此做幂等去重"" },
    ""collectedAt"":   { ""type"": ""string"", ""format"": ""date-time"", ""description"": ""带时区偏移的采集时间"" },
    ""collectedAtText"": { ""type"": ""string"", ""description"": ""本地可读时间 yyyy-MM-dd HH:mm:ss"" },
    ""agent"": {
      ""type"": ""object"",
      ""properties"": {
        ""name"": { ""type"": ""string"" },
        ""version"": { ""type"": ""string"", ""example"": ""1.0.0"" },
        ""build"": { ""type"": ""string"" },
        ""elevated"": { ""type"": ""boolean"" },
        ""osArch"": { ""type"": ""string"" },
        ""runtime"": { ""type"": ""string"" }
      }
    },
    ""device"": {
      ""type"": ""object"",
      ""required"": [""deviceId"", ""computerName""],
      ""properties"": {
        ""deviceId"": { ""type"": ""string"", ""description"": ""SN + 主 MAC + 计算机名的哈希，跨次上报保持稳定"" },
        ""computerName"": { ""type"": ""string"" },
        ""currentUser"": { ""type"": ""string"" },
        ""domain"": { ""type"": ""string"" },
        ""serialNumber"": { ""type"": ""string"" },
        ""osCaption"": { ""type"": ""string"" },
        ""osVersion"": { ""type"": ""string"" },
        ""cpu"": { ""type"": ""object"", ""properties"": {
          ""model"": { ""type"": ""string"" },
          ""physicalCores"": { ""type"": ""integer"" },
          ""logicalCores"": { ""type"": ""integer"" },
          ""currentMHz"": { ""type"": [""number"", ""null""] },
          ""maxMHz"": { ""type"": [""number"", ""null""] } } },
        ""memory"": { ""type"": ""object"", ""properties"": {
          ""moduleCount"": { ""type"": ""integer"" },
          ""totalGB"": { ""type"": [""number"", ""null""] },
          ""detail"": { ""type"": ""string"" } } },
        ""board"": { ""type"": ""object"", ""properties"": {
          ""manufacturer"": { ""type"": ""string"" },
          ""product"": { ""type"": ""string"" },
          ""serialNumber"": { ""type"": ""string"" } } },
        ""bios"": { ""type"": ""object"", ""properties"": {
          ""manufacturer"": { ""type"": ""string"" },
          ""version"": { ""type"": ""string"" },
          ""releaseDate"": { ""type"": ""string"" } } },
        ""disks"": { ""type"": ""object"", ""properties"": {
          ""count"": { ""type"": ""integer"" },
          ""detail"": { ""type"": ""string"" },
          ""partitions"": { ""type"": ""string"" } } },
        ""network"": { ""type"": ""object"", ""properties"": {
          ""primaryMac"": { ""type"": ""string"" },
          ""addresses"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
            ""adapter"": { ""type"": ""string"" },
            ""address"": { ""type"": ""string"" },
            ""prefixLength"": { ""type"": ""integer"" },
            ""mac"": { ""type"": ""string"" },
            ""primary"": { ""type"": ""boolean"" } } } },
          ""adapters"": { ""type"": ""string"" } } }
      }
    },
    ""ports"": {
      ""type"": ""object"",
      ""properties"": {
        ""policy"": { ""type"": ""string"", ""example"": ""high-risk-84"" },
        ""total"": { ""type"": ""integer"" },
        ""blocked"": { ""type"": ""integer"" },
        ""exposed"": { ""type"": ""integer"" },
        ""items"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
          ""port"": { ""type"": ""integer"" },
          ""service"": { ""type"": ""string"" },
          ""state"": { ""type"": ""string"", ""enum"": [""blocked"", ""exposed""] },
          ""critical"": { ""type"": ""boolean"" } } } }
      }
    },
    ""risk"": {
      ""type"": ""object"",
      ""properties"": {
        ""score"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 100 },
        ""level"": { ""type"": ""string"", ""enum"": [""low"", ""medium"", ""high""] },
        ""criticalCount"": { ""type"": ""integer"" },
        ""criticalExposed"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
        ""exposedPorts"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } }
      }
    }
  }
}";
        }
    }
}
