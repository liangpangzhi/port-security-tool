using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace PortSecurityTool
{
    /// <summary>接口文档（ApiDoc.md 以嵌入资源随程序分发，可按需导出给服务端开发）。</summary>
    internal static class ApiDoc
    {
        private static string _text;

        public static string Text
        {
            get
            {
                if (_text != null) return _text;
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var st = asm.GetManifestResourceStream("PortSecurityTool.ApiDoc.md"))
                    {
                        if (st != null)
                            using (var rd = new StreamReader(st, Encoding.UTF8))
                                _text = rd.ReadToEnd();
                    }
                }
                catch { }
                if (_text == null || _text.Length == 0)
                    _text = "# 数据接口说明\n\n契约：" + ApiReport.SchemaName + " / v" + ApiReport.SchemaVersion
                          + "\n\n文档资源缺失，请参考软件目录中的 API 文档文件。\n";
                return _text;
            }
        }
    }
}
