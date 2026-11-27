using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace PortSecurityTool
{
    // 交互式诊断探针：读取 probe_cmd.txt 中的 JS 表达式逐条求值，结果追加到 probe_out.txt
    internal static class ProbeProgram
    {
        private static MainForm _form;
        private static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string CmdPath = Path.Combine(Dir, "probe_cmd.txt");
        private static readonly string OutPath = Path.Combine(Dir, "probe_out.txt");

        [STAThread]
        private static void Main()
        {
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            _form = new MainForm();
            _form.Shown += (s, e) =>
            {
                var t = new Timer { Interval = 400 };
                t.Tick += (a, b) => Poll();
                t.Start();
            };
            Application.Run(_form);
        }

        private static string Eval(string js)
        {
            try
            {
                var r = _form.Browser.Document.InvokeScript("eval", new object[] { js });
                return r == null ? "<null>" : r.ToString();
            }
            catch (Exception ex)
            {
                return "<HOST-EX>" + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static void Poll()
        {
            try
            {
                if (!File.Exists(CmdPath)) return;
                string content;
                try { content = File.ReadAllText(CmdPath, Encoding.UTF8); }
                catch { return; }
                try { File.Delete(CmdPath); } catch { }
                File.AppendAllText(OutPath, "=== batch " + DateTime.Now.ToString("HH:mm:ss") + " ===\n", Encoding.UTF8);

                foreach (var raw in content.Split('\n'))
                {
                    var expr = raw.Trim('\r', ' ', '\t');
                    if (expr.Length == 0 || expr.StartsWith("#")) continue;
                    var res = Eval(expr);
                    File.AppendAllText(OutPath, "EXPR: " + expr + "\n  => " + res + "\n", Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(Dir, "probe_err.txt"),
                    DateTime.Now.ToString("HH:mm:ss") + " " + ex.GetType().Name + ": " + ex.Message + "\n" +
                    ex.StackTrace + "\n", Encoding.UTF8); } catch { }
            }
        }
    }
}
