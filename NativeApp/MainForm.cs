using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PortSecurityTool
{
    public class MainForm : Form
    {
        private readonly WebBrowser _browser;
        private readonly Bridge _bridge;

        private NotifyIcon _tray;
        private ContextMenuStrip _trayMenu;
        private bool _reallyExit;
        private bool _trayHintShown;
        private readonly bool _startMinimized;
        private Timer _autoTimer;
        private volatile bool _collecting;

        internal WebBrowser Browser { get { return _browser; } }

        public MainForm() : this(false) { }

        public MainForm(bool startMinimized)
        {
            _startMinimized = startMinimized;

            Text = SystemIntegration.WindowTitle;
            try
            {
                var iconStream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("PortSecurityTool.app.ico");
                if (iconStream != null)
                    using (iconStream) Icon = new Icon(iconStream);
            }
            catch { /* 图标加载失败不影响运行 */ }
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(6, 16, 29);
            ShowInTaskbar = true;
            int w = 1240, h = 820;
            Size = new Size(w, h);
            Location = new Point(
                Math.Max(0, (Screen.PrimaryScreen.Bounds.Width - w) / 2),
                Math.Max(0, (Screen.PrimaryScreen.Bounds.Height - h) / 2));

            _bridge = new Bridge(this);
            _browser = new WebBrowser
            {
                Dock = DockStyle.Fill,
                ObjectForScripting = _bridge,
                ScrollBarsEnabled = false,
                AllowWebBrowserDrop = false,
                // 必须保留编辑快捷键（Ctrl+A/C/V/X），否则输入框无法粘贴；
                // 右键菜单只在输入框内由脚本放行，其它区域仍由 oncontextmenu 拦截。
                WebBrowserShortcutsEnabled = true,
                IsWebBrowserContextMenuEnabled = true
            };
            Controls.Add(_browser);

            // 内嵌 HTML 直接加载，无外部文件依赖
            _browser.DocumentCompleted += Browser_DocumentCompleted;
            _browser.DocumentText = LoadEmbeddedHtml();

            InitTray();
            RestoreAutoCollect();
            InitApiServer();
        }

        /// <summary>按配置启动本地数据接口，并补发上离线期间积压的报文。</summary>
        private void InitApiServer()
        {
            try { if (ApiServer.Enabled) ApiServer.Start(); } catch { }
            try
            {
                Task.Run(delegate
                {
                    try { ApiClient.FlushOutbox(20); } catch { }
                });
            }
            catch { }
        }

        // ---------------- 托盘 ----------------
        private void InitTray()
        {
            try
            {
                _trayMenu = new ContextMenuStrip();
                _trayMenu.Items.Add("显示主界面", null, delegate { ShowMain(); });
                _trayMenu.Items.Add("立即采集并上报", null, delegate { RunCollect(true); });
                _trayMenu.Items.Add(new ToolStripSeparator());
                _trayMenu.Items.Add("退出", null, delegate { RealExit(); });

                _tray = new NotifyIcon
                {
                    Icon = Icon != null ? Icon : SystemIcons.Application,
                    Text = "网络信息采集和端口管理工具",
                    Visible = true,
                    ContextMenuStrip = _trayMenu
                };
                _tray.DoubleClick += delegate { ShowMain(); };
            }
            catch { }
        }

        public void ShowMain()
        {
            try
            {
                Show();
                WindowState = FormWindowState.Normal;
                ShowInTaskbar = true;
                Activate();
                BringToFront();
            }
            catch { }
        }

        public void HideToTray()
        {
            try { EmbeddedRemote.CloseAll(); } catch { }
            try
            {
                Hide();
                ShowInTaskbar = false;
                if (!_trayHintShown && _tray != null)
                {
                    _trayHintShown = true;
                    _tray.ShowBalloonTip(4000, "已最小化到托盘",
                        "程序仍在后台运行，端口封堵与定时采集不受影响。\n双击托盘图标可重新打开。",
                        ToolTipIcon.Info);
                }
            }
            catch { }
        }

        /// <summary>界面「关闭」按钮 / 右上角 × 的统一入口。</summary>
        public void RequestClose()
        {
            if (AppConfig.GetBool("ui.tray", true)) HideToTray();
            else RealExit();
        }

        public void RealExit()
        {
            _reallyExit = true;
            try { EmbeddedRemote.CloseAll(); } catch { }
            try { if (_autoTimer != null) _autoTimer.Stop(); } catch { }
            try { ApiServer.Stop(); } catch { }
            try
            {
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                    _tray = null;
                }
            }
            catch { }
            try { Close(); } catch { }
            Application.ExitThread();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing && AppConfig.GetBool("ui.tray", true))
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            // 第二个实例启动时发来的「显示主界面」消息（含托盘隐藏状态）
            try
            {
                if (m.Msg == (int)SystemIntegration.ShowMainMessage)
                {
                    ShowMain();
                    return;
                }
            }
            catch { }
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_startMinimized) HideToTray();
        }

        // ---------------- 定时采集 ----------------
        public void RestoreAutoCollect()
        {
            try
            {
                if (_autoTimer == null)
                {
                    _autoTimer = new Timer();
                    _autoTimer.Tick += delegate { RunCollect(true); };
                }
                RestartAutoCollect();
            }
            catch { }
        }

        public void RestartAutoCollect()
        {
            try
            {
                if (_autoTimer == null) return;
                _autoTimer.Stop();
                bool on = AppConfig.GetBool("collect.auto", false);
                int minutes = AppConfig.GetInt("collect.interval", 60);
                if (minutes < 1) minutes = 1;
                if (minutes > 10080) minutes = 10080;
                if (on)
                {
                    _autoTimer.Interval = minutes * 60 * 1000;
                    _autoTimer.Start();
                }
            }
            catch { }
        }

        /// <summary>执行一次采集上报（后台线程，不阻塞界面）。fromTimer=true 表示由定时器触发。</summary>
        public void RunCollect(bool fromTimer)
        {
            if (_collecting) return;
            _collecting = true;

            string ip = AppConfig.Get("collect.ip", "");
            string share = AppConfig.Get("collect.share", "");
            string user = AppConfig.Get("collect.user", "");
            string pass = AppConfig.Unprotect(AppConfig.Get("collect.pass", ""));

            Task.Run(delegate
            {
                Collector.SendResult r;
                try
                {
                    r = Collector.CollectAndSend(ip, share, user, pass);
                }
                catch (Exception ex)
                {
                    r = new Collector.SendResult { Ok = false, Message = "采集异常：" + ex.Message };
                }

                try
                {
                    AppConfig.Set("collect.lastTime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    AppConfig.Set("collect.lastResult", r.Message.Replace("\r", "").Replace("\n", " | "));
                }
                catch { }

                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        _collecting = false;
                        NotifyJs(r.Ok ? "ok" : "warn", "采集上报：" + r.Message.Replace("\n", " "));
                        if (fromTimer && !Visible && _tray != null)
                            _tray.ShowBalloonTip(3000, "定时采集",
                                r.Ok ? "上报成功：" + System.IO.Path.GetFileName(r.LocalFile) : "上报失败，详见主界面日志",
                                r.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
                    }));
                }
                catch { _collecting = false; }
            });
        }

        // ---------------- 远程桌面页刷新（WebBrowser 里 setTimeout 不触发，只能由宿主定时推） ----------------
        private Timer _rdpTimer;

        /// <summary>开始扫描或发出远程请求后调用，让宿主按节拍把进度推给界面。</summary>
        public void BeginRdpTicks()
        {
            try
            {
                if (_rdpTimer == null)
                {
                    _rdpTimer = new Timer { Interval = 600 };
                    _rdpTimer.Tick += RdpTick;
                }
                if (!_rdpTimer.Enabled) _rdpTimer.Start();
            }
            catch { }
        }

        private void RdpTick(object sender, EventArgs e)
        {
            try
            {
                InvokeJs("typeof rdpTick==='function' && rdpTick();");
                if (!NetDiscovery.Scanning && !RdpClient.Pending)
                {
                    _rdpTimer.Stop();
                    InvokeJs("typeof rdpTick==='function' && rdpTick();");   // 补最后一帧
                }
            }
            catch { }
        }

        private void InvokeJs(string script)
        {
            try
            {
                if (_browser.Document == null) return;
                _browser.Document.InvokeScript("eval", new object[] { script });
            }
            catch { }
        }

        /// <summary>供原生模块往界面日志里写一行（被请求方弹窗、放行结果等）。</summary>
        public void NotifyLog(string kind, string text) { NotifyJs(kind, text); }

        /// <summary>把信息写入界面底部执行日志（界面未就绪时静默忽略）。</summary>
        private void NotifyJs(string kind, string text)
        {
            try
            {
                if (_browser.Document == null) return;
                var safe = (text ?? "")
                    .Replace("\\", "\\\\")
                    .Replace("'", "\\'")
                    .Replace("\r", "")
                    .Replace("\n", " ");
                _browser.Document.InvokeScript("eval", new object[]
                {
                    "typeof log==='function' && log('" + safe + "','" + kind + "');"
                });
            }
            catch { }
        }

        private bool _bridgeChecked;
        private void Browser_DocumentCompleted(object sender, WebBrowserDocumentCompletedEventArgs e)
        {
            if (_bridgeChecked) return;
            _bridgeChecked = true;
            try
            {
                // 验证 JS 可访问原生桥接层；失败时在日志区给出明确提示
                var ok = _browser.Document.InvokeScript("eval", new object[]
                {
                    "(function(){try{return !!(window.external && window.external.IsAdmin);}catch(e){return false;}})()"
                });
                var bridgeOk = ok != null && ok.ToString() == "True";
                if (!bridgeOk)
                    _browser.Document.InvokeScript("eval", new object[]
                    {
                        "typeof log==='function' && log('原生桥接层不可用，请检查运行环境。','err');"
                    });
            }
            catch { }
        }

        private static string LoadEmbeddedHtml()
        {
            // 目录版优先加载 exe 同级 ui.html，便于直接改 HTA/界面后免重编译；
            // 找不到外部文件时回退到编译进程序集的 ui.html。
            try
            {
                string external = Path.Combine(AppContext.BaseDirectory, "ui.html");
                if (File.Exists(external))
                    return File.ReadAllText(external, Encoding.UTF8);
            }
            catch { }

            var asm = Assembly.GetExecutingAssembly();
            using (var stream = asm.GetManifestResourceStream("PortSecurityTool.ui.html"))
            {
                if (stream == null) return "<html><body>界面资源加载失败</body></html>";
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        // 无边框窗口的拖动由顶栏 mousedown → Bridge.StartDrag() 交给系统完成
        // （WM_NCHITTEST 方案不可行：WebBrowser 子窗口铺满客户区，主窗体收不到该消息）
    }
}
