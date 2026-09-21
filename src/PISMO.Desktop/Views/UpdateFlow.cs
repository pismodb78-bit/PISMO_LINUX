using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PISMO.Views
{
    /// <summary>
    /// Автообновление со стороны интерфейса: спросить, скачать, перезапустить.
    /// Сама проверка и установка — в PISMO.Core/Updater.cs.
    ///
    /// Правило здесь одно: молчать, когда сказать нечего. Проверка идёт в
    /// фоне при старте, и если обновления нет, интернета нет или GitHub
    /// недоступен — человек не увидит ничего. Окно появляется только когда
    /// есть что предложить.
    /// </summary>
    public static class UpdateFlow
    {
        private static bool _checked;

        /// <summary>
        /// Тихая проверка при запуске. Вызывать из Opened окна входа:
        /// обновляться до входа безопаснее всего — ничего не открыто, ничего
        /// не потеряется.
        /// </summary>
        public static void CheckInBackground(Window owner)
        {
            if (_checked) return;   // одного раза за запуск достаточно
            _checked = true;

            _ = Task.Run(async () =>
            {
                var rel = await Updater.CheckAsync();
                if (rel == null) return;

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    // Установка из пакета обновляется пакетным менеджером —
                    // предлагать самообновление бессмысленно, только собьём с толку.
                    if (!Updater.CanSelfUpdate()) return;

                    string notes = (rel.Notes ?? "").Trim();
                    if (notes.Length > 300) notes = notes.Substring(0, 300).TrimEnd() + "…";

                    string text = $"Доступна версия {rel.Tag} (сейчас {Updater.Current}).";
                    if (notes.Length > 0) text += "\n\n" + notes;
                    text += "\n\nСкачать и перезапустить PISMO?";

                    bool yes = await Dialogs.Confirm(owner, text, "Обновление", "Обновить", "Позже");
                    if (yes) await RunUpdate(owner, rel);
                });
            });
        }

        private static async Task RunUpdate(Window owner, Updater.Available rel)
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 6 };
            var label = new TextBlock
            {
                Text = "Загрузка…",
                Foreground = new SolidColorBrush(Color.Parse("#dcddde")),
            };

            var wnd = new Window
            {
                Title = "Обновление",
                Width = 360,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.Parse("#36393f")),
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(20),
                    Spacing = 14,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "PISMO " + rel.Tag,
                            FontSize = 16,
                            FontWeight = FontWeight.Bold,
                            Foreground = new SolidColorBrush(Colors.White),
                        },
                        label,
                        bar,
                    },
                },
            };
            wnd.Show(owner);

            try
            {
                // Прогресс приходит из потока загрузки — в интерфейс только через диспетчер.
                string exe = await Updater.ApplyAsync(rel, p =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        bar.Value = Math.Round(p * 100);
                        label.Text = $"Загрузка… {bar.Value:0}%";
                    }));

                label.Text = "Перезапуск…";
                Updater.RestartInto(exe);   // сюда уже не вернёмся
            }
            catch (Exception ex)
            {
                wnd.Close();
                await Dialogs.Error(owner,
                    "Не удалось обновиться.\n\n" + ex.Message +
                    "\n\nМожно скачать сборку вручную:\n" +
                    "https://github.com/pismodb78-bit/PISMO_LINUX/releases",
                    "Обновление");
            }
        }
    }
}
