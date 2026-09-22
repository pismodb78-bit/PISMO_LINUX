using System.Collections.Generic;
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

        /// <summary>
        /// Ввод одной строки. Возвращает текст или null, если отказались —
        /// пустая строка от отказа отличается: «стереть всё» это не то же
        /// самое, что «передумал».
        /// </summary>
        public static Task<string> Prompt(
            Window owner, string title, string initial = "",
            string yes = "Сохранить", string no = "Отмена")
        {
            var tcs = new TaskCompletionSource<string>();

            var box = new TextBox
            {
                Text = initial ?? "",
                AcceptsReturn = false,
                Watermark = "Текст",
            };

            var btnYes = new Button { Content = yes, Padding = new Avalonia.Thickness(20, 8) };
            btnYes.Classes.Add("blurple");
            var btnNo = new Button { Content = no, Padding = new Avalonia.Thickness(20, 8) };

            var dlg = new Window
            {
                Title = title,
                Width = 440,
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
                            Text = title,
                            FontSize = 16,
                            FontWeight = FontWeight.Bold,
                            Foreground = new SolidColorBrush(Colors.White),
                        },
                        box,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { btnNo, btnYes },
                        },
                    },
                },
            };

            void Accept() { tcs.TrySetResult(box.Text ?? ""); dlg.Close(); }

            btnYes.Click += (_, _) => Accept();
            btnNo.Click += (_, _) => { tcs.TrySetResult(null); dlg.Close(); };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; Accept(); }
                else if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; dlg.Close(); }
            };
            // Крестик и Escape — тот же отказ, что и кнопка.
            dlg.Closed += (_, _) => tcs.TrySetResult(null);
            dlg.Opened += (_, _) => { box.SelectAll(); box.Focus(); };

            if (owner != null) _ = dlg.ShowDialog(owner);
            else Dispatcher.UIThread.Post(() => dlg.Show());
            return tcs.Task;
        }

        /// <summary>
        /// Выбор одного из нескольких действий. Возвращает выбранную строку
        /// или null, если закрыли.
        ///
        /// Список, а не череда вопросов «да/нет»: три подряд подтверждения
        /// ради одного действия — верный способ нажать не то.
        /// </summary>
        public static Task<string> Choose(Window owner, string title, IEnumerable<string> options)
        {
            var tcs = new TaskCompletionSource<string>();
            var panel = new StackPanel { Spacing = 6 };

            // Окно объявляем заранее: кнопки внутри должны его закрывать, а
            // создаётся оно ниже — без этой переменной пришлось бы искать
            // корень по дереву и тянуть ради одной строки лишний using.
            Window dlg = null;

            foreach (var option in options)
            {
                var b = new Button
                {
                    Content = option,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Avalonia.Thickness(12, 8),
                };
                string captured = option;
                b.Click += (_, _) => { tcs.TrySetResult(captured); dlg?.Close(); };
                panel.Children.Add(b);
            }

            dlg = new Window
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
                    Spacing = 14,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = title,
                            FontSize = 16,
                            FontWeight = FontWeight.Bold,
                            Foreground = new SolidColorBrush(Colors.White),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                        panel,
                    },
                },
            };

            dlg.Closed += (_, _) => tcs.TrySetResult(null);
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
