using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PISMO.Views
{
    /// <summary>
    /// Простые модальные диалоги (замена WinForms MessageBox, которого в Avalonia нет).
    /// </summary>
    public static class Dialogs
    {
        public static Task Info(Window owner, string message, string title = "PISMO")
            => Show(owner, message, title, false);

        public static Task Error(Window owner, string message, string title = "Ошибка")
            => Show(owner, message, title, true);

        /// <summary>
        /// Вопрос с двумя ответами. Возвращает true, если нажали
        /// подтверждающую кнопку, и false в любом другом случае —
        /// включая закрытие окна крестиком.
        /// </summary>
        public static Task<bool> Confirm(
            Window owner, string message, string title = "PISMO",
            string yes = "Да", string no = "Отмена")
        {
            var tcs = new TaskCompletionSource<bool>();

            var btnYes = new Button { Content = yes, Padding = new Avalonia.Thickness(20, 8) };
            btnYes.Classes.Add("blurple");
            var btnNo = new Button { Content = no, Padding = new Avalonia.Thickness(20, 8) };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Children = { btnNo, btnYes },
            };

            var dlg = new Window
            {
                Title = title,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.Parse("#36393f")),
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(20),
                    Spacing = 16,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = title,
                            FontSize = 16,
                            FontWeight = FontWeight.Bold,
                            Foreground = new SolidColorBrush(Colors.White),
                        },
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = new SolidColorBrush(Color.Parse("#dcddde")),
                        },
                        buttons,
                    },
                },
            };

            // Ответ фиксируем до Close(), иначе обработчик Closed успеет
            // выставить false раньше нажатой кнопки.
            btnYes.Click += (_, _) => { tcs.TrySetResult(true); dlg.Close(); };
            btnNo.Click += (_, _) => { tcs.TrySetResult(false); dlg.Close(); };
            dlg.Closed += (_, _) => tcs.TrySetResult(false);

            if (owner != null) _ = dlg.ShowDialog(owner);
            else Dispatcher.UIThread.Post(() => dlg.Show());
            return tcs.Task;
        }

        private static Task Show(Window owner, string message, string title, bool isError)
        {
            var ok = new Button
            {
                Content = "OK",
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Avalonia.Thickness(20, 8),
            };
            ok.Classes.Add("blurple");

            var dlg = new Window
            {
                Title = title,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.Parse("#36393f")),
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(20),
                    Spacing = 16,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = title,
                            FontSize = 16,
                            FontWeight = FontWeight.Bold,
                            Foreground = new SolidColorBrush(isError ? Color.Parse("#f04747") : Colors.White),
                        },
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = new SolidColorBrush(Color.Parse("#dcddde")),
                        },
                        ok,
                    },
                },
            };

            ok.Click += (_, _) => dlg.Close();

            if (owner != null)
                return dlg.ShowDialog(owner);

            // Без владельца (например, из фонового потока до логина) — показываем как окно.
            var tcs = new TaskCompletionSource();
            dlg.Closed += (_, _) => tcs.TrySetResult();
            Dispatcher.UIThread.Post(() => dlg.Show());
            return tcs.Task;
        }
    }
}
