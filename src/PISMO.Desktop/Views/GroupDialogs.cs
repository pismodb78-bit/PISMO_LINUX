using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PISMO.Views
{
    /// <summary>
    /// Окна групп: создать группу и посмотреть/поправить состав.
    /// Порт CreateGroupForm и GroupMembersForm Windows-версии.
    /// </summary>
    public static class GroupDialogs
    {
        private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#36393f"));
        private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#dcddde"));
        private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#72767d"));

        /// <summary>
        /// Создание группы. Возвращает её номер или 0, если передумали.
        ///
        /// Без участников группу создать можно: в неё потом добавляют. Но без
        /// названия — нет, иначе в списке появится безымянная строка, которую
        /// не отличить от соседних.
        /// </summary>
        public static async Task<int> Create(Window owner, int myId)
        {
            var name = new TextBox { Watermark = "Название группы" };

            var people = new List<UserItem>();
            try { people = MessageService.GetAllUsers().Where(u => u.Id != myId).ToList(); }
            catch { }

            var boxes = new List<CheckBox>();
            var list = new StackPanel { Spacing = 2 };
            foreach (var u in people)
            {
                var cb = new CheckBox
                {
                    Content = string.IsNullOrWhiteSpace(u.Name) ? u.Login : u.Name,
                    Foreground = Text,
                    Tag = u.Id,
                };
                boxes.Add(cb);
                list.Children.Add(cb);
            }

            var error = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.Parse("#f04747")),
                FontSize = 12, IsVisible = false, TextWrapping = TextWrapping.Wrap,
            };

            var create = new Button { Content = "Создать", Padding = new Avalonia.Thickness(20, 8) };
            create.Classes.Add("blurple");
            var cancel = new Button { Content = "Отмена", Padding = new Avalonia.Thickness(20, 8) };

            var tcs = new TaskCompletionSource<int>();

            var dlg = new Window
            {
                Title = "Новая группа",
                Width = 420, Height = 520,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Bg,
                Content = new DockPanel
                {
                    Margin = new Avalonia.Thickness(20),
                    Children =
                    {
                        Docked(new TextBlock
                        {
                            Text = "Новая группа", FontSize = 16, FontWeight = FontWeight.Bold,
                            Foreground = Brushes.White, Margin = new Avalonia.Thickness(0, 0, 0, 12),
                        }, Avalonia.Controls.Dock.Top),
                        Docked(name, Avalonia.Controls.Dock.Top),
                        Docked(new TextBlock
                        {
                            Text = "Кого добавить", Foreground = Muted, FontSize = 12,
                            Margin = new Avalonia.Thickness(0, 14, 0, 6),
                        }, Avalonia.Controls.Dock.Top),
                        Docked(error, Avalonia.Controls.Dock.Bottom),
                        Docked(new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Margin = new Avalonia.Thickness(0, 12, 0, 0),
                            Children = { cancel, create },
                        }, Avalonia.Controls.Dock.Bottom),
                        new ScrollViewer { Content = list },
                    },
                },
            };

            create.Click += (_, _) =>
            {
                string title = (name.Text ?? "").Trim();
                if (title.Length == 0)
                {
                    error.Text = "Без названия группу не отличить от соседних в списке.";
                    error.IsVisible = true;
                    return;
                }
                var chosen = boxes.Where(b => b.IsChecked == true)
                                  .Select(b => (int)b.Tag).ToList();
                try
                {
                    int id = GroupService.Create(title, myId, chosen);
                    tcs.TrySetResult(id);
                    dlg.Close();
                }
                catch (System.Exception ex)
                {
                    error.Text = "Не вышло: " + ex.Message;
                    error.IsVisible = true;
                }
            };
            cancel.Click += (_, _) => { tcs.TrySetResult(0); dlg.Close(); };
            dlg.Closed += (_, _) => tcs.TrySetResult(0);
            dlg.Opened += (_, _) => name.Focus();

            if (owner != null) _ = dlg.ShowDialog(owner);
            else Dispatcher.UIThread.Post(() => dlg.Show());
            return await tcs.Task;
        }

        /// <summary>
        /// Состав группы: кто есть, кого добавить, кого убрать.
        /// Возвращает true, если состав меняли — вызывающему надо обновить
        /// шапку с числом участников.
        /// </summary>
        public static async Task<bool> Members(Window owner, int groupId, int myId)
        {
            bool changed = false;
            var tcs = new TaskCompletionSource<bool>();

            var list = new StackPanel { Spacing = 4 };
            var dlg = new Window
            {
                Title = "Участники",
                Width = 380, Height = 460,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Bg,
            };

            void Refresh()
            {
                list.Children.Clear();
                List<GroupService.Member> members;
                try { members = GroupService.GetMembers(groupId); }
                catch { return; }

                var present = new HashSet<int>(members.Select(m => m.Id));

                foreach (var m in members)
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                    var who = new TextBlock
                    {
                        Text = m.Name + (m.IsAdmin ? "  · создатель" : ""),
                        Foreground = m.IsAdmin ? Brushes.White : Text,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    Grid.SetColumn(who, 0);
                    row.Children.Add(who);

                    // Создателя не убираем: группа без него не видна даже ему
                    // самому — список строится по group_members.
                    if (!m.IsAdmin && m.Id != myId)
                    {
                        var kick = new Button
                        {
                            Content = "✕", Background = Brushes.Transparent,
                            Foreground = new SolidColorBrush(Color.Parse("#f04747")),
                            Padding = new Avalonia.Thickness(6, 2),
                        };
                        int uid = m.Id;
                        kick.Click += (_, _) =>
                        {
                            try { GroupService.RemoveMember(groupId, uid); changed = true; Refresh(); }
                            catch { }
                        };
                        Grid.SetColumn(kick, 1);
                        row.Children.Add(kick);
                    }
                    list.Children.Add(row);
                }

                list.Children.Add(new TextBlock
                {
                    Text = "Добавить", Foreground = Muted, FontSize = 12,
                    Margin = new Avalonia.Thickness(0, 12, 0, 4),
                });

                try
                {
                    foreach (var u in MessageService.GetAllUsers())
                    {
                        if (present.Contains(u.Id)) continue;
                        var add = new Button
                        {
                            Content = "＋ " + (string.IsNullOrWhiteSpace(u.Name) ? u.Login : u.Name),
                            Background = Brushes.Transparent, Foreground = Text,
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Padding = new Avalonia.Thickness(4, 2),
                        };
                        int uid = u.Id;
                        add.Click += (_, _) =>
                        {
                            try { GroupService.AddMember(groupId, uid); changed = true; Refresh(); }
                            catch { }
                        };
                        list.Children.Add(add);
                    }
                }
                catch { }
            }

            Refresh();

            var close = new Button { Content = "Готово", Padding = new Avalonia.Thickness(20, 8) };
            close.Classes.Add("blurple");
            close.Click += (_, _) => dlg.Close();

            dlg.Content = new DockPanel
            {
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    Docked(new TextBlock
                    {
                        Text = "Участники", FontSize = 16, FontWeight = FontWeight.Bold,
                        Foreground = Brushes.White, Margin = new Avalonia.Thickness(0, 0, 0, 12),
                    }, Avalonia.Controls.Dock.Top),
                    Docked(new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Avalonia.Thickness(0, 12, 0, 0),
                        Children = { close },
                    }, Avalonia.Controls.Dock.Bottom),
                    new ScrollViewer { Content = list },
                },
            };

            dlg.Closed += (_, _) => tcs.TrySetResult(changed);
            if (owner != null) _ = dlg.ShowDialog(owner);
            else Dispatcher.UIThread.Post(() => dlg.Show());
            return await tcs.Task;
        }

        /// <summary>DockPanel.SetDock в одну строку — иначе разметка тонет в вызовах.</summary>
        private static Control Docked(Control c, Avalonia.Controls.Dock side)
        {
            DockPanel.SetDock(c, side);
            return c;
        }
    }
}
