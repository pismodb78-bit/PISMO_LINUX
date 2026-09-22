using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PISMO.Views
{
    /// <summary>
    /// Пузырь сообщения — один на все три места, где идёт переписка: личная,
    /// группа и канал сервера.
    ///
    /// Раньше он жил внутри MainWindow, и у окна серверов не было выбора,
    /// кроме как завести свой такой же. Две копии одного пузыря — это две
    /// разных переписки на вид и два места, куда надо не забыть внести любую
    /// правку. Поэтому здесь только рисование, а что делать по нажатию —
    /// передаёт вызывающий: в личной переписке «удалить» значит одно, в
    /// канале сервера (где удалять чужое может модератор) — другое.
    /// </summary>
    public static class MessageView
    {
        /// <summary>Что умеет меню конкретного пузыря.</summary>
        public sealed class Actions
        {
            public Action<ChatMessage> Reply;
            public Action<ChatMessage> Pin;
            public Action<ChatMessage> Edit;
            public Action<ChatMessage> Delete;
            public Action<ChatMessage> SaveFile;
            public Action<ChatMessage> Copy;

            /// <summary>Проиграть голосовое или открыть кружок. Второй
            /// параметр: true — кружок (video_data), false — голосовое.</summary>
            public Action<ChatMessage, bool> PlayMedia;

            /// <summary>Показывать ли имя отправителя над сообщением.
            /// В личной переписке оно лишнее — там собеседник один.</summary>
            public bool ShowSender;

            /// <summary>Рисовать ли галочки прочтения. У групп и каналов
            /// прочтений нет вовсе, и галочка там означала бы неправду.</summary>
            public bool ShowReadMarks;

            public Func<ChatMessage, bool> CanEdit = m => m.IsMine;
            public Func<ChatMessage, bool> CanDelete = m => m.IsMine;
        }

        public static Control Build(ChatMessage m, Actions a)
        {
            var content = new StackPanel { Spacing = 6 };

            // Удалённое показываем как след, а не прячем: иначе ответы на него
            // начинают ссылаться в пустоту, и в переписке молча образуется
            // дыра. На ПК сделано так же.
            if (m.IsDeleted)
            {
                content.Children.Add(new TextBlock
                {
                    Text = "сообщение удалено",
                    FontStyle = FontStyle.Italic, FontSize = 13,
                    Foreground = new SolidColorBrush(Color.Parse("#72767d")),
                });
                return Wrap(m, content, muted: true, a: null);
            }

            if (a.ShowSender && !m.IsMine && !string.IsNullOrWhiteSpace(m.SenderName))
                content.Children.Add(new TextBlock
                {
                    Text = m.SenderName, FontSize = 12, FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#8ea1e1")),
                });

            if (m.ReplyToId > 0)
            {
                var quote = new StackPanel { Spacing = 1 };
                quote.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(m.ReplyToSender) ? "сообщение" : m.ReplyToSender,
                    FontSize = 11, FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(
                        m.IsMine ? Color.Parse("#d0d3ff") : Color.Parse("#a0a7b4")),
                });
                quote.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(m.ReplyToText) ? "вложение" : m.ReplyToText,
                    FontSize = 12, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(
                        m.IsMine ? Color.Parse("#c7cbff") : Color.Parse("#8e939c")),
                });
                content.Children.Add(new Border
                {
                    // Полоска слева — она отделяет цитату от самого сообщения
                    // без лишней рамки.
                    BorderBrush = new SolidColorBrush(
                        m.IsMine ? Color.Parse("#ffffff") : Color.Parse("#5865F2")),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(8, 2, 0, 2),
                    Child = quote,
                });
            }

            if (m.HasImage)
            {
                try
                {
                    using var ms = new MemoryStream(m.ImageData);
                    var bmp = new Bitmap(ms);
                    content.Children.Add(new Image
                    {
                        Source = bmp, MaxWidth = 320, MaxHeight = 320,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Left,
                    });
                }
                catch { /* нечитабельное изображение — пропускаем */ }
            }

            if (!string.IsNullOrEmpty(m.Text))
                content.Children.Add(new TextBlock
                {
                    Text = m.Text, TextWrapping = TextWrapping.Wrap,
                    Foreground = m.IsMine ? Brushes.White : new SolidColorBrush(Color.Parse("#dcddde")),
                    FontSize = 14,
                });

            // Файл — кнопкой: сами байты лежат в базе и тянутся только когда
            // на неё нажали, иначе каждая отрисовка переписки качала бы все
            // когда-либо присланные файлы.
            if (m.HasFile && a.SaveFile != null)
            {
                var save = new Button
                {
                    Content = $"📎 {(string.IsNullOrWhiteSpace(m.FileName) ? "файл" : m.FileName)}" +
                              $"  ·  {HumanSize(m.FileSize)}",
                    Background = new SolidColorBrush(
                        m.IsMine ? Color.Parse("#4752c4") : Color.Parse("#40444b")),
                    Foreground = Brushes.White,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Padding = new Thickness(10, 6),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                save.Click += (_, _) => a.SaveFile(m);
                content.Children.Add(save);
            }

            // Голосовое и кружок — кнопкой. Их байты, как и файлы, лежат в
            // базе и тянутся по нажатию: голосовое на минуту это два мегабайта,
            // и качать их при каждой отрисовке переписки незачем.
            if ((m.HasAudio || m.HasVideo) && a.PlayMedia != null)
            {
                bool circle = m.HasVideo;
                var play = new Button
                {
                    Content = circle ? "🎥 Кружок — открыть" : "▶ Голосовое сообщение",
                    Background = new SolidColorBrush(
                        m.IsMine ? Color.Parse("#4752c4") : Color.Parse("#40444b")),
                    Foreground = Brushes.White,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Padding = new Thickness(10, 6),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                play.Click += (_, _) => a.PlayMedia(m, circle);
                content.Children.Add(play);
            }

            var meta = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 5,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var metaBrush = new SolidColorBrush(
                m.IsMine ? Color.Parse("#d0d3ff") : Color.Parse("#72767d"));

            if (m.IsPinned)
                meta.Children.Add(new TextBlock { Text = "📌", FontSize = 10, Foreground = metaBrush });
            if (m.IsEdited)
                meta.Children.Add(new TextBlock
                {
                    Text = "изменено", FontSize = 10, FontStyle = FontStyle.Italic, Foreground = metaBrush,
                });
            meta.Children.Add(new TextBlock
            {
                Text = m.CreatedAt.ToString("HH:mm"), FontSize = 10, Foreground = metaBrush,
            });
            if (a.ShowReadMarks && m.IsMine)
                meta.Children.Add(new TextBlock
                {
                    Text = m.IsRead ? "✓✓" : "✓", FontSize = 10, Foreground = metaBrush,
                });
            content.Children.Add(meta);

            return Wrap(m, content, muted: false, a: a);
        }

        private static Control Wrap(ChatMessage m, Control content, bool muted, Actions a)
        {
            var bubble = new Border
            {
                Background = new SolidColorBrush(muted
                    ? Color.Parse("#292b2f")
                    : (m.IsMine ? Color.Parse("#5865F2") : Color.Parse("#2f3136"))),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 8),
                MaxWidth = 460,
                Child = content,
                HorizontalAlignment = m.IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            if (a != null) bubble.ContextMenu = Menu(m, a);
            return bubble;
        }

        private static ContextMenu Menu(ChatMessage m, Actions a)
        {
            var menu = new ContextMenu();
            var items = new List<Control>();

            if (a.Reply != null)
            {
                var reply = new MenuItem { Header = "Ответить" };
                reply.Click += (_, _) => a.Reply(m);
                items.Add(reply);
            }

            if (!string.IsNullOrEmpty(m.Text) && a.Copy != null)
            {
                var copy = new MenuItem { Header = "Копировать текст" };
                copy.Click += (_, _) => a.Copy(m);
                items.Add(copy);
            }

            if (a.Pin != null)
            {
                var pin = new MenuItem { Header = m.IsPinned ? "Открепить" : "Закрепить" };
                pin.Click += (_, _) => a.Pin(m);
                items.Add(pin);
            }

            // Пункты «изменить» и «удалить» показываем по праву, но спрятанный
            // пункт — это удобство, а не защита: то же условие стоит в самих
            // запросах.
            bool canEdit = a.Edit != null && (a.CanEdit?.Invoke(m) ?? m.IsMine);
            bool canDelete = a.Delete != null && (a.CanDelete?.Invoke(m) ?? m.IsMine);
            if (canEdit || canDelete) items.Add(new Separator());

            if (canEdit)
            {
                var edit = new MenuItem { Header = "Изменить" };
                edit.Click += (_, _) => a.Edit(m);
                items.Add(edit);
            }
            if (canDelete)
            {
                var del = new MenuItem { Header = "Удалить" };
                del.Click += (_, _) => a.Delete(m);
                items.Add(del);
            }

            menu.ItemsSource = items;
            return menu;
        }

        public static string HumanSize(long bytes)
        {
            if (bytes < 1024) return bytes + " Б";
            double kb = bytes / 1024.0;
            return kb > 1024
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0} МБ", kb / 1024.0)
                : (int)kb + " КБ";
        }

        /// <summary>Разделитель дат между сообщениями.</summary>
        public static Control DateSeparator(string date) => new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8),
            Child = new TextBlock
            {
                Text = date, FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#72767d")),
            },
        };
    }
}
