using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace PISMO.Views
{
    /// <summary>
    /// Профиль: баннер, аватар, имя, «о себе», ссылки. Свой профиль можно
    /// править прямо здесь, чужой — только смотреть.
    /// </summary>
    public static class ProfileDialog
    {
        private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#dcddde"));
        private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#72767d"));

        private const int MaxImageBytes = 4 * 1024 * 1024;

        public static async Task Show(Window owner, int uid)
        {
            var p = await Task.Run(() => ProfileService.Load(uid));
            if (p == null)
            {
                await Dialogs.Error(owner, "Профиль не найден.");
                return;
            }

            bool mine = uid == UserSession.EffectiveId;
            var tcs = new TaskCompletionSource();

            // Картинки тянем отдельно и уже после текста: профиль должен
            // открыться сразу, а не после мегабайта по сети.
            var bannerBox = new Border
            {
                Height = 110,
                Background = new SolidColorBrush(Color.Parse("#5865F2")),
                CornerRadius = new Avalonia.CornerRadius(8, 8, 0, 0),
            };
            var avatarBox = new Border
            {
                Width = 76, Height = 76, CornerRadius = new Avalonia.CornerRadius(38),
                Background = new SolidColorBrush(Color.Parse("#2f3136")),
                BorderBrush = new SolidColorBrush(Color.Parse("#36393f")),
                BorderThickness = new Avalonia.Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Avalonia.Thickness(16, -38, 0, 0),
                Child = new TextBlock
                {
                    Text = string.IsNullOrEmpty(p.Name) ? "?" : p.Name.Substring(0, 1).ToUpper(),
                    Foreground = Brushes.White, FontSize = 30, FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var about = new TextBox
            {
                Text = p.About, AcceptsReturn = true, Height = 90,
                Watermark = "О себе", IsReadOnly = !mine,
                TextWrapping = TextWrapping.Wrap,
            };
            var social = new TextBox
            {
                Text = p.Social, Watermark = "Ссылки", IsReadOnly = !mine,
            };

            var body = new StackPanel { Spacing = 10, Margin = new Avalonia.Thickness(16, 10, 16, 16) };
            body.Children.Add(new TextBlock
            {
                Text = p.Name, FontSize = 19, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
            });
            body.Children.Add(new TextBlock { Text = "@" + p.Login + " · " + p.Role, Foreground = Muted, FontSize = 12 });
            body.Children.Add(new TextBlock { Text = "О СЕБЕ", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Muted });
            body.Children.Add(about);
            body.Children.Add(new TextBlock { Text = "ССЫЛКИ", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Muted });
            body.Children.Add(social);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var dlg = new Window
            {
                Title = p.Name,
                Width = 440, Height = 560, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.Parse("#36393f")),
            };

            if (mine)
            {
                var pickAvatar = new Button { Content = "Аватар" };
                pickAvatar.Click += async (_, _) =>
                {
                    var data = await PickImage(dlg);
                    if (data == null) return;
                    ProfileService.SetAvatar(uid, data);
                    SetImage(avatarBox, data);
                };
                var pickBanner = new Button { Content = "Баннер" };
                pickBanner.Click += async (_, _) =>
                {
                    var data = await PickImage(dlg);
                    if (data == null) return;
                    ProfileService.SetBanner(uid, data);
                    SetImage(bannerBox, data);
                };
                var save = new Button { Content = "Сохранить" };
                save.Classes.Add("blurple");
                save.Click += (_, _) =>
                {
                    ProfileService.SaveAbout(uid, about.Text ?? "", social.Text ?? "");
                    dlg.Close();
                };
                buttons.Children.Add(pickAvatar);
                buttons.Children.Add(pickBanner);
                buttons.Children.Add(save);
            }
            else
            {
                var close = new Button { Content = "Закрыть" };
                close.Classes.Add("blurple");
                close.Click += (_, _) => dlg.Close();
                buttons.Children.Add(close);
            }

            body.Children.Add(buttons);

            dlg.Content = new ScrollViewer
            {
                Content = new StackPanel { Children = { bannerBox, avatarBox, body } },
            };

            dlg.Closed += (_, _) => tcs.TrySetResult();
            if (owner != null) _ = dlg.ShowDialog(owner);
            else Dispatcher.UIThread.Post(() => dlg.Show());

            // Картинки догружаем, когда окно уже на экране.
            _ = Task.Run(() =>
            {
                var av = ProfileService.Avatar(uid);
                var bn = ProfileService.Banner(uid);
                Dispatcher.UIThread.Post(() =>
                {
                    if (av != null) SetImage(avatarBox, av);
                    if (bn != null) SetImage(bannerBox, bn);
                });
            });

            await tcs.Task;
        }

        /// <summary>
        /// Кладёт картинку в рамку. Скругление берётся у самой рамки, а
        /// ClipToBounds его применяет — иначе квадратная картинка торчала бы
        /// из круглой аватарки углами.
        /// </summary>
        private static void SetImage(Border target, byte[] data)
        {
            try
            {
                using var ms = new MemoryStream(data);
                var bmp = new Bitmap(ms);
                target.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
                target.ClipToBounds = true;
            }
            catch { /* нечитабельная картинка — оставляем заглушку */ }
        }

        private static async Task<byte[]> PickImage(Window owner)
        {
            var top = TopLevel.GetTopLevel(owner);
            if (top?.StorageProvider == null) return null;

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Выберите изображение",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Изображения")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp" },
                    },
                },
            });
            var file = files?.FirstOrDefault();
            if (file == null) return null;

            try
            {
                await using var stream = await file.OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                var data = ms.ToArray();

                // Аватар грузится на каждой строке списка, поэтому предел тут
                // строже, чем у вложений: большая картинка в профиле замедлит
                // не только сам профиль.
                if (data.Length > MaxImageBytes)
                {
                    await Dialogs.Error(owner,
                        $"Картинка {MessageView.HumanSize(data.Length)} — больше предела в " +
                        $"{MessageView.HumanSize(MaxImageBytes)}.\n\n" +
                        "Аватар показывается в списках на каждой строке, и тяжёлый " +
                        "замедлит не только профиль.");
                    return null;
                }
                return data;
            }
            catch (Exception ex)
            {
                await Dialogs.Error(owner, "Не удалось прочитать файл: " + ex.Message);
                return null;
            }
        }
    }
}
