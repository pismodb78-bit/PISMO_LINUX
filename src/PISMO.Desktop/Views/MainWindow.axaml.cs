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

        // Звонки
        private int _lastCheckedCallId = 0;
        private bool _incomingOpen;
        private CallWindow _activeCall;

        // Присутствие: uid -> 0 не в сети, 1 бездействует, 2 в сети.
        private readonly Dictionary<int, int> _presence = new();
        // Когда статус этого человека пришёл по сокету. По этой отметке снимок
        // из базы не затирает то, что пришло секунду назад: запрос уходит
        // раньше, чем приходит ответ, и за это время человек успевает отойти.
        private readonly Dictionary<int, DateTime> _presencePushedAt = new();
        private readonly DispatcherTimer _presenceTimer;
        // Момент, когда окно в последний раз было активным — наш аналог
        // системного простоя ввода, см. PresenceService.
        private DateTime _lastActiveAt = DateTime.UtcNow;
        private bool _windowActive = true;
        private Action<string, int, int, string> _wsHandler;

        // Карточки и кружки статуса на них — чтобы перекрашивать точку, не
        // пересобирая список: пересборка сбрасывает прокрутку.
        private readonly Dictionary<int, Control> _cardByUser = new();
        private readonly Dictionary<int, Border> _dotByUser = new();

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
            BtnAudioCall.Click += (_, _) => StartOutgoingCall(false);
            BtnVideoCall.Click += (_, _) => StartOutgoingCall(true);

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

            // Присутствие: тот же период, что у ПК и телефона.
            _presenceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(PresenceService.TickMs),
            };
            _presenceTimer.Tick += (_, _) => PresenceTick();
            _presenceTimer.Start();
            PresenceTick();   // сразу, а не через шесть секунд

            // Активность окна — наш признак «человек за компьютером».
            // Отслеживаем событиями, а не опросом свойства: события есть у
            // всех платформ Avalonia и работают одинаково.
            Activated += (_, _) => { _windowActive = true; _lastActiveAt = DateTime.UtcNow; };
            Deactivated += (_, _) => { _windowActive = false; _lastActiveAt = DateTime.UtcNow; };

            ConnectSignaling();

            Closed += (_, _) =>
            {
                _pollTimer.Stop();
                _presenceTimer.Stop();
                DisconnectSignaling();
                System.Threading.Tasks.Task.Run(PresenceService.MarkOffline);
            };
        }


        // ─────────────── Присутствие ───────────────

        /// <summary>
        /// Простой в секундах. Активностью считаем «окно программы активно» —
        /// тот же признак, что на телефоне; почему не системный простой ввода,
        /// объяснено в PresenceService.
        /// </summary>
        private int IdleSeconds()
        {
            if (_windowActive) { _lastActiveAt = DateTime.UtcNow; return 0; }
            return (int)(DateTime.UtcNow - _lastActiveAt).TotalSeconds;
        }

        private void PresenceTick()
        {
            int idle = IdleSeconds();
            PresenceService.Announce(idle);

            // Кого показываем — тех и спрашиваем.
            var ids = _cardByUser.Keys.ToList();
            if (_currentPartnerId > 0 && !ids.Contains(_currentPartnerId)) ids.Add(_currentPartnerId);

            System.Threading.Tasks.Task.Run(() =>
            {
                PresenceService.Heartbeat(idle);
                var fresh = PresenceService.Read(ids);
                PresenceService.Ago peer = _currentPartnerId > 0
                    ? PresenceService.ReadOne(_currentPartnerId) : null;
                int peerId = _currentPartnerId;

                Dispatcher.UIThread.Post(() =>
                {
                    ApplyPresence(fresh);
                    // Не удалось прочитать — ОСТАВЛЯЕМ то, что написано.
                    // Прятать подпись на каждом неудачном запросе значит гасить
                    // статус от одной моргнувшей связи.
                    if (peer != null && peerId == _currentPartnerId)
                        ShowPeerStatus(peer.Status, peer.SeenAgo, peer.ActiveAgo);
                });
            });
        }

        /// <summary>Снимок из базы поверх того, что знаем. Поключево: приход по
        /// сокету добавляет и тех, кого нет в списке, и сравнение длин здесь
        /// находило бы «изменение» на каждом тике.</summary>
        private void ApplyPresence(Dictionary<int, int> fresh)
        {
            if (fresh == null) return;

            var now = DateTime.UtcNow;
            foreach (var kv in fresh.Keys.ToList())
            {
                // Свежий приход по сокету старше ответа базы — про себя клиент
                // знает точнее любой строки в ней.
                if (_presencePushedAt.TryGetValue(kv, out var at)
                    && (now - at).TotalSeconds < 10
                    && _presence.TryGetValue(kv, out int pushed))
                    fresh[kv] = pushed;
            }

            foreach (var kv in fresh)
                if (!_presence.TryGetValue(kv.Key, out int v) || v != kv.Value)
                {
                    _presence[kv.Key] = kv.Value;
                    PaintDot(kv.Key, kv.Value);
                }
        }

        /// <summary>Пришёл чужой статус по сокету — применяем немедленно.</summary>
        private void ApplyPresencePush(int senderId, int status, string payload)
        {
            if (senderId <= 0 || status < 0 || status > 2) return;

            bool differs = !_presence.TryGetValue(senderId, out int prev) || prev != status;
            _presence[senderId] = status;
            _presencePushedAt[senderId] = DateTime.UtcNow;
            if (differs) PaintDot(senderId, status);

            if (senderId == _currentPartnerId)
            {
                int.TryParse(payload, out int idle);
                ShowPeerStatus(status, 0, Math.Max(0, idle));
            }
        }

        private static readonly Color DotOnline = Color.Parse("#3ba55d");
        private static readonly Color DotIdle = Color.Parse("#faa81a");
        private static readonly Color DotOffline = Color.Parse("#747f8d");

        private static Color DotColor(int status) =>
            status == 2 ? DotOnline : status == 1 ? DotIdle : DotOffline;

        /// <summary>Кружок статуса на аватарке карточки, если она на экране.</summary>
        private void PaintDot(int uid, int status)
        {
            if (!_dotByUser.TryGetValue(uid, out var dot)) return;
            dot.Background = new SolidColorBrush(DotColor(status));
            dot.IsVisible = true;
        }

        private void ShowPeerStatus(int status, int seenAgo, int activeAgo)
        {
            ChatHeaderStatus.Text = PresenceService.Text(status, seenAgo, activeAgo);
            ChatHeaderStatus.Foreground = new SolidColorBrush(DotColor(status));
            ChatHeaderStatus.IsVisible = true;
        }

        // ─────────────── Сигналинг ───────────────

        private void ConnectSignaling()
        {
            _wsHandler = (type, senderId, sessionId, payload) =>
                Dispatcher.UIThread.Post(() => OnSignal(type, senderId, sessionId, payload));
            SignalingClient.Instance.OnMessage += _wsHandler;
            _ = SignalingClient.Instance.ConnectAsync(UserSession.EffectiveId);
        }

        private void DisconnectSignaling()
        {
            try
            {
                if (_wsHandler != null) SignalingClient.Instance.OnMessage -= _wsHandler;
                SignalingClient.Instance.Disconnect();
            }
            catch { }
        }

        /// <summary>
        /// Событие из сокета. Всё, что здесь делается, умеет делать и опрос —
        /// разница только в том, что опрос делает это через несколько секунд.
        /// Поэтому ни одна ветка не обязана сработать: сервера может не быть.
        /// </summary>
        private void OnSignal(string type, int senderId, int sessionId, string payload)
        {
            try
            {
                switch (type)
                {
                    case "new_message":
                        // Открытый чат перечитываем сразу; список — ради
                        // непрочитанных и порядка.
                        if (_currentPartnerId > 0) LoadMessages();
                        LoadConversations();
                        break;

                    case "read":
                        // Собеседник прочитал мои сообщения.
                        if (senderId == _currentPartnerId) LoadMessages();
                        break;

                    case "presence":
                        ApplyPresencePush(senderId, sessionId, payload);
                        break;

                    case "incoming_call":
                        CheckIncomingCalls();
                        break;
                }
            }
            catch { }
        }

        // ─────────────── Список диалогов ───────────────
        private void LoadConversations()
        {
            // Запоминаем, где стоял ползунок. Список пересобирается на каждое
            // новое сообщение, и без этого он на каждом сообщении прыгал бы
            // наверх — прямо посреди чтения.
            double keepScroll = UserListScroll?.Offset.Y ?? 0;

            UserListPanel.Children.Clear();
            _cardByUser.Clear();
            _dotByUser.Clear();
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

            // Вернуть ползунок можно только после того, как список разложат по
            // местам: до этого его высота ещё нулевая и прокручивать нечего.
            if (keepScroll > 0 && UserListScroll != null)
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        double max = Math.Max(0, UserListScroll.Extent.Height - UserListScroll.Viewport.Height);
                        UserListScroll.Offset = new Vector(UserListScroll.Offset.X, Math.Min(keepScroll, max));
                    }
                    catch { }
                }, DispatcherPriority.Loaded);
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

            // Кружок статуса в правом нижнем углу аватарки — как на ПК.
            // Обводка цветом фона карточки, иначе точка сливается с аватаркой.
            var dot = new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(6),
                BorderBrush = new SolidColorBrush(Color.Parse("#2f3136")),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                // Пока статус неизвестен, точки нет вовсе: серая означала бы
                // «не в сети», а мы этого ещё не знаем.
                IsVisible = false,
            };
            if (_presence.TryGetValue(uid, out int known))
            {
                dot.Background = new SolidColorBrush(DotColor(known));
                dot.IsVisible = true;
            }
            _dotByUser[uid] = dot;

            var avatarBox = new Panel { Width = 40, Height = 40 };
            avatarBox.Children.Add(avatar);
            avatarBox.Children.Add(dot);

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
            Grid.SetColumn(avatarBox, 0);
            Grid.SetColumn(texts, 1);
            texts.Margin = new Thickness(10, 0, 6, 0);
            grid.Children.Add(avatarBox);
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
            _cardByUser[uid] = card;
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

            // Подпись от ПРЕЖНЕГО собеседника убираем сразу: показывать его
            // статус под чужим именем хуже, чем не показывать ничего. Что
            // знаем — покажем, остальное допишет ближайший тик.
            ChatHeaderStatus.IsVisible = false;
            if (_presence.TryGetValue(partnerId, out int known))
                ShowPeerStatus(known, 0, 0);

            try
            {
                MessageService.MarkAsRead(UserSession.EffectiveId, partnerId);
                // Собеседнику — чтобы галочки прочтения у него появились
                // сразу, а не со следующей его сверкой.
                SignalingClient.Instance.Send("read", partnerId, 0, "");
            }
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

            // Сообщаем адресату сразу. Без этого он узнает о сообщении своим
            // опросом — через несколько секунд.
            try { SignalingClient.Instance.Send("new_message", _currentPartnerId, 0, ""); }
            catch { }

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
            CheckIncomingCalls();
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
            _presenceTimer.Stop();
            // «Не в сети» — ДО закрытия сокета: после него отправлять уже
            // некуда, и собеседники ждали бы таймаута, глядя на зелёную точку.
            try { PresenceService.MarkOffline(); } catch { }
            DisconnectSignaling();
            UserSession.Clear();
            Close(); // LoginWindow снова покажется (см. LoginWindow.BtnLogin_Click)
        }

        // ─────────────── Звонки ───────────────
        private async void StartOutgoingCall(bool withVideo)
        {
            if (_currentPartnerId < 0) return;
            if (_activeCall != null) { _activeCall.Activate(); return; }

            if (!PISMO.Platform.PlatformServices.AudioAvailable)
            {
                await Dialogs.Info(this,
                    "Аудио для звонков доступно на Linux через ALSA (пакет alsa-utils). " +
                    "На этой системе устройство аудио не зарегистрировано.", "Звонки");
                return;
            }

            try
            {
                int myId = UserSession.EffectiveId;
                int sid = CallSignaling.FindActiveSession(myId, _currentPartnerId);
                bool caller = sid <= 0;
                if (caller)
                    sid = CallSignaling.CreateCall(myId, _currentPartnerId, withVideo);
                OpenCallWindow(sid, caller, _currentPartnerName);
            }
            catch (Exception ex)
            {
                await Dialogs.Error(this, "Не удалось начать звонок: " + ex.Message);
            }
        }

        private void CheckIncomingCalls()
        {
            if (_activeCall != null || _incomingOpen) return;
            if (!PISMO.Platform.PlatformServices.AudioAvailable) return;

            try
            {
                var incoming = CallSignaling.CheckIncoming(UserSession.EffectiveId, _lastCheckedCallId);
                foreach (var call in incoming)
                {
                    _lastCheckedCallId = Math.Max(_lastCheckedCallId, call.SessionId);
                    ShowIncoming(call);
                    break; // по одному за раз
                }
            }
            catch { /* опрос не должен ронять UI */ }
        }

        private async void ShowIncoming(CallSignaling.Incoming call)
        {
            _incomingOpen = true;
            var dlg = new IncomingCallWindow(call.CallerName);
            await dlg.ShowDialog(this);
            _incomingOpen = false;

            if (dlg.Accepted)
                OpenCallWindow(call.SessionId, isCaller: false, call.CallerName);
            else
                CallSignaling.RejectCall(call.SessionId);
        }

        private void OpenCallWindow(int sessionId, bool isCaller, string peerName)
        {
            _activeCall = new CallWindow(sessionId, isCaller, peerName);
            _activeCall.Closed += (_, _) => _activeCall = null;
            _activeCall.Show();
        }

        private static Color AvatarColor(int uid)
            => AvatarColors[Math.Abs(uid) % AvatarColors.Length];
    }
}
