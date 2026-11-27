using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PortSecurityTool
{
    /// <summary>
    /// 轻量配置存储：%APPDATA%\PortSecurityTool\config.ini
    /// 键值格式 key=value，UTF-8 无 BOM。所有读写都在同一进程内加锁。
    /// </summary>
    internal static class AppConfig
    {
        private static readonly object _lock = new object();
        private static Dictionary<string, string> _map;
        private static string _dir;
        private static string _file;

        private static void Load()
        {
            if (_map != null) return;
            _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                _dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PortSecurityTool");
                _file = Path.Combine(_dir, "config.ini");
                if (!File.Exists(_file)) return;
                foreach (var raw in File.ReadAllLines(_file, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    _map[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch { }
        }

        private static void SaveLocked()
        {
            try
            {
                Directory.CreateDirectory(_dir);
                var sb = new StringBuilder();
                sb.AppendLine("# 网络信息采集和端口管理工具 配置文件");
                foreach (var kv in _map) sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(_file, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        public static string Get(string key, string def)
        {
            lock (_lock)
            {
                Load();
                string v;
                return _map.TryGetValue(key, out v) ? v : def;
            }
        }

        public static int GetInt(string key, int def)
        {
            int v;
            return int.TryParse(Get(key, def.ToString()), out v) ? v : def;
        }

        public static bool GetBool(string key, bool def)
        {
            var s = Get(key, def ? "1" : "0");
            return s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }

        public static void Set(string key, string val)
        {
            lock (_lock)
            {
                Load();
                _map[key] = val == null ? "" : val;
                SaveLocked();
            }
        }

        public static void SetBool(string key, bool val) { Set(key, val ? "1" : "0"); }
        public static void SetInt(string key, int val) { Set(key, val.ToString()); }

        public static string ConfigPath
        {
            get
            {
                lock (_lock) { Load(); return _file; }
            }
        }

        // 密码不落明文：XOR + Base64（内网工具的最低限度混淆，避免记事本直接看到）
        private static readonly byte[] _salt = { 0x5A, 0x3C, 0x91, 0xE7, 0x24, 0x6B, 0xD8, 0x17 };

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                var b = Encoding.UTF8.GetBytes(plain);
                for (int i = 0; i < b.Length; i++) b[i] ^= _salt[i % _salt.Length];
                return Convert.ToBase64String(b);
            }
            catch { return ""; }
        }

        public static string Unprotect(string cipher)
        {
            if (string.IsNullOrEmpty(cipher)) return "";
            try
            {
                var b = Convert.FromBase64String(cipher);
                for (int i = 0; i < b.Length; i++) b[i] ^= _salt[i % _salt.Length];
                return Encoding.UTF8.GetString(b);
            }
            catch { return ""; }
        }
    }
}
