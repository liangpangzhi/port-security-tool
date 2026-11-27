using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PortSecurityTool
{
    // ============================================================
    //  极简 JSON 构造器
    //  目的：不依赖序列化框架、字段顺序可控、输出紧凑（网络）与缩进（文档/文件）两种形态。
    // ============================================================

    internal abstract class JNode
    {
        public abstract void Write(StringBuilder sb, int ind, bool pretty);
        public string ToJson(bool pretty)
        {
            var sb = new StringBuilder(256);
            Write(sb, 0, pretty);
            return sb.ToString();
        }
        public override string ToString() { return ToJson(false); }
    }

    internal sealed class JVal : JNode
    {
        private readonly string _raw;           // 已是合法 JSON 片段
        private JVal(string raw) { _raw = raw; }

        public static JVal Null { get { return new JVal("null"); } }

        public static JVal Str(string s)
        {
            if (s == null) return Null;
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return new JVal(sb.ToString());
        }

        public static JVal Num(long v) { return new JVal(v.ToString(CultureInfo.InvariantCulture)); }
        public static JVal Num(double? v)
        {
            if (!v.HasValue) return Null;
            double d = v.Value;
            if (double.IsNaN(d) || double.IsInfinity(d)) return Null;
            return new JVal(d.ToString("0.###", CultureInfo.InvariantCulture));
        }
        public static JVal Bool(bool v) { return new JVal(v ? "true" : "false"); }
        public static JVal Raw(string json) { return new JVal(string.IsNullOrEmpty(json) ? "null" : json); }

        public override void Write(StringBuilder sb, int ind, bool pretty) { sb.Append(_raw); }
    }

    internal sealed class JArr : JNode
    {
        private readonly List<JNode> _items = new List<JNode>();

        public JArr Add(JNode n) { _items.Add(n ?? JVal.Null); return this; }
        public JArr Add(string s) { return Add(JVal.Str(s)); }
        public JArr Add(long v) { return Add(JVal.Num(v)); }
        public JArr Add(bool v) { return Add(JVal.Bool(v)); }
        public JArr AddObj(JObj o) { return Add(o); }
        public int Count { get { return _items.Count; } }

        public override void Write(StringBuilder sb, int ind, bool pretty)
        {
            if (_items.Count == 0) { sb.Append("[]"); return; }
            sb.Append('[');
            for (int i = 0; i < _items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                if (pretty)
                {
                    sb.Append('\n');
                    Indent(sb, ind + 1);
                }
                _items[i].Write(sb, ind + 1, pretty);
            }
            if (pretty) { sb.Append('\n'); Indent(sb, ind); }
            sb.Append(']');
        }

        internal static void Indent(StringBuilder sb, int level) { sb.Append(' ', level * 2); }
    }

    internal sealed class JObj : JNode
    {
        private readonly List<string> _keys = new List<string>();
        private readonly List<JNode> _vals = new List<JNode>();

        public int Count { get { return _keys.Count; } }
        public bool Has(string key) { return _keys.Contains(key); }
        public JNode Get(string key)
        {
            int i = _keys.IndexOf(key);
            return i < 0 ? null : _vals[i];
        }

        private JObj Add(string key, JNode n)
        {
            _keys.Add(key);
            _vals.Add(n ?? JVal.Null);
            return this;
        }

        public JObj Put(string k, string v) { return Add(k, JVal.Str(v)); }
        public JObj PutNum(string k, long v) { return Add(k, JVal.Num(v)); }
        public JObj PutNum(string k, double? v) { return Add(k, JVal.Num(v)); }
        public JObj PutBool(string k, bool v) { return Add(k, JVal.Bool(v)); }
        public JObj PutObj(string k, JObj v) { return Add(k, v); }
        public JObj PutArr(string k, JArr v) { return Add(k, v); }
        public JObj PutRaw(string k, string json) { return Add(k, JVal.Raw(json)); }

        public override void Write(StringBuilder sb, int ind, bool pretty)
        {
            if (_keys.Count == 0) { sb.Append("{}"); return; }
            sb.Append('{');
            for (int i = 0; i < _keys.Count; i++)
            {
                if (i > 0) sb.Append(',');
                if (pretty)
                {
                    sb.Append('\n');
                    JArr.Indent(sb, ind + 1);
                }
                sb.Append('"').Append(_keys[i]).Append("\":");
                if (pretty) sb.Append(' ');
                _vals[i].Write(sb, ind + 1, pretty);
            }
            if (pretty) { sb.Append('\n'); JArr.Indent(sb, ind); }
            sb.Append('}');
        }
    }
}
