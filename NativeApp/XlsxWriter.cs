using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PortSecurityTool
{
    /// <summary>
    /// 极简 xlsx 生成器：直接按 OOXML 规范打包 zip，不依赖第三方组件。
    /// 版式对齐「采集端」模板：表头蓝底(#4472C4)白字加粗居中、无边框、行高 20px、
    /// 数据行默认格式，仅按列声明的数字列写为数值单元格（右对齐）。
    /// </summary>
    internal static class XlsxWriter
    {
        public sealed class Sheet
        {
            public string Name = "Sheet1";
            public List<List<string>> Rows = new List<List<string>>();
            public int[] ColWidths;                 // 每列宽度（字符数）
            public HashSet<int> NumericCols = new HashSet<int>();  // 需要写成数值的列（0 基）
            public bool HeaderStyle = true;         // 首行套用模板表头样式
            public double RowHeight = 0;            // 0 = 不写行高（沿用 Excel 默认，与模板一致）

            public Sheet() { }
            public Sheet(string name) { Name = name; }

            public void Add(params object[] cells)
            {
                var row = new List<string>(cells.Length);
                foreach (var c in cells) row.Add(c == null ? "" : c.ToString());
                Rows.Add(row);
            }
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default:
                        if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') sb.Append(' ');
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string ColName(int index)
        {
            var sb = new StringBuilder();
            int n = index;
            while (true)
            {
                int r = n % 26;
                sb.Insert(0, (char)('A' + r));
                n = n / 26 - 1;
                if (n < 0) break;
            }
            return sb.ToString();
        }

        private static string SanitizeSheetName(string name, int idx)
        {
            if (string.IsNullOrEmpty(name)) return "Sheet" + (idx + 1);
            foreach (var ch in new[] { ':', '\\', '/', '?', '*', '[', ']' })
                name = name.Replace(ch, '_');
            if (name.Length > 31) name = name.Substring(0, 31);
            return name;
        }

        private static string SheetXml(Sheet sheet)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

            if (sheet.ColWidths != null && sheet.ColWidths.Length > 0)
            {
                sb.Append("<cols>");
                for (int i = 0; i < sheet.ColWidths.Length; i++)
                    sb.AppendFormat("<col min=\"{0}\" max=\"{0}\" width=\"{1}\" customWidth=\"1\"/>", i + 1, sheet.ColWidths[i]);
                sb.Append("</cols>");
            }

            sb.Append("<sheetData>");
            for (int r = 0; r < sheet.Rows.Count; r++)
            {
                var row = sheet.Rows[r];
                bool isHeader = sheet.HeaderStyle && r == 0;
                if (sheet.RowHeight > 0)
                    sb.AppendFormat("<row r=\"{0}\" ht=\"{1}\" customHeight=\"1\">", r + 1, sheet.RowHeight);
                else
                    sb.AppendFormat("<row r=\"{0}\">", r + 1);

                for (int c = 0; c < row.Count; c++)
                {
                    var val = row[c] ?? "";
                    string cellRef = ColName(c) + (r + 1);

                    if (isHeader)
                    {
                        sb.AppendFormat("<c r=\"{0}\" t=\"inlineStr\" s=\"1\"><is><t xml:space=\"preserve\">{1}</t></is></c>",
                            cellRef, Esc(val));
                        continue;
                    }

                    // 数字列：写成数值单元格，Excel 自动右对齐（与模板中的数量/容量列一致）
                    if (sheet.NumericCols.Contains(c))
                    {
                        double num;
                        if (val.Length > 0 && double.TryParse(val,
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out num))
                        {
                            sb.AppendFormat("<c r=\"{0}\"><v>{1}</v></c>", cellRef,
                                num.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            continue;
                        }
                    }

                    if (val.Length == 0)
                        sb.AppendFormat("<c r=\"{0}\"/>", cellRef);
                    else
                        sb.AppendFormat("<c r=\"{0}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{1}</t></is></c>",
                            cellRef, Esc(val));
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        // 样式表：0=常规  1=模板表头（#4472C4 填充 + 10pt 加粗白字 + 居中）
        private const string StylesXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"3\">" +
              "<font><color theme=\"1\"/></font>" +
              "<font><b/><color theme=\"1\"/></font>" +
              "<font><b/><color rgb=\"FFFFFFFF\"/></font>" +
            "</fonts>" +
            "<fills count=\"3\">" +
              "<fill><patternFill patternType=\"none\"/></fill>" +
              "<fill><patternFill patternType=\"gray125\"/></fill>" +
              "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF4472C4\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
            "</fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"2\">" +
              "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
              "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\">" +
                "<alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
            "</cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"常规\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
            "</styleSheet>";

        public static void Write(string path, IList<Sheet> sheets)
        {
            if (sheets == null || sheets.Count == 0) throw new ArgumentException("至少需要一个工作表");

            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (File.Exists(path)) File.Delete(path);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                Action<string, string> put = (name, content) =>
                {
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using (var st = entry.Open())
                    {
                        var bytes = new UTF8Encoding(false).GetBytes(content);
                        st.Write(bytes, 0, bytes.Length);
                    }
                };

                var ct = new StringBuilder();
                ct.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                ct.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
                ct.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
                ct.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
                ct.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
                ct.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
                for (int i = 0; i < sheets.Count; i++)
                    ct.AppendFormat("<Override PartName=\"/xl/worksheets/sheet{0}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>", i + 1);
                ct.Append("</Types>");
                put("[Content_Types].xml", ct.ToString());

                put("_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                    "</Relationships>");

                var wb = new StringBuilder();
                wb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                wb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
                wb.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
                for (int i = 0; i < sheets.Count; i++)
                    wb.AppendFormat("<sheet name=\"{0}\" sheetId=\"{1}\" r:id=\"rId{1}\"/>",
                        Esc(SanitizeSheetName(sheets[i].Name, i)), i + 1);
                wb.Append("</sheets></workbook>");
                put("xl/workbook.xml", wb.ToString());

                var rels = new StringBuilder();
                rels.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                rels.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
                for (int i = 0; i < sheets.Count; i++)
                    rels.AppendFormat("<Relationship Id=\"rId{0}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{0}.xml\"/>", i + 1);
                rels.AppendFormat("<Relationship Id=\"rId{0}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>", sheets.Count + 1);
                rels.Append("</Relationships>");
                put("xl/_rels/workbook.xml.rels", rels.ToString());

                put("xl/styles.xml", StylesXml);

                for (int i = 0; i < sheets.Count; i++)
                    put("xl/worksheets/sheet" + (i + 1) + ".xml", SheetXml(sheets[i]));
            }
        }
    }
}
