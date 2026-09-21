using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace PISMO
{
    /// <summary>
    /// Автообновление из релизов GitHub — то же, что делает Windows-версия
    /// (PISMO/Updater.cs), но с поправкой на Linux.
    ///
    /// Поправок три, и каждая — не мелочь.
    ///
    /// ПЕРВАЯ: приложение может стоять там, куда писать нельзя. Пакет из
    /// PKGBUILD кладёт его в /opt, владелец root; обновлять такую установку
    /// должен менеджер пакетов, а не программа. Поэтому право на запись
    /// проверяется ДО всего остального, и если его нет — не пытаемся и
    /// объясняем, а не падаем на середине с половиной новых файлов.
    ///
    /// ВТОРАЯ: поверх работающего исполняемого файла Linux писать не даёт
    /// (ETXTBSY). Но удалить его можно: работающий процесс держит уже
    /// открытый inode и спокойно доживает до перезапуска. Поэтому не
    /// «перезаписать», а «удалить и создать заново».
    ///
    /// ТРЕТЬЯ: архив tar.gz, а не zip, — только он хранит бит запуска. Через
    /// zip новый PISMO приехал бы незапускаемым.
    /// </summary>
    public static class Updater
    {
        private const string Owner = "pismodb78-bit";
        private const string Repo = "PISMO_LINUX";
        private const string LatestUrl =
            "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";

        public sealed class Available
        {
            public string Tag = "";
            public string Notes = "";
            public string AssetUrl = "";
        }

        /// <summary>Версия запущенной сборки.</summary>
        public static Version Current =>
            Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);

        /// <summary>Каталог, из которого запущено приложение.</summary>
        public static string InstallDir =>
            Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)
            ?? AppContext.BaseDirectory;

        /// <summary>
        /// Можно ли обновляться самим. Нет — значит установку ведёт кто-то
        /// другой (менеджер пакетов), и трогать её нельзя.
        /// </summary>
        public static bool CanSelfUpdate()
        {
            try
            {
                string probe = Path.Combine(InstallDir, ".pismo_write_test");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Есть ли релиз новее запущенного. null — нет, или спросить не вышло:
        /// отсутствие сети не повод мешать человеку работать.
        /// </summary>
        public static async Task<Available> CheckAsync()
        {
            try
            {
                using var http = NewClient();
                string json = await http.GetStringAsync(LatestUrl);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(tag)) return null;
                if (!TryParseTag(tag, out var remote)) return null;
                if (remote <= Current) return null;

                string asset = null;
                if (root.TryGetProperty("assets", out var assets)
                    && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        string name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (name != null && name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                            && a.TryGetProperty("browser_download_url", out var u))
                        {
                            asset = u.GetString();
                            break;
                        }
                    }
                }
                if (string.IsNullOrWhiteSpace(asset)) return null;

                return new Available
                {
                    Tag = tag,
                    Notes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "",
                    AssetUrl = asset,
                };
            }
            catch { return null; }
        }

        /// <summary>
        /// Скачивает и ставит обновление. Возвращает путь к новому
        /// исполняемому файлу — вызывающий его запускает и выходит.
        /// Бросает исключение с внятным текстом, если не вышло.
        /// </summary>
        public static async Task<string> ApplyAsync(Available rel, Action<double> onProgress = null)
        {
            if (rel == null) throw new ArgumentNullException(nameof(rel));
            if (!CanSelfUpdate())
                throw new InvalidOperationException(
                    "Нет прав на запись в " + InstallDir + ".\n" +
                    "Похоже, программа установлена пакетом — обновляйте её менеджером пакетов.");

            string work = Path.Combine(Path.GetTempPath(), "pismo_update_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string archive = Path.Combine(work, "pismo.tar.gz");

            try
            {
                await DownloadAsync(rel.AssetUrl, archive, onProgress);

                // Распаковываем В СТОРОНЕ и только потом подменяем. Распаковка
                // прямо поверх установки оставила бы при обрыве половину новых
                // файлов и половину старых — состояние, из которого программа
                // уже не запустится.
                string unpacked = Path.Combine(work, "new");
                Directory.CreateDirectory(unpacked);
                ExtractTarGz(archive, unpacked);

                // Внутри архива один каталог (pismo) — берём его содержимое.
                string src = unpacked;
                var dirs = Directory.GetDirectories(unpacked);
                if (dirs.Length == 1 && Directory.GetFiles(unpacked).Length == 0) src = dirs[0];

                string exeName = Path.GetFileName(Environment.ProcessPath ?? "PISMO");
                ReplaceAll(src, InstallDir);

                string newExe = Path.Combine(InstallDir, exeName);
                if (!File.Exists(newExe))
                {
                    // Имя могло смениться между версиями — ищем что-то запускаемое.
                    foreach (var f in Directory.GetFiles(InstallDir))
                        if (Path.GetFileName(f).StartsWith("PISMO", StringComparison.OrdinalIgnoreCase)
                            && !Path.HasExtension(f)) { newExe = f; break; }
                }
                MakeExecutable(newExe);
                return newExe;
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }
        }

        /// <summary>Перезапуск: запускаем новую сборку и уходим.</summary>
        public static void RestartInto(string exePath)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = InstallDir,
                    UseShellExecute = false,
                });
            }
            catch { }
            Environment.Exit(0);
        }

        // ── Внутреннее ───────────────────────────────────────────────────

        private static HttpClient NewClient()
        {
            var http = new HttpClient();
            // GitHub отказывает запросам без User-Agent.
            http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("PISMO", Current.ToString()));
            http.Timeout = TimeSpan.FromMinutes(10);
            return http;
        }

        private static async Task DownloadAsync(string url, string toFile, Action<double> onProgress)
        {
            using var http = NewClient();
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            long total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(toFile);

            var buf = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read));
                done += read;
                if (total > 0) onProgress?.Invoke((double)done / total);
            }
        }

        private static void ExtractTarGz(string archive, string toDir)
        {
            using var file = File.OpenRead(archive);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, toDir, overwriteFiles: true);
        }

        /// <summary>
        /// Переносит распакованное поверх установки. Файл удаляется перед
        /// записью — см. пояснение про ETXTBSY в описании класса.
        /// </summary>
        private static void ReplaceAll(string from, string to)
        {
            foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));

            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(from, file);
                string dest = Path.Combine(to, rel);

                // Настройки, которые человек правил руками, обновление не
                // откатывает к тому, что лежало в архиве. Но если файла нет
                // (удалили, первая установка) — берём его из архива, иначе
                // клиент останется вовсе без адреса базы.
                string name = Path.GetFileName(rel);
                bool isSettings =
                    name.Equals("ip.txt", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("turnsettings.json", StringComparison.OrdinalIgnoreCase);
                if (isSettings && File.Exists(dest)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                File.Copy(file, dest, overwrite: true);
            }
        }

        private static void MakeExecutable(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch { }
        }

        /// <summary>Тег релиза (0.0.1.1 или v0.0.1.1) в Version.</summary>
        private static bool TryParseTag(string tag, out Version version)
        {
            version = null;
            string clean = (tag ?? "").Trim();
            if (clean.StartsWith("v", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(1);
            // Version требует минимум major.minor.
            if (!clean.Contains('.')) clean += ".0";
            return Version.TryParse(clean, out version);
        }
    }
}
