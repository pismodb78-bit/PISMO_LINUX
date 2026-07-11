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
