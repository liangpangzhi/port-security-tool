using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PortSecurityTool
{
    internal static class Program
    {
        private const string SkipElevateArg = "--no-reelevate";
        private const string MinimizedArg = "--minimized";

        private static bool IsElevated()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            bool skipElevate = false, startMinimized = false;
            foreach (var a in args)
            {
                if (string.Equals(a, SkipElevateArg, StringComparison.OrdinalIgnoreCase)) skipElevate = true;
                else if (string.Equals(a, MinimizedArg, StringComparison.OrdinalIgnoreCase)) startMinimized = true;
            }

            // 强制管理员运行：清单已声明 requireAdministrator，这里再做一次兜底，
            // 防止清单被绕过（被其他进程以普通令牌拉起等）时程序半残运行。
            if (!IsElevated() && !skipElevate)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = Application.ExecutablePath,
                        Arguments = SkipElevateArg + (startMinimized ? " " + MinimizedArg : ""),
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    Process.Start(psi);
                    return; // 已交给提权后的实例
                }
                catch
                {
                    MessageBox.Show(
                        "本程序需要管理员权限才能执行端口封堵与安全加固。\n\n" +
                        "请关闭本窗口，右键程序图标 → “以管理员身份运行”。",
                        "需要管理员权限",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            // 注册代码页提供程序，保证 GBK(936) 可用（读写批处理/报告与解析 netsh 中文输出）
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }

            // 让内嵌 WebBrowser 使用系统最高可用 IE/Edge 内核版本，保证 CSS 渲染一致
            try
            {
                const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings\FEATURE_BROWSER_EMULATION";
                string exe = System.IO.Path.GetFileName(Application.ExecutablePath);
                // 11001 = IE11 强制标准模式
                Registry.SetValue(key, exe, 11001, RegistryValueKind.DWord);
            }
            catch { /* 注册表不可写时忽略，不影响功能 */ }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 单实例：命名互斥体先占位（无竞态），拿不到说明已有实例在跑，
            // 把它唤到前台后本进程直接退出；开机自启的静默启动不抢焦点。
            if (!SingleInstance.TryAcquire())
            {
                if (!startMinimized) SystemIntegration.ActivateExisting();
                return;
            }

            Application.Run(new MainForm(startMinimized));
        }
    }
}
