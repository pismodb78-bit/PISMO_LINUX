using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PISMO.Views
{
    /// <summary>
    /// Друзья — порт FriendsAddForm: вкладки «В сети», «Все», «Ожидание»,
    /// «Найти». Собрано кодом, а не разметкой: строки тут одинаковые и
    /// отличаются только набором кнопок, и в XAML это вышло бы четыре почти
    /// одинаковых списка вместо одного метода.
    /// </summary>
    public sealed class FriendsWindow : Window
    {
        private readonly int _me = UserSession.EffectiveId;
        private readonly StackPanel _list = new() { Spacing = 4 };
        private readonly TextBox _search = new() { Watermark = "Имя или логин" };
        private readonly TabControl _tabs = new();
        private readonly DispatcherTimer _poll;

        /// <summary>Открыть переписку с этим человеком. Ставит MainWindow.</summary>
        public Action<int, string> OpenChat;

        private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#dcddde"));
        private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#72767d"));

        public FriendsWindow()
        {
            Title = "PISMO — Друзья";
            Width = 560; Height = 620;
            Background = new SolidColorBrush(Color.Parse("#36393f"));
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            foreach (var name in new[] { "В сети", "Все", "Ожидание", "Найти" })
                _tabs.Items.Add(new TabItem { Header = name });
            _tabs.SelectedIndex = 1;
            _tabs.SelectionChanged += (_, _) => Refresh();

            _search.KeyUp += (_, e) => { if (e.Key == Key.Enter) Refresh(); };

            var searchRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Avalonia.Thickness(0, 10, 0, 6),
            };
            Grid.SetColumn(_search, 0);
            searchRow.Children.Add(_search);
            var go = new Button { Content = "Найти", Margin = new Avalonia.Thickness(8, 0, 0, 0) };
            go.Classes.Add("blurple");
            go.Click += (_, _) => { _tabs.SelectedIndex = 3; Refresh(); };
            Grid.SetColumn(go, 1);
            searchRow.Children.Add(go);

            Content = new DockPanel
            {
                Margin = new Avalonia.Thickness(16),
                Children =
                {
                    Top(_tabs),
                    Top(searchRow),
                    new ScrollViewer { Content = _list },
                },
            };

            Refresh();

            // Заявки приходят не от нас, и узнать о них можно только спросив.
            // Плюс событие по сокету — оно быстрее, но может не дойти.
            _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _poll.Tick += (_, _) => { if (_tabs.SelectedIndex == 2) Refresh(); };
            _poll.Start();

            Action<string, int, int, string> ws = (type, _, _, _) =>
            {
                if (type == "friend") Dispatcher.UIThread.Post(Refresh);
            };
            SignalingClient.Instance.OnMessage += ws;
            Closed += (_, _) =>
            {
                _poll.Stop();
                try { SignalingClient.Instance.OnMessage -= ws; } catch { }
            };
        }

        private static Control Top(Control c)
        {
            DockPanel.SetDock(c, Dock.Top);
            return c;
        }

        private void Refresh()
        {
            _list.Children.Clear();
            List<FriendsService.UserHit> items;

            try
            {
                items = _tabs.SelectedIndex switch
                {
                    0 => Online(FriendsService.Friends(_me)),
                    1 => FriendsService.Friends(_me),
                    2 => FriendsService.Incoming(_me),
                    _ => FriendsService.Search(_me, _search.Text ?? ""),
                };
            }
            catch (Exception ex)
            {
                _list.Children.Add(new TextBlock { Text = "Ошибка: " + ex.Message, Foreground = Muted });
                return;
            }

            if (items.Count == 0)
            {
                _list.Children.Add(new TextBlock
                {
                    Text = _tabs.SelectedIndex switch
                    {
                        0 => "Никого из друзей нет в сети.",
                        2 => "Заявок нет.",
                        3 => "Ничего не нашлось.",
                        _ => "Друзей пока нет. Найдите кого-нибудь во вкладке «Найти».",
                    },
                    Foreground = Muted, Margin = new Avalonia.Thickness(4, 10, 0, 0),
                });
                return;
            }

            foreach (var h in items) _list.Children.Add(Row(h));
        }

        /// <summary>Из списка друзей — только те, кто сейчас на связи.</summary>
        private List<FriendsService.UserHit> Online(List<FriendsService.UserHit> all)
        {
            var ids = new List<int>();
            foreach (var h in all) ids.Add(h.Id);
            var states = PresenceService.Read(ids);
            if (states == null) return all;   // не спросилось — лучше показать всех

            var res = new List<FriendsService.UserHit>();
            foreach (var h in all)
                if (states.TryGetValue(h.Id, out int st) && st > 0) res.Add(h);
            return res;
        }

        private Control Row(FriendsService.UserHit h)
        {
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            texts.Children.Add(new TextBlock
            {
                Text = h.Name, Foreground = Text, FontWeight = FontWeight.SemiBold, FontSize = 14,
            });
            texts.Children.Add(new TextBlock { Text = "@" + h.Login, Foreground = Muted, FontSize = 12 });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };

            void Add(string caption, bool primary, Action act)
            {
                var b = new Button { Content = caption, Padding = new Avalonia.Thickness(12, 6) };
                if (primary) b.Classes.Add("blurple");
                b.Click += (_, _) => { act(); Refresh(); };
                buttons.Children.Add(b);
            }

            switch (h.Rel)
            {
                case FriendsService.Relation.Friend:
                    Add("Написать", true, () => { OpenChat?.Invoke(h.Id, h.Name); Close(); });
                    Add("Удалить", false, () => FriendsService.Remove(_me, h.Id));
                    break;
                case FriendsService.Relation.IncomingPending:
                    Add("Принять", true, () => FriendsService.Accept(_me, h.Id));
                    Add("Отклонить", false, () => FriendsService.Decline(_me, h.Id));
                    break;
                case FriendsService.Relation.OutgoingPending:
                    // Заявка уже отправлена — предлагать отправить снова
                    // бессмысленно, но отменить её человек вправе.
                    buttons.Children.Add(new TextBlock
                    {
                        Text = "заявка отправлена", Foreground = Muted, FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    Add("Отменить", false, () => FriendsService.Remove(_me, h.Id));
                    break;
                default:
                    Add("Добавить", true, () => FriendsService.SendRequest(_me, h.Id));
                    break;
            }

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(texts, 0);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(texts);
            grid.Children.Add(buttons);

            var card = new Border
            {
                Padding = new Avalonia.Thickness(10, 8),
                CornerRadius = new Avalonia.CornerRadius(6),
                Background = new SolidColorBrush(Color.Parse("#2f3136")),
                Child = grid,
            };
            // По самой строке — профиль: отдельная кнопка «профиль» в каждой
            // строке заняла бы место у тех, что действительно нужны.
            card.PointerPressed += async (_, e) =>
            {
                if (e.GetCurrentPoint(card).Properties.IsRightButtonPressed)
                    await ProfileDialog.Show(this, h.Id);
            };
            return card;
        }
    }
}
