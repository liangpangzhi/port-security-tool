using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PortSecurityTool
{
    /// <summary>
    /// 系统集成：开机自启（优先计划任务，回退注册表 Run 键）、单实例激活。
    /// </summary>
    internal static class SystemIntegration
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "NetworkPortSecurityTool";
        private const string TaskName = "NetworkPortSecurityTool";

        public const string WindowTitle = "网络信息采集和端口管理工具 1.0版";

        public static string StartupCommand()
        {
            return "\"" + Application.ExecutablePath + "\" --minimized";
        }

        // ---------------- 计划任务（最高权限，开机不弹 UAC） ----------------
        private static int RunSchtasks(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); } catch { }
                        return -1;
                    }
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        public static bool TaskExists()
        {
            return RunSchtasks("/query /tn \"" + TaskName + "\"") == 0;
        }

        private static bool CreateTask()
        {
            string tr = "\\\"" + Application.ExecutablePath + "\\\" --minimized";
            int rc = RunSchtasks("/create /tn \"" + TaskName + "\" /tr \"" + tr +
                                 "\" /sc onlogon /rl highest /f");
            return rc == 0;
        }

        private static void DeleteTask()
        {
            RunSchtasks("/delete /tn \"" + TaskName + "\" /f");
        }

        // ---------------- 注册表 Run 键（回退方案） ----------------
        private static bool RunKeyEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (k == null) return false;
                    var v = k.GetValue(ValueName) as string;
                    if (string.IsNullOrEmpty(v)) return false;
                    return v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        private static bool SetRunValue(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (k == null) return false;
                    if (on) k.SetValue(ValueName, StartupCommand(), RegistryValueKind.String);
                    else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                }
                return true;
            }
            catch { return false; }
        }

        private static void RemoveRunValue() { try { SetRunValue(false); } catch { } }

        // ---------------- 对外接口 ----------------
        public static bool AutoStartEnabled()
        {
            return TaskExists() || RunKeyEnabled();
        }

        /// <summary>返回实际采用的方式文字，便于界面提示。</summary>
        public static string AutoStartMethod()
        {
            if (TaskExists()) return "计划任务";
            if (RunKeyEnabled()) return "注册表 Run";
            return "未设置";
        }

        public static bool SetAutoStart(bool on)
        {
            if (on)
            {
                if (CreateTask())
                {
                    RemoveRunValue();   // 避免两种方式同时存在导致重复启动
                    return true;
                }
                return SetRunValue(true); // 计划任务被策略禁用时回退
            }

            DeleteTask();
            RemoveRunValue();
            return true;
        }

        // ---------------- 单实例 ----------------
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private static uint _showMsg;

        /// <summary>跨进程自定义消息：让已有实例把主窗口显示出来（托盘隐藏状态下也能被唤起）。</summary>
        public static uint ShowMainMessage
        {
            get
            {
                if (_showMsg == 0)
                {
                    try { _showMsg = RegisterWindowMessage("PstPortTool.ShowMain"); }
                    catch { _showMsg = 0x0400 + 0x101; }
                }
                return _showMsg;
            }
        }

        /// <summary>
        /// 若已有实例在运行，则把它显示到前台并返回 true。
        /// 带重试：第二个实例可能比第一个实例的窗口更早启动，短暂等待后仍找不到才放弃。
        /// </summary>
        public static bool ActivateExisting(int waitMs = 3000)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    var h = FindWindow(null, WindowTitle);
                    if (h != IntPtr.Zero)
                    {
                        uint msg = ShowMainMessage;
                        if (msg != 0) PostMessage(h, msg, IntPtr.Zero, IntPtr.Zero);
                        ShowWindow(h, 9);        // SW_RESTORE
                        SetForegroundWindow(h);
                        return true;
                    }
                }
                catch { return false; }
                if (sw.ElapsedMilliseconds >= waitMs) return false;
                Thread.Sleep(120);
            }
        }
    }
}
