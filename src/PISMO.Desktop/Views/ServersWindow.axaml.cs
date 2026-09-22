using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace PISMO.Views
{
    /// <summary>
    /// Серверы как в Discord — порт ServersForm Windows-версии.
    ///
    /// Отдельным окном, а не вкладкой в главном: у серверов своя разметка в
    /// четыре колонки (серверы, каналы, переписка, участники), и втискивать
    /// её в окно личных сообщений значило бы ломать и то и другое.
    ///
    /// Переписку рисует общий MessageView — тот же, что в личных чатах и
    /// группах. Разное здесь только то, что и должно быть разным: удалять
    /// чужое в канале может модератор, а прочтений и подписи «в сети» тут
    /// нет вовсе.
    /// </summary>
    public partial class ServersWindow : Window
    {
        private readonly int _me = UserSession.EffectiveId;

        private int _serverId = -1;
        private string _serverName = "";
        private bool _canManage;

        private int _channelId = -1;
        private string _channelName = "";

        private int _replyToId;
        private byte[] _pending;
        private string _pendingName;
        private bool _pendingIsImage;

        private int _lastMaxId = -1;
        private int _voiceChannelId = -1;      // где я сейчас «в эфире»

        private readonly DispatcherTimer _poll;
        private Action<string, int, int, string> _wsHandler;

        private const int MaxAttachmentBytes = 16 * 1024 * 1024;

        private static readonly Color[] RailColors =
        {
            Color.Parse("#5865F2"), Color.Parse("#43b581"), Color.Parse("#faa61a"),
            Color.Parse("#f04747"), Color.Parse("#3498db"), Color.Parse("#9b59b6"),
        };

        public ServersWindow()
        {
            InitializeComponent();

            BtnAddServer.Click += async (_, _) => await AddServer();
            BtnServerMenu.Click += async (_, _) => await ServerMenu();
            BtnChSend.Click += (_, _) => SendCurrent();
            BtnChAttach.Click += async (_, _) => await PickAttachment();
            BtnChAttachCancel.Click += (_, _) => ClearAttachment();
            BtnChReplyCancel.Click += (_, _) => CancelReply();

            ChInput.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && e.KeyModifiers != KeyModifiers.Shift)
                {
                    e.Handled = true;
                    SendCurrent();
                }
            };

            LoadServers();

            // Опрос — запасной путь: события приходят по сокету, но сервера
            // может не быть, и тогда всё должно работать как раньше.
            _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _poll.Tick += (_, _) => PollTick();
            _poll.Start();

            _wsHandler = (type, sender, session, payload) =>
                Dispatcher.UIThread.Post(() => OnSignal(type, sender, session, payload));
            SignalingClient.Instance.OnMessage += _wsHandler;

            Closed += (_, _) =>
            {
                _poll.Stop();
                try { SignalingClient.Instance.OnMessage -= _wsHandler; } catch { }
                // Выходим из голосового канала: иначе останемся «в эфире» до
                // истечения свежести записи, и остальные будут видеть нас в
                // канале, которого мы уже не слышим.
                LeaveVoice();
            };
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        // ─────────────── Серверы ───────────────

        private void LoadServers()
        {
            ServerRail.Children.Clear();
            List<ServerService.ServerItem> servers;
            try { servers = ServerService.MyServers(_me); }
            catch (Exception ex)
            {
                _ = Dialogs.Error(this, "Не удалось получить список серверов: " + ex.Message);
                return;
            }

            foreach (var s in servers)
            {
                var badge = new Border
                {
                    Width = 48, Height = 48, CornerRadius = new CornerRadius(s.Id == _serverId ? 14 : 24),
                    Background = new SolidColorBrush(RailColors[Math.Abs(s.Id) % RailColors.Length]),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Child = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(s.Name) ? "?" : s.Name.Substring(0, 1).ToUpper(),
                        Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = 20,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                ToolTip.SetTip(badge, $"{s.Name} · {s.MemberCount} чел.");
                var item = s;
                badge.PointerPressed += (_, _) => OpenServer(item);
                ServerRail.Children.Add(badge);
            }

            if (servers.Count == 0)
                ServerRail.Children.Add(new TextBlock
                {
                    Text = "нет\nсерверов", FontSize = 10, TextAlignment = TextAlignment.Center,
                    Foreground = new SolidColorBrush(Color.Parse("#72767d")),
                });
            else if (_serverId < 0)
                OpenServer(servers[0]);
        }

        private void OpenServer(ServerService.ServerItem s)
        {
            _serverId = s.Id;
            _serverName = s.Name;
            _canManage = ServerService.CanManage(s.Id, _me);
            ServerName.Text = s.Name;
            BtnServerMenu.IsVisible = true;
            _channelId = -1;
            _lastMaxId = -1;
            ChannelTitle.Text = "Канал не выбран";
            ChannelMessages.Children.Clear();
            ChInput.IsEnabled = false;

            LoadServers();      // подсветить выбранный
            LoadChannels();
            LoadMembers();
        }

        private async Task AddServer()
        {
            string answer = await Dialogs.Prompt(this,
                "Название нового сервера — или номер существующего, чтобы войти",
                "", "Дальше", "Отмена");
            if (answer == null) return;
            answer = answer.Trim();
            if (answer.Length == 0) return;

            // Число — это попытка войти в уже существующий сервер; всё
            // остальное — название нового. Спрашивать «создать или войти?»
            // отдельным вопросом значит заставлять выбирать там, где ответ
            // виден из самого введённого.
            if (int.TryParse(answer, out int id))
            {
                if (!ServerService.Join(id, _me))
                {
                    await Dialogs.Error(this,
                        $"Войти в сервер №{id} не вышло: такого сервера нет либо вход закрыт.");
                    return;
                }
            }
            else
            {
                try { ServerService.Create(answer, _me); }
                catch (Exception ex)
                {
                    await Dialogs.Error(this, "Не удалось создать сервер: " + ex.Message);
                    return;
                }
            }
            _serverId = -1;
            LoadServers();
        }

        private async Task ServerMenu()
        {
            if (_serverId < 0) return;

            var options = new List<string> { $"Номер сервера: {_serverId} (скопировать)" };
            if (_canManage) options.Add("Новый канал");
            options.Add("Покинуть сервер");

            string pick = await Dialogs.Choose(this, _serverName, options);
            if (pick == null) return;

            if (pick.StartsWith("Номер"))
            {
                var cb = GetTopLevel(this)?.Clipboard;
                if (cb != null) await cb.SetTextAsync(_serverId.ToString());
            }
            else if (pick == "Новый канал")
            {
                string name = await Dialogs.Prompt(this, "Название канала", "", "Создать", "Отмена");
                if (string.IsNullOrWhiteSpace(name)) return;
                bool voice = await Dialogs.Confirm(this,
                    "Голосовой канал или текстовый?", "Тип канала", "Голосовой", "Текстовый");
                if (!ServerService.CreateChannel(_serverId, _me, name.Trim(), voice))
                    await Dialogs.Error(this, "Создать канал не вышло — нет прав.");
                LoadChannels();
            }
            else if (pick == "Покинуть сервер")
            {
                if (!await Dialogs.Confirm(this, $"Покинуть «{_serverName}»?", "Сервер", "Покинуть", "Отмена"))
                    return;
                ServerService.Leave(_serverId, _me);
                _serverId = -1;
                ServerName.Text = "Выберите сервер";
                BtnServerMenu.IsVisible = false;
                ChannelList.Children.Clear();
                MemberList.Children.Clear();
                ChannelMessages.Children.Clear();
                LoadServers();
            }
        }

        // ─────────────── Каналы ───────────────

        private void LoadChannels()
        {
            ChannelList.Children.Clear();
            if (_serverId < 0) return;

            var channels = ServerService.Channels(_serverId);
            var voice = ServerService.VoiceForServer(_serverId);

            bool textHeader = false, voiceHeader = false;
            foreach (var ch in channels)
            {
                if (!ch.IsVoice && !textHeader)
                {
                    ChannelList.Children.Add(Header("ТЕКСТОВЫЕ"));
                    textHeader = true;
                }
                if (ch.IsVoice && !voiceHeader)
                {
                    ChannelList.Children.Add(Header("ГОЛОСОВЫЕ"));
                    voiceHeader = true;
                }

                ChannelList.Children.Add(ChannelRow(ch));

                // Кто сейчас в голосовом — прямо под каналом, как в Discord.
                if (ch.IsVoice && voice.TryGetValue(ch.Id, out var people))
                    foreach (var p in people)
                        ChannelList.Children.Add(new TextBlock
                        {
                            Text = "   🔊 " + p.Name + (p.MicMuted ? " (микрофон выключен)" : ""),
                            FontSize = 12,
                            Foreground = new SolidColorBrush(Color.Parse("#43b581")),
                            Margin = new Thickness(10, 0, 0, 0),
                        });
            }

            if (channels.Count == 0)
                ChannelList.Children.Add(new TextBlock
                {
                    Text = "каналов нет", FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#72767d")),
                    Margin = new Thickness(10, 6, 0, 0),
                });
        }

        private static Control Header(string text) => new TextBlock
        {
            Text = text, FontSize = 10, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#72767d")),
            Margin = new Thickness(6, 10, 0, 2),
        };

        private Control ChannelRow(ServerService.ChannelItem ch)
        {
            var row = new Border
            {
                Padding = new Thickness(8, 5), CornerRadius = new CornerRadius(4),
                Background = ch.Id == _channelId
                    ? new SolidColorBrush(Color.Parse("#40444b")) : Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = (ch.IsVoice ? "🔊 " : "# ") + ch.Name,
                    Foreground = new SolidColorBrush(
                        ch.Id == _channelId ? Color.Parse("#ffffff") : Color.Parse("#8e9297")),
                    FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            row.PointerEntered += (_, _) => row.Background = new SolidColorBrush(Color.Parse("#40444b"));
            row.PointerExited += (_, _) =>
                row.Background = ch.Id == _channelId
                    ? new SolidColorBrush(Color.Parse("#40444b")) : Brushes.Transparent;

            var item = ch;
            row.PointerPressed += async (_, e) =>
            {
                if (_canManage && e.GetCurrentPoint(row).Properties.IsRightButtonPressed)
                {
                    if (await Dialogs.Confirm(this, $"Удалить канал «{item.Name}» со всей перепиской?",
                            "Канал", "Удалить", "Отмена"))
                    {
                        ServerService.DeleteChannel(_serverId, _me, item.Id);
                        if (_channelId == item.Id) { _channelId = -1; ChannelMessages.Children.Clear(); }
                        LoadChannels();
                    }
                    return;
                }
                if (item.IsVoice) ToggleVoice(item);
                else OpenChannel(item);
            };
            return row;
        }

        private void OpenChannel(ServerService.ChannelItem ch)
        {
            _channelId = ch.Id;
            _channelName = ch.Name;
            ChannelTitle.Text = "# " + ch.Name;
            ChInput.IsEnabled = true;
            CancelReply();
            LoadChannels();
            LoadMessages();
        }

        // ─────────────── Голосовые каналы ───────────────

        /// <summary>
        /// Зайти в голосовой канал или выйти из него.
        ///
        /// Сам разговор здесь пока не поднимается: транспорт звонка на Linux
        /// сделан для пары собеседников (CallTransport, DataChannel), а канал —
        /// это комната на многих. Присутствие при этом честное: нас видят все,
        /// и мы видим всех.
        /// </summary>
        private async void ToggleVoice(ServerService.ChannelItem ch)
        {
            if (_voiceChannelId == ch.Id) { LeaveVoice(); LoadChannels(); return; }

            LeaveVoice();
            _voiceChannelId = ch.Id;
            ServerService.VoiceHeartbeat(ch.Id, _me);
            LoadChannels();
            await Dialogs.Info(this,
                $"Вы в канале «{ch.Name}» — остальные это видят.\n\n" +
                "Голос в каналах серверов на Linux ещё не подключён: транспорт " +
                "звонка здесь рассчитан на двоих, а канал — это комната на многих. " +
                "Личные и групповые звонки работают.",
                "Голосовой канал");
        }

        private void LeaveVoice()
        {
            if (_voiceChannelId <= 0) return;
            try { ServerService.VoiceLeave(_voiceChannelId, _me); } catch { }
            _voiceChannelId = -1;
        }

        // ─────────────── Переписка канала ───────────────

        private void LoadMessages()
        {
            if (_channelId < 0) return;
            ChannelMessages.Children.Clear();

            try
            {
                var messages = ServerService.Messages(_channelId);

                var ids = new List<int>();
                foreach (var m in messages) ids.Add(m.Id);
                _reactions = ReactionsService.ForMessages(
                    ids, ReactionsService.Scope.Server, _me);

                string lastDate = "";
                foreach (var m in messages)
                {
                    string date = m.CreatedAt.ToString("d MMMM yyyy",
                        new System.Globalization.CultureInfo("ru-RU"));
                    if (date != lastDate)
                    {
                        ChannelMessages.Children.Add(MessageView.DateSeparator(date));
                        lastDate = date;
                    }
                    ChannelMessages.Children.Add(Bubble(m));
                }

                Dispatcher.UIThread.Post(() =>
                    ChannelScroll.Offset = new Vector(0, ChannelScroll.Extent.Height),
                    DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                _ = Dialogs.Error(this, "Не удалось прочитать канал: " + ex.Message);
            }
        }

        private Control Bubble(ChatMessage m) => MessageView.Build(m, new MessageView.Actions
        {
            // В канале пишут многие — без имени непонятно, кто.
            ShowSender = true,
            // Прочтений в каналах нет: колонки is_read в server_messages не
            // существует, и галочка означала бы неправду.
            ShowReadMarks = false,
            Reply = StartReply,
            Copy = async msg =>
            {
                var cb = GetTopLevel(this)?.Clipboard;
                if (cb != null) await cb.SetTextAsync(msg.Text);
            },
            Edit = async msg => await EditMessage(msg),
            // Чужое в канале удаляет модератор — то же условие стоит и в
            // самом запросе, здесь оно только про показ пункта меню.
            CanDelete = msg => msg.IsMine || _canManage,
            Delete = async msg => await DeleteMessage(msg),
            SaveFile = async msg => await SaveAttachment(msg),
            Reactions = msg => _reactions.TryGetValue(msg.Id, out var r) ? r : null,
            React = (msg, emoji) =>
            {
                ReactionsService.Toggle(msg.Id, ReactionsService.Scope.Server, _me, emoji);
                LoadMessages();
            },
        });

        private Dictionary<int, List<ReactionsService.Reaction>> _reactions = new();

        private void StartReply(ChatMessage m)
        {
            _replyToId = m.Id;
            ChReplyWho.Text = m.IsMine ? "Вы" : m.SenderName;
            ChReplyWhat.Text = string.IsNullOrWhiteSpace(m.Text)
                ? (m.HasImage ? "изображение" : m.HasFile ? "файл" : "сообщение")
                : m.Text;
            ChReplyBar.IsVisible = true;
            ChInput.Focus();
        }

        private void CancelReply()
        {
            _replyToId = 0;
            ChReplyBar.IsVisible = false;
        }

        private void SendCurrent()
        {
            if (_channelId < 0) return;
            string text = (ChInput.Text ?? "").Trim();
            if (text.Length == 0 && _pending == null) return;

            try
            {
                if (_pending != null && !_pendingIsImage)
                    ServerService.SendFile(_channelId, _me, text, _pending, _pendingName);
                else
                    ServerService.Send(_channelId, _me, text, _pending, _replyToId);
            }
            catch (Exception ex)
            {
                _ = Dialogs.Error(this, "Не удалось отправить: " + ex.Message);
                return;
            }

            // Событие широковещательное с номером КАНАЛА: состав сервера
            // знает база, а не отправитель.
            try { SignalingClient.Instance.Send("new_message", 0, _channelId, "channel"); }
            catch { }

            ChInput.Text = "";
            CancelReply();
            ClearAttachment();
            LoadMessages();
        }

        private async Task EditMessage(ChatMessage m)
        {
            string text = await Dialogs.Prompt(this, "Изменить сообщение", m.Text);
            if (text == null) return;
            text = text.Trim();
            if (text.Length == 0 || text == m.Text) return;

            if (!ServerService.Edit(_me, m.Id, text))
            {
                await Dialogs.Error(this, "Изменить не вышло: сообщение не ваше или уже удалено.");
                return;
            }
            try { SignalingClient.Instance.Send("edit", 0, _channelId, "channel"); } catch { }
            LoadMessages();
        }

        private async Task DeleteMessage(ChatMessage m)
        {
            if (!await Dialogs.Confirm(this, "Удалить это сообщение?", "Удаление", "Удалить", "Отмена"))
                return;
            if (!ServerService.Delete(_me, m.Id))
            {
                await Dialogs.Error(this, "Удалить не вышло — нет прав.");
                return;
            }
            try { SignalingClient.Instance.Send("edit", 0, _channelId, "channel"); } catch { }
            LoadMessages();
        }

        // ─────────────── Вложения ───────────────

        private async Task PickAttachment()
        {
            var top = GetTopLevel(this);
            if (top?.StorageProvider == null) return;

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Выберите файл",
                AllowMultiple = false,
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

                if (data.Length > MaxAttachmentBytes)
                {
                    await Dialogs.Error(this,
                        $"Файл {MessageView.HumanSize(data.Length)} — больше предела в " +
                        $"{MessageView.HumanSize(MaxAttachmentBytes)}.\n\n" +
                        "Вложение хранится целиком в базе, и файлы такого размера она не принимает.");
                    return;
                }

                _pending = data;
                _pendingName = file.Name;
                _pendingIsImage = IsImageName(file.Name);
                ChAttachName.Text = (_pendingIsImage ? "🖼 " : "📎 ") + file.Name
                                    + "  ·  " + MessageView.HumanSize(data.Length);
                ChAttachPreview.IsVisible = true;
            }
            catch (Exception ex)
            {
                await Dialogs.Error(this, "Не удалось прочитать файл: " + ex.Message);
            }
        }

        private static bool IsImageName(string name)
        {
            string ext = (Path.GetExtension(name ?? "") ?? "").ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";
        }

        private void ClearAttachment()
        {
            _pending = null;
            _pendingName = null;
            _pendingIsImage = false;
            ChAttachPreview.IsVisible = false;
        }

        private async Task SaveAttachment(ChatMessage m)
        {
            var top = GetTopLevel(this);
            if (top?.StorageProvider == null) return;

            var pick = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Сохранить файл",
                SuggestedFileName = string.IsNullOrWhiteSpace(m.FileName) ? "file" : m.FileName,
            });
            if (pick == null) return;

            try
            {
                var data = await Task.Run(() => ServerService.LoadFile(m.Id));
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

        // ─────────────── Участники ───────────────

        private void LoadMembers()
        {
            MemberList.Children.Clear();
            if (_serverId < 0) return;

            foreach (var m in ServerService.Members(_serverId))
            {
                var row = new StackPanel { Spacing = 0 };
                row.Children.Add(new TextBlock
                {
                    Text = m.Name,
                    Foreground = new SolidColorBrush(Color.Parse("#dcddde")),
                    FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
                });
                if (!string.IsNullOrWhiteSpace(m.RoleName))
                    row.Children.Add(new TextBlock
                    {
                        Text = m.RoleName, FontSize = 11,
                        Foreground = new SolidColorBrush(Color.Parse("#72767d")),
                    });

                var card = new Border
                {
                    Padding = new Thickness(6, 4), CornerRadius = new CornerRadius(4),
                    Background = Brushes.Transparent, Child = row,
                };

                // Исключить и забанить — только тем, у кого есть право, и
                // только не себя. Право всё равно перепроверяется запросом.
                var me = ServerService.Members(_serverId).FirstOrDefault(x => x.Id == _me);
                if (m.Id != _me && me != null && (me.CanKick || me.CanBan))
                {
                    var menu = new ContextMenu();
                    var items = new List<Control>();
                    if (me.CanKick)
                    {
                        var kick = new MenuItem { Header = "Исключить" };
                        kick.Click += (_, _) => { ServerService.Kick(_serverId, _me, m.Id, false); LoadMembers(); };
                        items.Add(kick);
                    }
                    if (me.CanBan)
                    {
                        var ban = new MenuItem { Header = "Забанить" };
                        ban.Click += (_, _) => { ServerService.Kick(_serverId, _me, m.Id, true); LoadMembers(); };
                        items.Add(ban);
                    }
                    menu.ItemsSource = items;
                    card.ContextMenu = menu;
                }

                MemberList.Children.Add(card);
            }
        }

        // ─────────────── Опрос и события ───────────────

        private void PollTick()
        {
            if (_serverId < 0) return;
            try
            {
                // Пока мы в голосовом канале, надо отмечаться: запись живёт,
                // только пока её обновляют.
                if (_voiceChannelId > 0) ServerService.VoiceHeartbeat(_voiceChannelId, _me);

                int max = ServerService.MaxMessageId(_serverId);
                if (max != _lastMaxId)
                {
                    _lastMaxId = max;
                    if (_channelId >= 0) LoadMessages();
                }
                LoadChannels();      // «в эфире» меняется без новых сообщений
            }
            catch { /* опрос не должен ронять окно */ }
        }

        private void OnSignal(string type, int senderId, int sessionId, string payload)
        {
            try
            {
                if (payload != "channel") return;
                if (sessionId != _channelId) return;
                if (type is "new_message" or "edit" or "pin" or "reaction") LoadMessages();
            }
            catch { }
        }
    }
}
