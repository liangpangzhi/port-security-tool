using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace PortSecurityTool
{
    // ============================================================
    //  内置远程桌面：SubnetDesk
    //
    //  · 远程桌面模块已整体切换为 SubnetDesk 客户端
    //  · SubnetDesk 便携包以 zip 形式嵌入主 exe
    //  · 首次使用时解压到 %LOCALAPPDATA%\PortSecurityTool\remote\<版本>
    //  · 之后直接复用，不再解压
    // ============================================================
    internal static class EmbeddedRemote
    {
        private const string ResourceName = "PortSecurityTool.subnetdesk-remote.zip";
        private const string RemoteVersion = "2.0.0";
        private static readonly object _lock = new object();

        /// <summary>启动 SubnetDesk；优先用 exe 同级 remote\SubnetDesk，找不到再用内嵌资源。</summary>
        public static string Open(string target)
        {
            try
            {
                // 目录版：SubnetDesk 与主程序同级，便于直接替换/升级
                string externalDir = Path.Combine(AppContext.BaseDirectory, "remote", "SubnetDesk");
                string externalExe = Path.Combine(externalDir, "SubnetDesk.exe");
                if (File.Exists(externalExe))
                    return Launch(externalExe, externalDir, target);

                string dir = EnsureExtracted();
                string exe = Path.Combine(dir, "SubnetDesk.exe");
                if (!File.Exists(exe))
                    return "ERR|远程桌面不完整，缺少 SubnetDesk.exe（请检查 remote\\SubnetDesk 目录）";

                return Launch(exe, dir, target);
            }
            catch (Exception ex)
            {
                return "ERR|启动 SubnetDesk 失败：" + ex.Message;
            }
        }

        private static string Launch(string exe, string dir, string target)
        {
            var psi = new ProcessStartInfo(exe);
            psi.WorkingDirectory = dir;
            psi.UseShellExecute = true;
            string ip = (target ?? "").Trim();
            if (ip.Length > 0)
                psi.Arguments = "--connect \"" + ip.Replace("\"", "") + "\"";
            Process.Start(psi);
            return "OK|已启动 SubnetDesk 远程桌面" + (ip.Length > 0 ? "：" + ip : "");
        }

        /// <summary>结束由本工具启动的 SubnetDesk 进程（含子进程），不影响其他安装。</summary>
        public static string CloseAll()
        {
            try
            {
                string[] roots =
                {
                    Path.Combine(AppContext.BaseDirectory, "remote", "SubnetDesk"),
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "PortSecurityTool", "remote")
                };

                int killed = 0;
                foreach (var p in Process.GetProcessesByName("SubnetDesk"))
                {
                    try
                    {
                        string path = "";
                        try { path = p.MainModule != null ? p.MainModule.FileName : ""; } catch { }

                        bool owned = false;
                        if (path.Length > 0)
                        {
                            foreach (var root in roots)
                            {
                                if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                                {
                                    owned = true;
                                    break;
                                }
                            }
                        }
                        if (!owned) continue;

                        p.Kill(true);
                        try { p.WaitForExit(3000); } catch { }
                        killed++;
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }

                return killed > 0
                    ? "OK|已关闭 SubnetDesk（" + killed + " 个进程）"
                    : "OK|SubnetDesk 未在运行";
            }
            catch (Exception ex)
            {
                return "ERR|关闭 SubnetDesk 失败：" + ex.Message;
            }
        }

        private static string EnsureExtracted()
        {
            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PortSecurityTool", "remote", RemoteVersion);
            string exe = Path.Combine(baseDir, "SubnetDesk.exe");
            if (File.Exists(exe)) return baseDir;

            lock (_lock)
            {
                if (File.Exists(exe)) return baseDir;

                string parent = Path.GetDirectoryName(baseDir);
                Directory.CreateDirectory(parent);

                string tmp = baseDir + ".tmp-" + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(tmp);
                try
                {
                    string zip = Path.Combine(tmp, "remote.zip");
                    using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                    {
                        if (s == null)
                            throw new InvalidOperationException("内置远程桌面资源不存在");
                        using (var f = File.Create(zip)) s.CopyTo(f);
                    }

                    string files = Path.Combine(tmp, "files");
                    Directory.CreateDirectory(files);
                    ZipFile.ExtractToDirectory(zip, files);
                    File.Delete(zip);

                    if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
                    Directory.Move(files, baseDir);
                }
                finally
                {
                    try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
                }
            }

            return baseDir;
        }
    }
}
