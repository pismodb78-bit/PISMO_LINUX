using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace PISMO.Views
{
    /// <summary>Запись видео-кружка: показывает, что снимает, и считает время.</summary>
    public static class CircleRecorderDialog
    {
        /// <summary>Возвращает готовый кружок или null, если отменили.</summary>
        public static async Task<byte[]> Record(Window owner)
        {
            var rec = new PISMO.Media.CircleRecorder();
            var tcs = new TaskCompletionSource<byte[]>();

            var preview = new Image
            {
                Width = 280, Height = 280, Stretch = Stretch.UniformToFill,
            };
            var frame = new Border
            {
                Width = 280, Height = 280, CornerRadius = new Avalonia.CornerRadius(140),
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.Parse("#202225")),
                Child = preview,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var timer = new TextBlock
            {
                Text = "00:00", FontSize = 20, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var hint = new TextBlock
            {
                Text = "Идёт запись", FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#72767d")),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            };

            var send = new Button { Content = "Отправить", Padding = new Avalonia.Thickness(20, 8) };
            send.Classes.Add("blurple");
            var cancel = new Button { Content = "Отмена", Padding = new Avalonia.Thickness(20, 8) };

            var dlg = new Window
            {
                Title = "Видео-кружок",
                Width = 360, Height = 480, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.Parse("#36393f")),
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(20), Spacing = 14,
                    Children =
                    {
                        frame, timer, hint,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal, Spacing = 8,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Children = { cancel, send },
                        },
                    },
                },
            };

            // Кадры приходят из процесса ffmpeg — в интерфейс только через
            // диспетчер.
            rec.Preview += jpeg => Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    using var ms = new MemoryStream(jpeg);
                    preview.Source = new Bitmap(ms);
                }
                catch { }
            });

            var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            tick.Tick += (_, _) =>
            {
                var t = rec.Elapsed;
                timer.Text = $"{t:mm\\:ss}";
                // Предел — не каприз: кружок целиком лежит в одной строке
                // таблицы, и минута записи это уже десятки мегабайт.
                if (t >= PISMO.Media.CircleRecorder.MaxDuration) Finish();
            };

            void Finish()
            {
                tick.Stop();
                var blob = rec.Stop();
                tcs.TrySetResult(blob);
                dlg.Close();
            }

            send.Click += (_, _) => Finish();
            cancel.Click += (_, _) => { tick.Stop(); rec.Cancel(); tcs.TrySetResult(null); dlg.Close(); };
            dlg.Closed += (_, _) => { tick.Stop(); rec.Cancel(); tcs.TrySetResult(null); };

            if (!rec.Start())
            {
                // Нет камеры или ffmpeg — говорим, чего именно не хватает.
                await Dialogs.Error(owner,
                    (rec.LastError ?? "Камера не открылась.") + "\n\n" +
                    "Кружкам нужен ffmpeg и доступ к /dev/video0:\n" +
                    "  Arch:   sudo pacman -S ffmpeg\n" +
                    "  Debian: sudo apt install ffmpeg",
                    "Видео-кружок");
                return null;
            }

            tick.Start();
            _ = dlg.ShowDialog(owner);
            return await tcs.Task;
        }
    }

    /// <summary>Проигрывание кружка: кадры по таймеру плюс звук через aplay.</summary>
    public static class CirclePlayerDialog
    {
        public static async Task Show(Window owner, byte[] blob)
        {
            var circle = PISMO.Media.VideoCircleCodec.Decode(blob);
            if (circle == null || circle.Frames.Count == 0)
            {
                await Dialogs.Error(owner, "Это не похоже на кружок — запись повреждена.");
                return;
            }

            var tcs = new TaskCompletionSource();
            var image = new Image { Width = 300, Height = 300, Stretch = Stretch.UniformToFill };
            var frame = new Border
            {
                Width = 300, Height = 300, CornerRadius = new Avalonia.CornerRadius(150),
                ClipToBounds = true, Child = image,
                Background = new SolidColorBrush(Color.Parse("#202225")),
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var dlg = new Window
            {
                Title = "Кружок",
                Width = 360, Height = 420, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.Parse("#36393f")),
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(20), Spacing = 12,
                    Children = { frame },
                },
            };

            int index = 0;
            var tick = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, circle.Fps)),
            };
            tick.Tick += (_, _) =>
            {
                if (index >= circle.Frames.Count)
                {
                    // Дошли до конца — закрываем сами: кружок смотрят один
                    // раз, и заставлять закрывать окно вручную незачем.
                    tick.Stop();
                    dlg.Close();
                    return;
                }
                try
                {
                    using var ms = new MemoryStream(circle.Frames[index]);
                    image.Source = new Bitmap(ms);
                }
                catch { }
                index++;
            };

            dlg.Closed += (_, _) =>
            {
                tick.Stop();
                PISMO.Media.VoiceNote.StopPlayback();
                tcs.TrySetResult();
            };

            _ = dlg.ShowDialog(owner);
            if (circle.Wav.Length > 0) PISMO.Media.VoiceNote.Play(circle.Wav);
            tick.Start();
            await tcs.Task;
        }
    }
}
