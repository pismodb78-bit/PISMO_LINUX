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
        // Открыта группа, а не личная переписка. −1 — не открыта.
        // Взаимоисключающе с _currentPartnerId: открыто ровно одно.
        private int _currentGroupId = -1;
        // Какой максимальный чужой id мы уже видели в каждой группе.
        // Прочтений у групп нет (в group_messages нет is_read), поэтому
        // «новое» определяется так же, как на ПК, — своей отметкой.
        private readonly Dictionary<int, int> _groupSeen = new();
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
            BtnReplyCancel.Click += (_, _) => CancelReply();
            BtnNewGroup.Click += async (_, _) =>
            {
                int id = await GroupDialogs.Create(this, UserSession.EffectiveId);
                if (id <= 0) return;
                LoadConversations();
                // Сразу открываем созданное: иначе человек ищет свою же
                // группу в списке, только что её заведя.
                foreach (var g in GroupService.GetGroups(UserSession.EffectiveId))
                    if (g.Id == id) { OpenGroup(g.Id, g.Name, g.MemberCount, g.MaxMessageId); break; }
            };
            BtnMembers.Click += async (_, _) =>
            {
                if (!InGroup) return;
                if (await GroupDialogs.Members(this, _currentGroupId, UserSession.EffectiveId))
                    LoadConversations();
            };
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
                        // Для группы sessionId — её номер: перечитываем, только
                        // если открыта именно она. Для личной — только если
                        // пишет открытый собеседник.
                        if (payload == "group")
                        {
                            if (InGroup && sessionId == _currentGroupId) LoadMessages();
                        }
                        else if (_currentPartnerId > 0) LoadMessages();
                        LoadConversations();
                        break;

                    case "read":
                        // Собеседник прочитал мои сообщения.
                        if (senderId == _currentPartnerId) LoadMessages();
                        break;

                    case "presence":
                        ApplyPresencePush(senderId, sessionId, payload);
                        break;

                    case "edit":
                    case "pin":
                        // Правка, удаление или закреп — перечитываем открытый
                        // чат. Число сообщений при этом не меняется, поэтому
                        // обычный опрос такого не замечает вовсе.
                        if (payload == "group")
                        {
                            if (InGroup && sessionId == _currentGroupId) LoadMessages();
                        }
                        else if (InGroup || _currentPartnerId > 0) LoadMessages();
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
                // Группы сверху — они не устаревают так, как переписка, и
                // искать их среди десятков личных диалогов неудобно.
                var groups = GroupService.GetGroups(myId);
                if (groups.Count > 0)
                {
                    UserListPanel.Children.Add(SectionHeader("ГРУППЫ"));
                    foreach (var g in groups) UserListPanel.Children.Add(BuildGroupCard(g));
                    UserListPanel.Children.Add(SectionHeader(
                        UserSession.Role == "admin" && !UserSession.IsImpersonating
                            ? "ПОЛЬЗОВАТЕЛИ" : "ЛИЧНЫЕ СООБЩЕНИЯ"));
                }

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

        private static Control SectionHeader(string text) => new TextBlock
        {
            Text = text, FontSize = 10, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#72767d")),
            Margin = new Thickness(6, 10, 0, 2),
        };

        /// <summary>
        /// Карточка группы. Вместо кружка статуса — значок, а вместо счётчика
        /// непрочитанных точка: прочтений у групп нет (в group_messages нет
        /// is_read), и точное число вывести неоткуда. Точка честнее числа,
        /// взятого с потолка.
        /// </summary>
        private Control BuildGroupCard(GroupService.GroupItem g)
        {
            var avatar = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(AvatarColor(g.Id * 7 + 3)),
                Child = new TextBlock
                {
                    Text = "#", Foreground = Brushes.White,
                    FontWeight = FontWeight.Bold, FontSize = 18,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            texts.Children.Add(new TextBlock
            {
                Text = g.Name, Foreground = new SolidColorBrush(Color.Parse("#dcddde")),
                FontWeight = FontWeight.SemiBold, FontSize = 14,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            texts.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(g.LastMessage)
                    ? $"{g.MemberCount} участников" : g.LastMessage,
                Foreground = new SolidColorBrush(Color.Parse("#72767d")),
                FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
            });

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            Grid.SetColumn(avatar, 0);
            Grid.SetColumn(texts, 1);
            texts.Margin = new Thickness(10, 0, 6, 0);
            grid.Children.Add(avatar);
            grid.Children.Add(texts);

            _groupSeen.TryGetValue(g.Id, out int seen);
            if (g.MaxMessageId > seen && g.Id != _currentGroupId)
            {
                var dot = new Border
                {
                    Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
                    Background = new SolidColorBrush(Color.Parse("#f04747")),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(dot, 2);
                grid.Children.Add(dot);
            }

            var card = new Border
            {
                Padding = new Thickness(8), CornerRadius = new CornerRadius(6),
                Background = g.Id == _currentGroupId
                    ? new SolidColorBrush(Color.Parse("#40444b")) : Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = grid,
            };
            card.PointerEntered += (_, _) => card.Background = new SolidColorBrush(Color.Parse("#40444b"));
            card.PointerExited += (_, _) =>
                card.Background = g.Id == _currentGroupId
                    ? new SolidColorBrush(Color.Parse("#40444b")) : Brushes.Transparent;
            card.PointerPressed += (_, _) => OpenGroup(g.Id, g.Name, g.MemberCount, g.MaxMessageId);
            return card;
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
            _currentGroupId = -1;
            _currentPartnerName = partnerName;
            ChatHeaderName.Text = partnerName;
            BtnAudioCall.IsVisible = true;
            BtnVideoCall.IsVisible = true;
            BtnMembers.IsVisible = false;
            CancelReply();

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

        /// <summary>Открывает групповой чат.</summary>
        private void OpenGroup(int groupId, string groupName, int memberCount, int maxSeenId)
        {
            _currentGroupId = groupId;
            _currentPartnerId = -1;
            _currentPartnerName = groupName;
            ChatHeaderName.Text = groupName;
            ChatHeaderStatus.Text = memberCount == 1 ? "1 участник" : $"{memberCount} участников";
            ChatHeaderStatus.Foreground = new SolidColorBrush(Color.Parse("#72767d"));
            ChatHeaderStatus.IsVisible = true;

            // Звонков в группах на Linux пока нет — кнопки прячем, чтобы не
            // предлагать то, чего не будет.
            BtnAudioCall.IsVisible = false;
            BtnVideoCall.IsVisible = false;
            BtnMembers.IsVisible = true;
            CancelReply();

            // Открыли — значит прочитали до этого места.
            _groupSeen[groupId] = maxSeenId;

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
        /// <summary>Открыта группа, а не личная переписка.</summary>
        private bool InGroup => _currentGroupId >= 0;

        private void LoadMessages()
        {
            if (!InGroup && _currentPartnerId < 0) return;
            MessagesPanel.Children.Clear();

            try
            {
                var messages = InGroup
                    ? GroupService.GetMessages(_currentGroupId)
                    : MessageService.GetMessages(UserSession.EffectiveId, _currentPartnerId);

                // Закрепы — одним запросом на всю переписку, а не по одному на
                // сообщение: их единицы, а сообщений могут быть сотни.
                var pinned = InGroup
                    ? PinsRepository.PinnedIds(1, UserSession.EffectiveId, _currentGroupId)
                    : PinsRepository.PinnedIds(0, UserSession.EffectiveId, _currentPartnerId);
                foreach (var msg in messages) msg.IsPinned = pinned.Contains(msg.Id);

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
                return WrapBubble(m, content, muted: true);
            }

            // Цитата того, на что отвечают.
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
                    // Полоска слева — как в любом мессенджере: она отделяет
                    // цитату от самого сообщения без лишней рамки.
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
            {
                content.Children.Add(new TextBlock
                {
                    Text = m.Text, TextWrapping = TextWrapping.Wrap,
                    Foreground = m.IsMine ? Brushes.White : new SolidColorBrush(Color.Parse("#dcddde")),
                    FontSize = 14,
                });
            }

            // Файл — кнопкой: сами байты лежат в базе и тянутся только когда
            // на неё нажали, иначе каждая отрисовка переписки качала бы все
            // когда-либо присланные файлы.
            if (m.HasFile)
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
                int fileId = m.Id;
                string fileName = m.FileName;
                save.Click += async (_, _) => await SaveAttachment(fileId, fileName);
                content.Children.Add(save);
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
            // Галочка прочтения — только на своих: у чужих она бессмысленна.
            if (m.IsMine)
                meta.Children.Add(new TextBlock
                {
                    Text = m.IsRead ? "✓✓" : "✓", FontSize = 10, Foreground = metaBrush,
                });
            content.Children.Add(meta);

            return WrapBubble(m, content, muted: false);
        }

        /// <summary>Общая оболочка пузыря — фон, скругление, меню действий.</summary>
        private Control WrapBubble(ChatMessage m, Control content, bool muted)
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
            if (!muted) bubble.ContextMenu = BuildMessageMenu(m);
            return bubble;
        }

        /// <summary>Меню сообщения: ответить, закрепить, изменить, удалить.</summary>
        private ContextMenu BuildMessageMenu(ChatMessage m)
        {
            var menu = new ContextMenu();
            var items = new List<Control>();

            var reply = new MenuItem { Header = "Ответить" };
            reply.Click += (_, _) => StartReply(m);
            items.Add(reply);

            if (!string.IsNullOrEmpty(m.Text))
            {
                var copy = new MenuItem { Header = "Копировать текст" };
                copy.Click += async (_, _) =>
                {
                    var cb = GetTopLevel(this)?.Clipboard;
                    if (cb != null) await cb.SetTextAsync(m.Text);
                };
                items.Add(copy);
            }

            var pin = new MenuItem { Header = m.IsPinned ? "Открепить" : "Закрепить" };
            pin.Click += (_, _) => TogglePin(m);
            items.Add(pin);

            // Править и удалять можно только своё — это же условие стоит и в
            // самом UPDATE, чтобы отсутствие пункта в меню не было
            // единственной защитой.
            if (m.IsMine)
            {
                items.Add(new Separator());

                var edit = new MenuItem { Header = "Изменить" };
                edit.Click += async (_, _) => await EditMessage(m);
                items.Add(edit);

                var del = new MenuItem { Header = "Удалить" };
                del.Click += async (_, _) => await DeleteMessage(m);
                items.Add(del);
            }

            menu.ItemsSource = items;
            return menu;
        }

        // ─────────────── Действия над сообщением ───────────────

        private int _replyToId;

        private void StartReply(ChatMessage m)
        {
            _replyToId = m.Id;
            ReplyWho.Text = m.IsMine ? "Вы" : (string.IsNullOrWhiteSpace(m.SenderName)
                ? _currentPartnerName : m.SenderName);
            ReplyWhat.Text = string.IsNullOrWhiteSpace(m.Text)
                ? (m.HasImage ? "изображение" : m.HasFile ? "файл" : "сообщение")
                : m.Text;
            ReplyBar.IsVisible = true;
            TxtMessage.Focus();
        }

        private void CancelReply()
        {
            _replyToId = 0;
            ReplyBar.IsVisible = false;
        }

        private void TogglePin(ChatMessage m)
        {
            int scope = InGroup ? 1 : 0;
            System.Threading.Tasks.Task.Run(() =>
            {
                bool ok = PinsRepository.Toggle(m.Id, scope, UserSession.EffectiveId);
                string err = PinsRepository.LastError;
                Dispatcher.UIThread.Post(() =>
                {
                    // Молчать об ошибке нельзя: «нажал и ничего» выглядит
                    // одинаково и при откреплении, и при отказе базы.
                    if (!ok && !string.IsNullOrEmpty(err))
                        _ = Dialogs.Error(this, "Не удалось закрепить: " + err);
                    LoadMessages();
                });
            });
        }

        private async System.Threading.Tasks.Task EditMessage(ChatMessage m)
        {
            string text = await Dialogs.Prompt(this, "Изменить сообщение", m.Text);
            if (text == null) return;                  // отмена
            text = text.Trim();
            if (text.Length == 0 || text == m.Text) return;

            try
            {
                bool ok = InGroup
                    ? GroupService.Edit(UserSession.EffectiveId, m.Id, text)
                    : MessageService.EditMessage(UserSession.EffectiveId, m.Id, text);
                if (!ok)
                {
                    await Dialogs.Error(this, "Изменить не вышло: сообщение не ваше или уже удалено.");
                    return;
                }
                AnnounceChange("edit", m.Id);
            }
            catch (Exception ex)
            {
                await Dialogs.Error(this, "Ошибка правки: " + ex.Message);
                return;
            }
            LoadMessages();
            LoadConversations();
        }

        private async System.Threading.Tasks.Task DeleteMessage(ChatMessage m)
        {
            if (!await Dialogs.Confirm(this, "Удалить это сообщение?", "Удаление", "Удалить", "Отмена"))
                return;
            try
            {
                if (InGroup) GroupService.Delete(UserSession.EffectiveId, m.Id);
                else MessageService.DeleteMessage(UserSession.EffectiveId, m.Id);
                AnnounceChange("edit", m.Id);
            }
            catch (Exception ex)
            {
                await Dialogs.Error(this, "Ошибка удаления: " + ex.Message);
                return;
            }
            LoadMessages();
            LoadConversations();
        }

        private async System.Threading.Tasks.Task SaveAttachment(int messageId, string suggestedName)
        {
            var top = GetTopLevel(this);
            if (top?.StorageProvider == null) return;

            var pick = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Сохранить файл",
                SuggestedFileName = string.IsNullOrWhiteSpace(suggestedName) ? "file" : suggestedName,
            });
            if (pick == null) return;

            try
            {
                // Байты тянем только сейчас — в переписке лежал один размер.
                bool grp = InGroup;
                var data = await System.Threading.Tasks.Task.Run(() =>
                    grp ? GroupService.LoadFile(messageId) : MessageService.LoadFile(messageId));
                if (data == null || data.Length == 0)
                {
                    await Dialogs.Error(this, "Файл не найден в базе.");
                    return;
                }
                await using var stream = await pick.OpenWriteAsync();
                await stream.WriteAsync(data);
            }
            catch (Exception ex)
            {
                await Dialogs.Error(this, "Не удалось сохранить: " + ex.Message);
            }
        }

        /// <summary>
        /// Сообщает об изменении сообщения. У личной переписки адресат один,
        /// у группы его нет вовсе — состав знает база, а не отправитель,
        /// поэтому уходит широковещательно с номером группы.
        /// </summary>
        private void AnnounceChange(string type, int messageId)
        {
            try
            {
                if (InGroup)
                    SignalingClient.Instance.Send(type, 0, _currentGroupId, "group");
                else
                    SignalingClient.Instance.Send(type, _currentPartnerId, messageId, "");
            }
            catch { }
        }

        private static string HumanSize(long bytes)
        {
            if (bytes < 1024) return bytes + " Б";
            double kb = bytes / 1024.0;
            return kb > 1024
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0} МБ", kb / 1024.0)
                : (int)kb + " КБ";
        }

        // ─────────────── Отправка ───────────────
        private void SendCurrent()
        {
            if (!InGroup && _currentPartnerId < 0) return;
            string text = (TxtMessage.Text ?? "").Trim();
            if (string.IsNullOrEmpty(text) && _pendingImage == null) return;

            int me = UserSession.EffectiveId;
            try
            {
                if (InGroup)
                {
                    if (_pendingImage != null && !_pendingIsImage)
                        GroupService.SendFile(_currentGroupId, me, text, _pendingImage, _pendingImageName);
                    else
                        GroupService.Send(_currentGroupId, me, text, _pendingImage, _replyToId);
                }
                else if (_pendingImage != null && !_pendingIsImage)
                    MessageService.SendFile(me, _currentPartnerId, text, _pendingImage, _pendingImageName);
                else if (_replyToId > 0)
                    MessageService.SendReply(me, _currentPartnerId, text, _pendingImage, _replyToId);
                else
                    MessageService.SendMessage(me, _currentPartnerId, text, _pendingImage);
            }
            catch (Exception ex)
            {
                _ = Dialogs.Error(this, "Ошибка отправки: " + ex.Message);
                return;
            }
            CancelReply();

            // Сообщаем сразу. Без этого получатель узнает о сообщении своим
            // опросом — через несколько секунд. Для группы адресата нет: её
            // состав знает база, а не отправитель, поэтому событие уходит
            // широковещательно с номером группы, как на ПК и на телефоне.
            try
            {
                if (InGroup)
                    SignalingClient.Instance.Send("new_message", 0, _currentGroupId, "group");
                else
                    SignalingClient.Instance.Send("new_message", _currentPartnerId, 0, "");
            }
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

            // Берём ЛЮБОЙ файл, а не только картинку. Картинки показываем в
            // переписке, остальное отправляем вложением — как на ПК. Раньше
            // кроме изображений отправить было нечего вовсе.
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Выберите файл",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Изображения")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp" },
                    },
                    new FilePickerFileType("Любые файлы") { Patterns = new[] { "*" } },
                },
            });

            var file = files?.FirstOrDefault();
            if (file == null) return;

            try
            {
                byte[] data;
                await using (var stream = await file.OpenReadAsync())
                {
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    data = ms.ToArray();
                }

                // Предел ставим сразу и объясняем: вложение целиком ложится в
                // одну строку таблицы, и сервер отказывает по max_allowed_packet
                // сообщением, по которому понять ничего нельзя.
                if (data.Length > MaxAttachmentBytes)
                {
                    await Dialogs.Error(this,
                        $"Файл {HumanSize(data.Length)} — это больше предела в " +
                        $"{HumanSize(MaxAttachmentBytes)}.\n\n" +
                        "Вложение хранится целиком в базе, и файлы такого размера " +
                        "она не принимает.");
                    return;
                }

                _pendingImage = data;
                _pendingImageName = file.Name;
                _pendingIsImage = IsImageName(file.Name);
                AttachName.Text = (_pendingIsImage ? "🖼 " : "📎 ") + file.Name
                                  + "  ·  " + HumanSize(data.Length);
                AttachPreview.IsVisible = true;
            }
            catch (Exception ex)
            {
                await Dialogs.Error(this, "Не удалось прочитать файл: " + ex.Message);
            }
        }

        /// <summary>Сколько байт вложения готовы положить в одну строку таблицы.</summary>
        private const int MaxAttachmentBytes = 16 * 1024 * 1024;

        private bool _pendingIsImage;

        private static bool IsImageName(string name)
        {
            string ext = (System.IO.Path.GetExtension(name ?? "") ?? "").ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";
        }

        private void ClearAttachment()
        {
            _pendingImage = null;
            _pendingImageName = null;
            _pendingIsImage = false;
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

                // Группы считаются отдельно: их сообщения лежат в другой
                // таблице, и счётчик личной переписки о них ничего не знает.
                // Сверяем один максимальный id на все группы — это одно
                // движение к концу индекса, сколько бы сообщений ни было.
                int groupMax = SafeGroupMaxId();
                if (groupMax != _lastGroupMax)
                {
                    _lastGroupMax = groupMax;
                    if (InGroup) LoadMessages();
                    LoadConversations();
                }
            }
            catch { /* поллинг не должен ронять UI */ }
        }

        private int _lastGroupMax = -1;

        /// <summary>Максимальный id сообщения среди МОИХ групп. Дёшево.</summary>
        private int SafeGroupMaxId()
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySql.Data.MySqlClient.MySqlCommand(
                    "SELECT COALESCE(MAX(gm.id),0) FROM group_messages gm " +
                    "JOIN group_members mem ON mem.group_id = gm.group_id AND mem.user_id=@me", conn);
                cmd.Parameters.AddWithValue("@me", UserSession.EffectiveId);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch { return _lastGroupMax; }
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
