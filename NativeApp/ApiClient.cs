using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PortSecurityTool
{
    // ============================================================
    //  HTTP 推送通道
    //
    //  采集完成后把标准 JSON 文档 POST 到服务端接口：
    //     POST {api.url}
    //     Content-Type: application/json; charset=utf-8
    //     Authorization: Bearer {api.token}
    //     X-PST-Schema / X-PST-Report-Id / X-PST-Computer
    //  服务端返回 2xx 视为成功；失败时把报文留在本地待发队列（outbox），
    //  下次采集或下次启动自动补发，避免终端离线丢数据。
    // ============================================================
    internal static class ApiClient
    {
        private static readonly object _lock = new object();
        private static HttpClient _http;

        public sealed class PushResult
        {
            public bool Ok;
            public int Status;
            public string Message = "";
            public string ReportId = "";
            public bool Queued;      // 是否已进入待发队列
            public string Url = "";
        }

        private static HttpClient Client
        {
            get
            {
                if (_http != null) return _http;
                lock (_lock)
                {
                    if (_http != null) return _http;
                    var handler = new SocketsHttpHandler
                    {
                        UseProxy = false,                       // 内网直连，避免系统代理干扰
                        AllowAutoRedirect = true,
                        ConnectTimeout = TimeSpan.FromSeconds(6),
                        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
                    };
                    _http = new HttpClient(handler);
                    _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // 由 CancellationToken 控制
                    _http.DefaultRequestHeaders.UserAgent.ParseAdd("PortSecurityTool-Agent/" + ApiReport.SchemaVersion);
                    return _http;
                }
            }
        }

        public static string OutboxDir
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PortSecurityTool", "outbox");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static bool Enabled { get { return AppConfig.GetBool("api.enabled", false); } }
        public static string Url { get { return AppConfig.Get("api.url", "").Trim(); } }
        public static string Token { get { return AppConfig.Unprotect(AppConfig.Get("api.token", "")); } }
        public static int TimeoutSec
        {
            get
            {
                int t = AppConfig.GetInt("api.timeout", 8);
                if (t < 3) t = 3;
                if (t > 120) t = 120;
                return t;
            }
        }

        // ---------------- 单次发送 ----------------

        private static PushResult Post(string url, string json, string reportId, string computer, string token, int timeoutSec, bool spool)
        {
            var r = new PushResult { ReportId = reportId, Url = url };
            if (string.IsNullOrEmpty(url))
            {
                r.Message = "未配置接口地址";
                return r;
            }
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec)))
                {
                    req.Content = new StringContent(json, new UTF8Encoding(false), "application/json");
                    req.Headers.TryAddWithoutValidation("X-PST-Schema", ApiReport.SchemaName + "/" + ApiReport.SchemaVersion);
                    if (!string.IsNullOrEmpty(reportId)) req.Headers.TryAddWithoutValidation("X-PST-Report-Id", reportId);
                    // HTTP 头只能放 ASCII：计算机名含中文时按 UTF-8 百分号编码（服务端 UrlDecode 即可还原）
                    if (!string.IsNullOrEmpty(computer))
                        req.Headers.TryAddWithoutValidation("X-PST-Computer",
                            IsAscii(computer) ? computer : Uri.EscapeDataString(computer));
                    if (!string.IsNullOrEmpty(token))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                    using (var resp = Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                                            .GetAwaiter().GetResult())
                    {
                        r.Status = (int)resp.StatusCode;
                        string body = "";
                        try { body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult(); } catch { }
                        if (body != null && body.Length > 300) body = body.Substring(0, 300);

                        if (r.Status >= 200 && r.Status < 300)
                        {
                            r.Ok = true;
                            r.Message = "HTTP " + r.Status + (string.IsNullOrWhiteSpace(body) ? "" : "：" + body.Trim());
                        }
                        else if (r.Status == 401 || r.Status == 403)
                        {
                            r.Message = "HTTP " + r.Status + " 认证失败（请检查令牌）";
                        }
                        else
                        {
                            r.Message = "HTTP " + r.Status + (string.IsNullOrWhiteSpace(body) ? "" : "：" + body.Trim());
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                r.Message = "请求超时（" + timeoutSec + " 秒）";
            }
            catch (HttpRequestException ex)
            {
                r.Message = "网络不可达：" + Innermost(ex);
            }
            catch (Exception ex)
            {
                r.Message = "发送失败：" + ex.Message;
            }

            if (!r.Ok && spool && json.Length > 0)
            {
                try
                {
                    var name = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + (reportId ?? Guid.NewGuid().ToString("N")) + ".json";
                    File.WriteAllText(Path.Combine(OutboxDir, name), json, new UTF8Encoding(false));
                    r.Queued = true;
                }
                catch { }
            }
            return r;
        }

        private static bool IsAscii(string s)
        {
            foreach (char c in s) if (c > 126 || c < 32) return false;
            return true;
        }

        private static string Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }

        /// <summary>按当前配置推送一份快照（失败自动入队）。</summary>
        public static PushResult Push(Collector.Snapshot s, string reportId)
        {
            if (string.IsNullOrEmpty(reportId)) reportId = Guid.NewGuid().ToString("N");
            string json = ApiReport.ToJson(s, reportId, false);
            return Post(Url, json, reportId, s.Computer, Token, TimeoutSec, true);
        }

        /// <summary>用指定地址/令牌测试连通性（不入队，供界面「测试推送」使用）。</summary>
        public static PushResult Test(string url, string token, int timeoutSec)
        {
            var s = Collector.Collect();
            var reportId = "test-" + Guid.NewGuid().ToString("N");
            string json = ApiReport.ToJson(s, reportId, false);
            return Post(url, json, reportId, s.Computer, token, timeoutSec <= 0 ? 8 : timeoutSec, false);
        }

        // ---------------- 待发队列 ----------------

        public static int OutboxCount()
        {
            try { return Directory.GetFiles(OutboxDir, "*.json").Length; }
            catch { return 0; }
        }

        /// <summary>补发队列中的报文，返回 "成功数/总数"。4xx（非 408/429）视为不可重试，归档为 .rejected。</summary>
        public static string FlushOutbox(int max)
        {
            if (!Enabled || Url.Length == 0) return "0/0";
            string[] files;
            try { files = Directory.GetFiles(OutboxDir, "*.json"); }
            catch { return "0/0"; }
            if (files.Length == 0) return "0/0";
            Array.Sort(files, StringComparer.Ordinal);

            int ok = 0, total = 0;
            foreach (var f in files)
            {
                if (total >= max) break;
                total++;
                try
                {
                    string json = File.ReadAllText(f, Encoding.UTF8);
                    var name = Path.GetFileNameWithoutExtension(f);
                    int us = name.IndexOf('_');
                    string reportId = us > 0 ? name.Substring(us + 1) : name;
                    var r = Post(Url, json, reportId, "", Token, TimeoutSec, false);
                    if (r.Ok) { try { File.Delete(f); } catch { } ok++; }
                    else if (r.Status >= 400 && r.Status < 500 && r.Status != 408 && r.Status != 429)
                    {
                        try { File.Move(f, Path.ChangeExtension(f, ".rejected")); } catch { }
                    }
                    else break;  // 网络类错误：保留队列，下次再试
                }
                catch { }
            }
            return ok + "/" + total;
        }
    }
}
