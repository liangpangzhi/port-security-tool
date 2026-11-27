using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace PortSecurityTool
{
    // ============================================================
    //  本地查询服务（只监听 127.0.0.1，供服务端采集器 / 运维脚本拉取）
    //
    //  GET  /api/v1/health      健康检查
    //  GET  /api/v1/device      最近一次采集结果（?refresh=1 强制重新采集）
    //  GET  /api/v1/ports       仅端口封堵明细
    //  GET  /api/v1/schema      数据契约（JSON Schema）
    //  POST /api/v1/collect     立即采集并返回结果
    //  GET  /api/v1/report.xlsx 下载电子表格
    //
    //  鉴权：配置了 api.local.token 时，需带 X-Api-Token 头或 ?token= 参数。
    // ============================================================
    internal static class ApiServer
    {
        private static readonly object _lock = new object();
        private static HttpListener _listener;
        private static Thread _thread;
        private static volatile bool _running;
        private static int _port;

        private static string _cacheJson;
        private static DateTime _cacheAt = DateTime.MinValue;
        private static Collector.Snapshot _cacheSnap;

        public const int DefaultPort = 18760;

        public static bool Running { get { return _running; } }
        public static int Port { get { return _port; } }        // 0 = 未监听

        public static int ConfiguredPort
        {
            get
            {
                int p = AppConfig.GetInt("api.local.port", DefaultPort);
                if (p < 1024 || p > 65535) p = DefaultPort;
                return p;
            }
        }

        public static bool Enabled { get { return AppConfig.GetBool("api.local.enabled", false); } }

        public static string LocalToken { get { return AppConfig.Unprotect(AppConfig.Get("api.local.token", "")); } }

        public static string BaseUrl
        {
            get { return "http://127.0.0.1:" + (_running && _port > 0 ? _port : ConfiguredPort) + "/api/v1"; }
        }

        // ---------------- 启停 ----------------

        /// <summary>启动服务；失败时返回错误说明，成功返回空串。</summary>
        public static string Start()
        {
            lock (_lock)
            {
                if (_running) return "";
                int port = ConfiguredPort;
                try
                {
                    var l = new HttpListener();
                    l.Prefixes.Add("http://127.0.0.1:" + port + "/");
                    try { l.Prefixes.Add("http://localhost:" + port + "/"); } catch { }
                    l.Start();
                    _listener = l;
                    _port = port;
                    _running = true;
                    _thread = new Thread(Loop) { IsBackground = true, Name = "PstApiServer" };
                    _thread.Start();
                    return "";
                }
                catch (Exception ex)
                {
                    _running = false;
                    _port = 0;
                    try { if (_listener != null) _listener.Close(); } catch { }
                    _listener = null;
                    string hint = "";
                    if (ex is HttpListenerException)
                    {
                        int code = ((HttpListenerException)ex).ErrorCode;
                        if (code == 5) hint = "（端口 " + port + " 注册被拒绝，请换端口或确认以管理员身份运行）";
                        else if (code == 32) hint = "（端口 " + port + " 已被其他程序占用）";
                    }
                    return "本地服务启动失败：" + ex.Message + hint;
                }
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                _running = false;
                try { if (_listener != null) { _listener.Stop(); _listener.Close(); } } catch { }
                _listener = null;
                _port = 0;
            }
        }

        /// <summary>按配置决定是否启动（设置变更后调用）。</summary>
        public static void ApplyConfig()
        {
            if (Enabled) { if (!_running) Start(); }
            else Stop();
        }

        // ---------------- 请求循环 ----------------

        private static void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }            // Stop() 后 GetContext 抛异常，退出循环
                try { ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); }); }
                catch { try { ctx.Response.Abort(); } catch { } }
            }
        }

        private static string Path_(HttpListenerContext ctx)
        {
            var p = ctx.Request.Url.AbsolutePath ?? "/";
            if (p.Length > 1) p = p.TrimEnd('/');
            return p.ToLowerInvariant();
        }

        private static void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            try
            {
                res.Headers["Access-Control-Allow-Origin"] = "*";
                res.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Api-Token, Authorization";
                res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                res.Headers["Cache-Control"] = "no-store";

                if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }

                if (!Authorized(ctx))
                {
                    Json(res, 401, Err("unauthorized", "缺少或错误的访问令牌，请在请求头 X-Api-Token 中提供"));
                    return;
                }

                string path = Path_(ctx);
                switch (path)
                {
                    case "/":
                    case "/api":
                    case "/api/v1":
                        Html(res, IndexPage());
                        return;

                    case "/api/v1/health":
                        Json(res, 200, Health());
                        return;

                    case "/api/v1/schema":
                        JsonRaw(res, 200, ApiReport.SchemaJson());
                        return;

                    case "/api/v1/device":
                        {
                            bool refresh = (req.QueryString["refresh"] ?? "") == "1";
                            JsonRaw(res, 200, DeviceJson(refresh));
                            return;
                        }

                    case "/api/v1/ports":
                        {
                            bool refresh = (req.QueryString["refresh"] ?? "") == "1";
                            var doc = ApiReport.Build(Snapshot(refresh), null);
                            var node = doc.Get("ports");
                            JsonRaw(res, 200, node == null ? "{}" : node.ToJson(true));
                            return;
                        }

                    case "/api/v1/collect":
                        {
                            if (req.HttpMethod != "POST" && req.HttpMethod != "GET")
                            {
                                Json(res, 405, Err("method_not_allowed", "请使用 POST /api/v1/collect"));
                                return;
                            }
                            Collector.Snapshot s;
                            string json = DeviceJson(true, out s);
                            JsonRaw(res, 200, json);
                            return;
                        }

                    case "/api/v1/report.xlsx":
                        {
                            var s = Snapshot(false);
                            var path2 = Collector.BuildFile(s, Path.Combine(Collector.ReportDirectory, "api"));
                            var bytes = File.ReadAllBytes(path2);
                            res.StatusCode = 200;
                            res.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                            res.AddHeader("Content-Disposition",
                                "attachment; filename=\"" + Path.GetFileName(path2) + "\"");
                            res.ContentLength64 = bytes.Length;
                            res.OutputStream.Write(bytes, 0, bytes.Length);
                            res.Close();
                            return;
                        }

                    default:
                        Json(res, 404, Err("not_found", "未知接口：" + path + "，可用接口见 GET /api/v1"));
                        return;
                }
            }
            catch (Exception ex)
            {
                try { Json(res, 500, Err("internal_error", ex.Message)); } catch { }
            }
            finally
            {
                try { res.Close(); } catch { }
            }
        }

        private static bool Authorized(HttpListenerContext ctx)
        {
            var token = LocalToken;
            if (token.Length == 0) return true;
            var given = ctx.Request.Headers["X-Api-Token"];
            if (string.IsNullOrEmpty(given))
            {
                var auth = ctx.Request.Headers["Authorization"];
                if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    given = auth.Substring(7).Trim();
            }
            if (string.IsNullOrEmpty(given)) given = ctx.Request.QueryString["token"];
            return string.Equals(given, token, StringComparison.Ordinal);
        }

        // ---------------- 数据 ----------------

        /// <summary>带短缓存的快照：默认 20 秒内复用，避免频繁 WMI 采集。</summary>
        private static Collector.Snapshot Snapshot(bool refresh)
        {
            lock (_lock)
            {
                if (!refresh && _cacheSnap != null && (DateTime.Now - _cacheAt).TotalSeconds < 20)
                    return _cacheSnap;
                var s = Collector.Collect();
                _cacheSnap = s;
                _cacheAt = DateTime.Now;
                _cacheJson = ApiReport.ToJson(s, null, true);
                return s;
            }
        }

        private static string DeviceJson(bool refresh)
        {
            Collector.Snapshot s;
            return DeviceJson(refresh, out s);
        }

        private static string DeviceJson(bool refresh, out Collector.Snapshot snap)
        {
            lock (_lock)
            {
                snap = Snapshot(refresh);
                if (!refresh && _cacheJson != null) return _cacheJson;
                _cacheJson = ApiReport.ToJson(snap, null, true);
                return _cacheJson;
            }
        }

        private static string Health()
        {
            var o = new JObj()
                .Put("status", "ok")
                .Put("schema", ApiReport.SchemaName)
                .Put("schemaVersion", ApiReport.SchemaVersion)
                .Put("agentVersion", ApiReport.AgentVersionText)
                .Put("computerName", Environment.MachineName)
                .Put("serverTime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .PutNum("uptimeSec", (long)(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalSeconds)
                .PutBool("cached", _cacheSnap != null)
                .PutArr("endpoints", new JArr()
                    .Add("/api/v1/health")
                    .Add("/api/v1/device")
                    .Add("/api/v1/ports")
                    .Add("/api/v1/schema")
                    .Add("/api/v1/collect")
                    .Add("/api/v1/report.xlsx"));
            return o.ToJson(true);
        }

        private static string Err(string code, string message)
        {
            return new JObj().PutBool("ok", false).Put("code", code).Put("message", message).ToJson(true);
        }

        // ---------------- HTTP 输出 ----------------

        private static void Json(HttpListenerResponse res, int status, string json)
        {
            JsonRaw(res, status, json);
        }

        private static void JsonRaw(HttpListenerResponse res, int status, string json)
        {
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(json ?? "{}");
                res.StatusCode = status;
                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch { }
        }

        private static void Html(HttpListenerResponse res, string html)
        {
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(html);
                res.StatusCode = 200;
                res.ContentType = "text/html; charset=utf-8";
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch { }
        }

        private static string IndexPage()
        {
            return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>本地数据接口</title>"
                + "<style>body{background:#0b1220;color:#cfe4f4;font:13px/1.8 'Microsoft YaHei',sans-serif;padding:28px}"
                + "h1{font-size:17px;color:#22d3ee;font-weight:600}code{color:#6fe0b8}"
                + "table{border-collapse:collapse;margin-top:10px}td{padding:5px 16px 5px 0;vertical-align:top}"
                + ".m{color:#6e93b2}</style></head><body>"
                + "<h1>网络信息采集和端口管理工具 · 本地数据接口</h1>"
                + "<p class=\"m\">契约 " + ApiReport.SchemaName + " / v" + ApiReport.SchemaVersion
                + "　计算机：" + Environment.MachineName + "</p>"
                + "<table>"
                + "<tr><td><code>GET /api/v1/health</code></td><td class=\"m\">服务状态</td></tr>"
                + "<tr><td><code>GET /api/v1/device</code></td><td class=\"m\">最近采集结果（?refresh=1 重新采集）</td></tr>"
                + "<tr><td><code>GET /api/v1/ports</code></td><td class=\"m\">端口封堵明细</td></tr>"
                + "<tr><td><code>GET /api/v1/schema</code></td><td class=\"m\">数据契约（JSON Schema）</td></tr>"
                + "<tr><td><code>POST /api/v1/collect</code></td><td class=\"m\">立即采集</td></tr>"
                + "<tr><td><code>GET /api/v1/report.xlsx</code></td><td class=\"m\">下载电子表格</td></tr>"
                + "</table></body></html>";
        }
    }
}
