using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace PISMO.Views
{
    public partial class MainWindow : Window
    {
        private int _currentPartnerId = -1;
        private string _currentPartnerName = "";
        private byte[] _pendingImage;
        private string _pendingImageName;
        private int _lastTotalCount = -1;
        private readonly DispatcherTimer _pollTimer;

        // Палитра аватарок (детерминированно по id пользователя).
        private static readonly Color[] AvatarColors =
        {
            Color.Parse("#5865F2"), Color.Parse("#43b581"), Color.Parse("#faa61a"),
            Color.Parse("#f04747"), Color.Parse("#3498db"), Color.Parse("#9b59b6"),
            Color.Parse("#e91e63"), Color.Parse("#1abc9c"),
        };

        public MainWindow()
        {
            InitializeComponent();

            MeLabel.Text = $"{UserSession.EffectiveName} · {UserSession.Role}";

            BtnRefresh.Click += (_, _) => LoadConversations();
            BtnSettings.Click += (_, _) => new SettingsWindow().ShowDialog(this);
            BtnChangePw.Click += (_, _) => new ChangePasswordWindow().ShowDialog(this);
            BtnLogout.Click += BtnLogout_Click;
            BtnSend.Click += (_, _) => SendCurrent();
            BtnAttach.Click += async (_, _) => await PickAttachment();
            BtnAttachCancel.Click += (_, _) => ClearAttachment();
            BtnNewGroup.Click += async (_, _) =>
                await Dialogs.Info(this, "Групповые чаты переносятся на следующем этапе (см. docs/ROADMAP.md).");
            BtnAudioCall.Click += async (_, _) => await CallsComingSoon();
            BtnVideoCall.Click += async (_, _) => await CallsComingSoon();

            TxtMessage.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && e.KeyModifiers != KeyModifiers.Shift)
                {
                    e.Handled = true;
                    SendCurrent();
                }
            };

            LoadConversations();

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _pollTimer.Tick += (_, _) => PollTick();
            _pollTimer.Start();

            Closed += (_, _) => _pollTimer.Stop();
        }


        // ─────────────── Список диалогов ───────────────
        private void LoadConversations()
        {
            UserListPanel.Children.Clear();
            int myId = UserSession.EffectiveId;

            SidebarTitle.Text = UserSession.IsImpersonating
                ? $"💬 За: {UserSession.EffectiveName}"
                : (UserSession.Role == "admin" ? "Все пользователи" : "Личные сообщения");

            try
            {
                if (UserSession.Role == "admin" && !UserSession.IsImpersonating)
                {
                    foreach (var u in MessageService.GetAllUsers())
                    {
                        if (u.Id == myId) continue;
                        UserListPanel.Children.Add(BuildCard(u.Id, u.Name, u.Role, 0));
                    }
                }
                else
                {
                    foreach (var c in MessageService.GetConversations(myId))
                        UserListPanel.Children.Add(BuildCard(c.PartnerId, c.Name, c.LastMessage, c.Unread));
                }
            }
            catch (Exception ex)
            {
                _ = Dialogs.Error(this, "Ошибка загрузки диалогов: " + ex.Message);
            }
        }

        private Control BuildCard(int uid, string name, string subtitle, int unread)
        {
            var avatar = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(20),
                Background = new SolidColorBrush(AvatarColor(uid)),
                Child = new TextBlock
                {
                    Text = (string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpper()),
                    Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            texts.Children.Add(new TextBlock
            {
                Text = name, Foreground = new SolidColorBrush(Color.Parse("#dcddde")),
                FontWeight = FontWeight.SemiBold, FontSize = 14,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            texts.Children.Add(new TextBlock
            {
                Text = subtitle ?? "", Foreground = new SolidColorBrush(Color.Parse("#72767d")),
                FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
            });

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            };
            Grid.SetColumn(avatar, 0);
            Grid.SetColumn(texts, 1);
            texts.Margin = new Thickness(10, 0, 6, 0);
            grid.Children.Add(avatar);
            grid.Children.Add(texts);

            if (unread > 0)
            {
                var badge = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#f04747")),
                    CornerRadius = new CornerRadius(10), MinWidth = 20, Height = 20,
                    Padding = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = unread > 99 ? "99+" : unread.ToString(),
                        Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeight.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                Grid.SetColumn(badge, 2);
                grid.Children.Add(badge);
            }

            var card = new Border
            {
                Padding = new Thickness(8), CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
                Child = grid,
            };
            card.PointerEntered += (_, _) => card.Background = new SolidColorBrush(Color.Parse("#40444b"));
            card.PointerExited += (_, _) =>
                card.Background = uid == _currentPartnerId
                    ? new SolidColorBrush(Color.Parse("#40444b"))
                    : Brushes.Transparent;
            card.PointerPressed += (_, e) =>
            {
                if (UserSession.Role == "admin" && !UserSession.IsImpersonating &&
                    e.GetCurrentPoint(card).Properties.IsRightButtonPressed)
                {
                    DoImpersonate(uid, name);
                    return;
                }
                OpenChat(uid, name);
            };
            return card;
        }

        // ─────────────── Открытие чата ───────────────
        private void OpenChat(int partnerId, string partnerName)
        {
            _currentPartnerId = partnerId;
            _currentPartnerName = partnerName;
            ChatHeaderName.Text = partnerName;
            BtnAudioCall.IsVisible = true;
            BtnVideoCall.IsVisible = true;

            try { MessageService.MarkAsRead(UserSession.EffectiveId, partnerId); }
            catch { /* игнор */ }

            LoadMessages();
            LoadConversations();
        }

        private void DoImpersonate(int uid, string name)
        {
            UserSession.ImpersonatedId = uid;
            UserSession.ImpersonatedName = name;
            MeLabel.Text = $"{UserSession.EffectiveName} · (за пользователя)";
            _currentPartnerId = -1;
            ChatHeaderName.Text = "Выберите диалог";
            MessagesPanel.Children.Clear();
            LoadConversations();
        }

        // ─────────────── Загрузка сообщений ───────────────
        private void LoadMessages()
        {
            if (_currentPartnerId < 0) return;
            MessagesPanel.Children.Clear();

            try
            {
                var messages = MessageService.GetMessages(UserSession.EffectiveId, _currentPartnerId);
                string lastDate = "";
                foreach (var m in messages)
                {
                    string date = m.CreatedAt.ToString("d MMMM yyyy",
                        new System.Globalization.CultureInfo("ru-RU"));
                    if (date != lastDate)
                    {
                        MessagesPanel.Children.Add(BuildDateSeparator(date));
                        lastDate = date;
                    }
                    MessagesPanel.Children.Add(BuildBubble(m));
                }
                _lastTotalCount = SafeTotalCount();

                Dispatcher.UIThread.Post(() =>
                    MessagesScroll.Offset = new Vector(0, MessagesScroll.Extent.Height),
                    DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                _ = Dialogs.Error(this, "Ошибка загрузки сообщений: " + ex.Message);
            }
        }

        private Control BuildDateSeparator(string date)
        {
            return new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8),
                Child = new TextBlock
                {
                    Text = date, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#72767d")),
                },
            };
        }

        private Control BuildBubble(ChatMessage m)
        {
            var content = new StackPanel { Spacing = 6 };

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
            {
                content.Children.Add(new TextBlock
                {
                    Text = m.Text, TextWrapping = TextWrapping.Wrap,
                    Foreground = m.IsMine ? Brushes.White : new SolidColorBrush(Color.Parse("#dcddde")),
                    FontSize = 14,
                });
            }

            content.Children.Add(new TextBlock
            {
                Text = m.CreatedAt.ToString("HH:mm"), FontSize = 10,
                Foreground = m.IsMine
                    ? new SolidColorBrush(Color.Parse("#d0d3ff"))
                    : new SolidColorBrush(Color.Parse("#72767d")),
                HorizontalAlignment = HorizontalAlignment.Right,
            });

            var bubble = new Border
            {
                Background = new SolidColorBrush(m.IsMine ? Color.Parse("#5865F2") : Color.Parse("#2f3136")),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 8),
                MaxWidth = 460,
                Child = content,
                HorizontalAlignment = m.IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            return bubble;
        }

        // ─────────────── Отправка ───────────────
        private void SendCurrent()
        {
            if (_currentPartnerId < 0) return;
            string text = (TxtMessage.Text ?? "").Trim();
            if (string.IsNullOrEmpty(text) && _pendingImage == null) return;

            try
            {
                MessageService.SendMessage(UserSession.EffectiveId, _currentPartnerId, text, _pendingImage);
            }
            catch (Exception ex)
            {
                _ = Dialogs.Error(this, "Ошибка отправки: " + ex.Message);
                return;
            }

            TxtMessage.Text = "";
            ClearAttachment();
            LoadMessages();
            LoadConversations();
        }

        // ─────────────── Вложения ───────────────
        private async System.Threading.Tasks.Task PickAttachment()
        {
            var top = GetTopLevel(this);
            if (top?.StorageProvider == null) return;

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Выберите изображение",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Изображения")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp" },
                    },
                },
            });

            var file = files?.FirstOrDefault();
            if (file == null) return;

            try
            {
                await using var stream = await file.OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                _pendingImage = ms.ToArray();
                _pendingImageName = file.Name;
                AttachName.Text = "🖼 " + file.Name;
                AttachPreview.IsVisible = true;
            }
            catch (Exception ex)
            {
                await Dialogs.Error(this, "Не удалось прочитать файл: " + ex.Message);
            }
        }

        private void ClearAttachment()
        {
            _pendingImage = null;
            _pendingImageName = null;
            AttachPreview.IsVisible = false;
        }

        // ─────────────── Поллинг ───────────────
        private void PollTick()
        {
            try
            {
                int total = SafeTotalCount();
                if (total != _lastTotalCount)
                {
                    _lastTotalCount = total;
                    if (_currentPartnerId >= 0)
                    {
                        MessageService.MarkAsRead(UserSession.EffectiveId, _currentPartnerId);
                        LoadMessages();
                    }
                    LoadConversations();
                }
            }
            catch { /* поллинг не должен ронять UI */ }
        }

        private int SafeTotalCount()
        {
            try { return MessageService.GetTotalMessageCount(UserSession.EffectiveId); }
            catch { return _lastTotalCount; }
        }

        // ─────────────── Выход / звонки ───────────────
        private void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            _pollTimer.Stop();
            UserSession.Clear();
            Close(); // LoginWindow снова покажется (см. LoginWindow.BtnLogin_Click)
        }

        private async System.Threading.Tasks.Task CallsComingSoon()
        {
            if (PISMO.Platform.PlatformServices.CallsAvailable)
                return; // когда движок звонков подключён — здесь откроется окно звонка
            await Dialogs.Info(this,
                "Модуль звонков (аудио/видео/демонстрация экрана) переносится на Linux через " +
                "браузерный движок CEF — тот же WebRTC, что и в Windows-версии.\n\n" +
                "Подробности и статус — docs/ROADMAP.md.", "Звонки — на подходе");
        }

        private static Color AvatarColor(int uid)
            => AvatarColors[Math.Abs(uid) % AvatarColors.Length];
    }
}
