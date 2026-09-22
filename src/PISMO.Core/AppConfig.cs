using System;
using System.Collections.Generic;
using System.IO;

namespace PISMO
{
    /// <summary>
    /// Внешняя конфигурация секретов, чтобы НЕ хранить их в репозитории.
    /// Порт PISMO/AppConfig.cs; отличается только тем, где ищет файл —
    /// на Linux это каталог рядом с программой и ~/.config/pismo.
    ///
    /// Значения берутся по порядку:
    ///   1) переменная окружения (PISMO_JWT_SECRET);
    ///   2) файл pismo.config (key=value);
    ///   3) вшитый дефолт — он же фолбэк ws-сервера, чтобы подпись проходила
    ///      проверку «из коробки». В проде переопределяется на обоих концах.
    /// </summary>
    public static class AppConfig
    {
        private static readonly object _lock = new();
        private static Dictionary<string, string> _file;

        private const string DefaultJwtSecret =
            "uc5KT2e+qYwa6tb0HUXnLZwsC55VuB93szkSpkucr8i1BFjKA6RXbyIrjk0+ign9";

        /// <summary>Секрет для подписи JWT (HS256). Должен совпадать с ws-сервером.</summary>
        public static string JwtSecret => Get("PISMO_JWT_SECRET", "jwt_secret", DefaultJwtSecret);

        public static string Get(string envVar, string fileKey, string fallback)
        {
            try
            {
                var env = Environment.GetEnvironmentVariable(envVar);
                if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

                var fromFile = FromFile(fileKey);
                if (!string.IsNullOrWhiteSpace(fromFile)) return fromFile;
            }
            catch { }
            return fallback;
        }

        private static string FromFile(string key)
        {
            lock (_lock)
            {
                _file ??= LoadFile();
                return _file.TryGetValue(key, out var v) ? v : null;
            }
        }

        private static Dictionary<string, string> LoadFile()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in ConfigPaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        var k = line.Substring(0, eq).Trim();
                        var v = line.Substring(eq + 1).Trim();
                        if (k.Length > 0 && !dict.ContainsKey(k)) dict[k] = v;
                    }
                }
                catch { }
            }
            return dict;
        }

        private static IEnumerable<string> ConfigPaths()
        {
            yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pismo.config");

            // XDG: ~/.config/pismo/pismo.config — туда кладут настройки на Linux.
            string cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(cfg))
                cfg = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            yield return Path.Combine(cfg, "pismo", "pismo.config");
        }
    }
}
