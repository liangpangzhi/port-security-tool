using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PortSecurityTool
{
    /// <summary>接口层自检：契约生成、推送/补发、本地服务、文档导出。结果写入 apiprobe_out.txt。</summary>
    internal static class ApiProbeProgram
    {
        private static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string OutPath = Path.Combine(Dir, "apiprobe_out.txt");
        private static readonly object _wlock = new object();

        private static int _pass, _fail;

        private static void W(string s)
        {
            lock (_wlock)
            {
                File.AppendAllText(OutPath, s + "\r\n", Encoding.UTF8);
            }
        }

        private static void Check(string name, bool ok, string detail = "")
        {
            if (ok) _pass++; else _fail++;
            W((ok ? "[PASS] " : "[FAIL] ") + name + (detail == null || detail.Length == 0 ? "" : "  → " + detail));
        }

        [STAThread]
        private static void Main(string[] args)
        {
            if (args != null && args.Length > 0 && args[0] == "--si-hold")
            {
                bool got = SingleInstance.TryAcquire();
                File.WriteAllText(Path.Combine(Dir, "si_hold.txt"), got ? "ACQUIRED" : "BLOCKED");
                Thread.Sleep(6000);
                Environment.Exit(0);
            }
            if (args != null && args.Length > 0 && args[0] == "--find")
            {
                bool found = SystemIntegration.ActivateExisting(2500);
                File.WriteAllText(Path.Combine(Dir, "find.txt"), found ? "FOUND" : "NOTFOUND");
                Environment.Exit(0);
            }
            if (args != null && args.Length > 0 && args[0] == "--si-probe")
            {
                bool got = SingleInstance.TryAcquire();
                File.WriteAllText(Path.Combine(Dir, "si_probe.txt"), got ? "ACQUIRED" : "BLOCKED");
                Environment.Exit(0);
            }
            try { File.WriteAllText(OutPath, "", Encoding.UTF8); } catch { }
            try
            {
                Run();
            }
            catch (Exception ex)
            {
                W("[FATAL] " + ex);
            }
            W("SUMMARY pass=" + _pass + " fail=" + _fail);
            W("DONE");
            Environment.Exit(0);
        }

        private static void Run()
        {
            var backup = new Dictionary<string, string>();
            string[] keys = { "api.enabled", "api.url", "api.token", "api.timeout",
                              "api.local.enabled", "api.local.port", "api.local.token",
                              "rdp.agent.enabled", "rdp.agent.port", "rdp.agent.token",
                              "rdp.allowIp", "rdp.allowTime", "rdp.origDeny" };
            foreach (var k in keys) backup[k] = AppConfig.Get(k, "");

            try
            {
                // ---------- 1. 契约文档 ----------
                var s = Collector.Collect();
                string json = ApiReport.ToJson(s, null, true);
                File.WriteAllText(Path.Combine(Dir, "api_sample.json"), json, new UTF8Encoding(false));

                JsonDocument doc = null;
                try { doc = JsonDocument.Parse(json); } catch (Exception ex) { Check("JSON 可解析", false, ex.Message); }
                if (doc != null)
                {
                    var root = doc.RootElement;
                    Check("根节点必填字段", root.TryGetProperty("schema", out _) && root.TryGetProperty("schemaVersion", out _)
                        && root.TryGetProperty("reportId", out _) && root.TryGetProperty("collectedAt", out _));
                    Check("schema 标识", root.GetProperty("schema").GetString() == ApiReport.SchemaName,
                        root.GetProperty("schema").GetString());

                    var dev = root.GetProperty("device");
                    Check("deviceId 形如 pst-", dev.GetProperty("deviceId").GetString().StartsWith("pst-"),
                        dev.GetProperty("deviceId").GetString());
                    Check("设备基础字段非空", dev.GetProperty("computerName").GetString().Length > 0
                        && dev.GetProperty("osCaption").GetString().Length > 0);
                    Check("CPU 核心数为数字", dev.GetProperty("cpu").GetProperty("logicalCores").ValueKind == JsonValueKind.Number,
                        dev.GetProperty("cpu").GetProperty("logicalCores").GetRawText());

                    var ports = root.GetProperty("ports");
                    int total = ports.GetProperty("total").GetInt32();
                    int blocked = ports.GetProperty("blocked").GetInt32();
                    int exposed = ports.GetProperty("exposed").GetInt32();
                    int items = ports.GetProperty("items").GetArrayLength();
                    Check("端口总数/明细一致", total == 84 && items == 84 && blocked + exposed == 84,
                        "total=" + total + " items=" + items + " " + blocked + "+" + exposed);

                    var risk = root.GetProperty("risk");
                    Check("风险等级取值合法", risk.GetProperty("level").GetString() == "high"
                        || risk.GetProperty("level").GetString() == "medium"
                        || risk.GetProperty("level").GetString() == "low",
                        risk.GetProperty("level").GetString() + " score=" + risk.GetProperty("score").GetInt32());

                    var ep = risk.GetProperty("exposedPorts").GetArrayLength();
                    Check("暴露端口数组与计数一致", ep == exposed, "exposedPorts=" + ep);

                    // 紧凑 JSON 与缩进 JSON 语义等价
                    var compact = ApiReport.ToJson(s, root.GetProperty("reportId").GetString(), false);
                    Check("紧凑报文为单行", compact.IndexOf('\n') < 0 && compact.Length > 1000, "len=" + compact.Length);
                    doc.Dispose();
                }

                // ---------- 2. Schema 自身是合法 JSON ----------
                try
                {
                    using (var sd = JsonDocument.Parse(ApiReport.SchemaJson()))
                    {
                        Check("Schema 合法且含 $schema", sd.RootElement.GetProperty("$schema").GetString().Contains("json-schema"));
                    }
                }
                catch (Exception ex) { Check("Schema 合法", false, ex.Message); }

                // ---------- 3. 局域网代理：UDP 发现 + TCP 拉取 ----------
                const int lanPort = 18897;
                AppConfig.SetBool("api.lan.enabled", true);
                AppConfig.SetInt("api.lan.port", lanPort);
                AppConfig.Set("api.local.token", "");
                LanAgent.Stop();
                string lanErr = LanAgent.Start();
                Check("局域网代理启动", lanErr.Length == 0 && LanAgent.Running,
                      lanErr.Length > 0 ? lanErr : LanAgent.BaseUrl);

                if (LanAgent.Running)
                {
                    // UDP 单播探测应答
                    string reply = UdpProbe("127.0.0.1", lanPort);
                    Check("UDP 发现应答", reply.Contains("\"pst\"") && reply.Contains("pst-agent"), Head(reply));

                    // TCP 只读接口
                    string ping = Http("GET", "http://127.0.0.1:" + lanPort + "/pst/v1/ping", null, out int ps);
                    Check("GET /pst/v1/ping", ps == 200 && ping.Contains("\"pst\""), "HTTP " + ps + " " + Head(ping));

                    string dev = Http("GET", "http://127.0.0.1:" + lanPort + "/pst/v1/device", null, out int ds);
                    bool devOk = false;
                    string devId = "";
                    try
                    {
                        using (var d = JsonDocument.Parse(dev))
                        {
                            devId = d.RootElement.GetProperty("device").GetProperty("deviceId").GetString();
                            devOk = d.RootElement.GetProperty("ports").GetProperty("total").GetInt32() == 84;
                        }
                    }
                    catch { }
                    Check("GET /pst/v1/device", ds == 200 && devOk, "HTTP " + ds + " " + Head(dev));

                    // 令牌保护：设置令牌后不带令牌应被拒绝
                    AppConfig.Set("api.local.token", AppConfig.Protect("lan-secret"));
                    string noAuth = Http("GET", "http://127.0.0.1:" + lanPort + "/pst/v1/device", null, out int ns);
                    Check("拉取接口令牌保护", ns == 401, "HTTP " + ns + " " + Head(noAuth));

                    string withTok = Http("GET", "http://127.0.0.1:" + lanPort + "/pst/v1/device", "lan-secret", out int ts, "X-Api-Token");
                    bool tokOk = false;
                    try { using (var d = JsonDocument.Parse(withTok)) tokOk = d.RootElement.GetProperty("device").GetProperty("deviceId").GetString() == devId; }
                    catch { }
                    Check("带令牌可拉取数据", ts == 200 && tokOk, "HTTP " + ts);
                    AppConfig.Set("api.local.token", "");
                }
                LanAgent.Stop();

                // ---------- 4. 契约样例导出（供服务端联调） ----------
                string expDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PortSecurityTool", "api");
                try
                {
                    if (Directory.Exists(expDir)) Directory.Delete(expDir, true);
                }
                catch { }
                Directory.CreateDirectory(expDir);
                File.WriteAllText(Path.Combine(expDir, "schema_1.0.json"), ApiReport.SchemaJson(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(expDir, "example_1.0.json"), ApiReport.ToJson(Collector.Collect(), null, true), new UTF8Encoding(false));
                var files = Directory.GetFiles(expDir);
                Check("契约样例写入", files.Length == 2, string.Join(",", Array.ConvertAll(files, Path.GetFileName)));
                foreach (var f in files) W("       " + Path.GetFileName(f) + "  " + new FileInfo(f).Length + " bytes");

                // ---------- 9. 远程桌面：代理 / 局域网发现 / 临时放行 ----------
                try
                {
                    string origDeny = ReadDeny();
                    bool svcWasRunning = RdpAgent.TermServiceRunning();
                    bool hadBlockTcp = RuleExists("Block_Port_3389_TCP_in");
                    bool hadBlockUdp = RuleExists("Block_Port_3389_UDP_in");
                    W("       环境快照 fDeny=" + origDeny + " TermService=" + (svcWasRunning ? "运行" : "停止")
                      + " 原封堵规则 TCP=" + hadBlockTcp + " UDP=" + hadBlockUdp);

                    AppConfig.SetBool("rdp.agent.enabled", true);
                    AppConfig.SetInt("rdp.agent.port", 18761);
                    AppConfig.Set("rdp.agent.token", AppConfig.Protect("rdp-test-token"));
                    RdpAgent.Stop();
                    string aerr = RdpAgent.Start();
                    Check("远程请求代理启动", aerr.Length == 0 && RdpAgent.Running, aerr);

                    string ping = Http("GET", "http://127.0.0.1:18761/pst/v1/ping", null, out int ps);
                    Check("ping 可识别本工具", ping.Contains("\"pst\"") && ping.Contains(Environment.MachineName),
                          "HTTP " + ps + " " + Head(ping));

                    string noTok = Http("GET", "http://127.0.0.1:18761/pst/v1/state", null, out int ns);
                    Check("无令牌请求被拒绝", ns == 403 && noTok.Contains("令牌校验失败"), "HTTP " + ns);

                    string stJson = Http("GET", "http://127.0.0.1:18761/pst/v1/state", "rdp-test-token", out int ss);
                    Check("带令牌可查状态", ss == 200 && stJson.Contains("\"listen\""), Head(stJson));

                    // 无人确认场景（未挂接主窗体）应明确返回「拒绝」，而不是放行
                    string deny = Http("POST", "http://127.0.0.1:18761/pst/v1/rdp-request", "rdp-test-token", out int ds);
                    Check("未经确认的请求被拒绝", ds == 200 && deny.Contains("\"allowed\":\"0\""), Head(deny));

                    string scanMsg = NetDiscovery.StartScan("127.0.0.1/32");
                    for (int i = 0; i < 80 && NetDiscovery.Scanning; i++) Thread.Sleep(200);
                    string rows = NetDiscovery.Rows();
                    bool found = false;
                    foreach (var row in rows.Split('\u0001'))
                    {
                        var f = row.Split('\t');
                        if (f.Length >= 5 && f[0] == "127.0.0.1" && f[4] == "1") found = true;
                    }
                    Check("局域网发现能识别本机代理", found, scanMsg + " | " + Head(rows));

                    // 放行/恢复要改防火墙与注册表，必须提权；非提权环境如实跳过
                    if (Bridge.IsRunningAsAdmin())
                    {
                        // 放行只作用于 127.0.0.1，不会把 3389 暴露到网络
                        string msg;
                        bool ok = RdpAgent.AllowFor("127.0.0.1", out msg);
                        Check("临时放行请求方 IP", ok, msg);
                        Check("放行后远程桌面开关已打开", ReadDeny() == "0", ReadDeny());

                        RdpAgent.Restore(out msg);
                        Check("恢复后远程桌面开关还原", ReadDeny() == origDeny, ReadDeny() + " / 原值 " + origDeny);

                        // 还原测试机原本不存在的封堵规则
                        if (!hadBlockTcp) Netsh("advfirewall firewall delete rule name=\"Block_Port_3389_TCP_in\"");
                        if (!hadBlockUdp) Netsh("advfirewall firewall delete rule name=\"Block_Port_3389_UDP_in\"");
                        W("       放行/恢复全流程已执行并还原（TermService 原状态：" + (svcWasRunning ? "运行" : "停止") + "）");
                    }
                    else
                    {
                        W("       [SKIP] 当前进程未提权，放行/恢复（防火墙+注册表）未执行；正式程序以管理员运行");
                    }

                    RdpAgent.Stop();
                    Check("代理已停止", !RdpAgent.Running, "");
                }
                catch (Exception ex) { Check("远程桌面模块", false, ex.Message); }

                // ---------- 8. 单实例（命名互斥体） ----------
                try
                {
                    string holdPath = Path.Combine(Dir, "si_hold.txt");
                    string probePath = Path.Combine(Dir, "si_probe.txt");
                    try { File.Delete(holdPath); } catch { }
                    try { File.Delete(probePath); } catch { }

                    var host = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        Environment.ProcessPath, "--si-hold")
                    { UseShellExecute = false, CreateNoWindow = true });
                    Thread.Sleep(1800);   // 等子进程拿到互斥体

                    string held = File.Exists(holdPath) ? File.ReadAllText(holdPath).Trim() : "MISSING";
                    bool mine = SingleInstance.TryAcquire();     // 期望 false（已被子进程占用）

                    host.WaitForExit(15000);

                    var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        Environment.ProcessPath, "--si-probe")
                    { UseShellExecute = false, CreateNoWindow = true });
                    probe.WaitForExit(10000);
                    string after = File.Exists(probePath) ? File.ReadAllText(probePath).Trim() : "MISSING";

                    Check("已有实例持有时拒绝再占位", held == "ACQUIRED" && !mine, "hold=" + held + " second=" + mine);
                    Check("实例退出后互斥体自动释放", after == "ACQUIRED", after);
                }
                catch (Exception ex) { Check("单实例互斥", false, ex.Message); }

            }
            finally
            {
                // 还原用户原有配置
                foreach (var kv in backup)
                {
                    if (string.IsNullOrEmpty(kv.Value)) AppConfig.Set(kv.Key, "");
                    else AppConfig.Set(kv.Key, kv.Value);
                }
                try { LanAgent.Stop(); } catch { }
            }
        }


        // ---------------- 远程桌面测试用的小工具 ----------------

        private static string Netsh(string args)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netsh", args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) return "";
                    string o = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(10000);
                    return o;
                }
            }
            catch { return ""; }
        }

        private static string Proc(string exe, string args)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) return "";
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(8000);
                    return o;
                }
            }
            catch { return ""; }
        }

        private static bool RuleExists(string name)
        {
            var output = Netsh("advfirewall firewall show rule name=\"" + name + "\"");
            return output != null && output.IndexOf("Rule Name", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ReadDeny()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Terminal Server", false))
                {
                    if (k == null) return "?";
                    var v = k.GetValue("fDenyTSConnections");
                    return v == null ? "?" : Convert.ToInt32(v).ToString();
                }
            }
            catch { return "?"; }
        }

        private static string Http(string method, string url, string token, out int status, string tokenHeader = "X-Pst-Token")
        {
            status = 0;
            try
            {
                using (var c = new HttpClient())
                {
                    c.Timeout = TimeSpan.FromSeconds(10);
                    var req = new HttpRequestMessage(new HttpMethod(method), url);
                    if (!string.IsNullOrEmpty(token)) req.Headers.TryAddWithoutValidation(tokenHeader, token);
                    using (var r = c.SendAsync(req).GetAwaiter().GetResult())
                    {
                        status = (int)r.StatusCode;
                        return r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    }
                }
            }
            catch (Exception ex) { return "ERR:" + ex.Message; }
        }


        /// <summary>向目标发一次 UDP 发现探测，返回应答文本（超时 3 秒）。</summary>
        private static string UdpProbe(string ip, int port)
        {
            try
            {
                using (var udp = new UdpClient())
                {
                    udp.Client.ReceiveTimeout = 3000;
                    var data = Encoding.UTF8.GetBytes("PST-DISCOVER-V1");
                    udp.Send(data, data.Length, ip, port);
                    var ep = new IPEndPoint(IPAddress.Any, 0);
                    var buf = udp.Receive(ref ep);
                    return Encoding.UTF8.GetString(buf);
                }
            }
            catch (Exception ex) { return "ERR:" + ex.Message; }
        }

        private static string Head(string s)
        {
            if (s == null) return "<null>";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 160 ? s.Substring(0, 160) + "…" : s;
        }
    }
}
