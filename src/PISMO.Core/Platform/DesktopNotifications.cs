using System;
using System.Diagnostics;

namespace PISMO.Platform
{
    /// <summary>
    /// Уведомления рабочего стола через <c>notify-send</c> (libnotify) —
    /// то, чем на Linux пользуются все: и GNOME, и KDE, и оконные менеджеры
    /// с демоном вроде dunst.
    ///
    /// Своего окна-всплывашки не рисуем намеренно. Оно не знало бы ни про
    /// «не беспокоить», ни про то, где у человека настроен угол показа, ни
    /// про историю уведомлений, — и лезло бы поверх полноэкранных программ.
    /// Системный демон всё это уже умеет.
    ///
    /// Нет notify-send — молчим. Уведомления приятны, но ничего не решают:
    /// сообщение всё равно видно в окне.
    /// </summary>
    public static class DesktopNotifications
    {
        private static bool? _available;

        public static bool Available
        {
            get
            {
                if (_available.HasValue) return _available.Value;
                try
                {
                    using var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = "which", Arguments = "notify-send",
                        RedirectStandardOutput = true, UseShellExecute = false,
                    });
                    p.WaitForExit(2000);
                    _available = p.ExitCode == 0;
                }
                catch { _available = false; }
                return _available.Value;
            }
        }

        /// <summary>Показывает уведомление. Тихо не делает ничего, если нечем.</summary>
        public static void Show(string title, string body)
        {
            if (!Available) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notify-send",
                    // -a задаёт имя программы: по нему человек сможет
                    // отключить наши уведомления настройками системы, не
                    // отключая все подряд.
                    ArgumentList =
                    {
                        "-a", "PISMO",
                        "-u", "normal",
                        title ?? "PISMO",
                        Trim(body),
                    },
                    UseShellExecute = false,
                })?.Dispose();
            }
            catch { }
        }

        /// <summary>
        /// Длинное сообщение режем: уведомление — это повод открыть окно, а
        /// не место читать переписку. Некоторые демоны длинный текст просто
        /// обрезают посередине слова.
        /// </summary>
        private static string Trim(string body)
        {
            body = (body ?? "").Replace("\n", " ").Trim();
            return body.Length <= 120 ? body : body.Substring(0, 120).TrimEnd() + "…";
        }
    }
}
