using System;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace PISMO.Views
{
    public partial class LoginWindow : Window
    {
        // Сохраняем рядом с пользовательскими настройками (~/.config на Linux,
        // %AppData% на Windows) — как в Windows-версии.
        private static readonly string CredsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PISMO", "saved_login.dat");

        public LoginWindow()
        {
            InitializeComponent();

            BtnLogin.Click += BtnLogin_Click;
            BtnSave.Click += BtnSave_Click;
            BtnClear.Click += BtnClear_Click;
            LnkRegister.Click += (_, _) => new RegisterWindow().ShowDialog(this);
            TxtPass.KeyDown += (_, e) => { if (e.Key == Key.Enter) BtnLogin_Click(null, null); };

            Opened += (_, _) => TestDbOnStart();
            // Проверка обновлений — до входа: ничего не открыто, перезапуск
            // ничего не оборвёт. Молча уходит, если обновления нет.
            Opened += (_, _) => UpdateFlow.CheckInBackground(this);
            LoadSavedCredentials();
        }


        private async void TestDbOnStart()
        {
            var r = AuthService.TestConnection();
            if (!r.Ok)
                await Dialogs.Error(this, "Ошибка БД:\n" + r.Error);
        }

        private void LoadSavedCredentials()
        {
            try
            {
                if (!File.Exists(CredsFile)) return;
                string[] lines = File.ReadAllLines(CredsFile, Encoding.UTF8);
                if (lines.Length < 3) return;

                TxtLogin.Text = lines[0];
                TxtPass.Text = DecodePassword(lines[1]);
                ChkRemember.IsChecked = lines[2] == "1";
            }
            catch { /* повреждённый файл — игнорируем */ }
        }

        private void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            string login = (TxtLogin.Text ?? "").Trim();
            string pass = TxtPass.Text ?? "";

            var r = AuthService.Login(login, pass);
            if (!r.Ok)
            {
                ShowError(r.Error, isError: true);
                return;
            }

            if (ChkRemember.IsChecked == true)
                SaveCredentials(login, pass, true);

            var main = new MainWindow();
            main.Closed += (_, _) =>
            {
                if (UserSession.UserId == 0)
                {
                    // Выход из аккаунта — снова показываем окно входа.
                    TxtPass.Text = "";
                    LblError.IsVisible = false;
                    LoadSavedCredentials();
                    Show();
                }
                else
                {
                    // Окно закрыто без выхода — завершаем приложение.
                    (Avalonia.Application.Current.ApplicationLifetime
                        as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                }
            };
            main.Show();
            Hide();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtLogin.Text))
            {
                ShowError("Введите логин, чтобы сохранить.", isError: true);
                return;
            }
            SaveCredentials(TxtLogin.Text.Trim(), TxtPass.Text ?? "", ChkRemember.IsChecked == true);
            ShowError("Данные входа сохранены.", isError: false);
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(CredsFile)) File.Delete(CredsFile);
                TxtLogin.Text = "";
                TxtPass.Text = "";
                ChkRemember.IsChecked = false;
                ShowError("Сохранённые данные удалены.", isError: false);
            }
            catch (Exception ex)
            {
                ShowError("Не удалось удалить файл: " + ex.Message, isError: true);
            }
        }

        private void SaveCredentials(string login, string pass, bool remember)
        {
            try
            {
                string dir = Path.GetDirectoryName(CredsFile);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(CredsFile, new[]
                {
                    login,
                    EncodePassword(pass),
                    remember ? "1" : "0",
                }, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                ShowError("Не удалось сохранить данные: " + ex.Message, isError: true);
            }
        }

        private static string EncodePassword(string pass)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(pass ?? ""));

        private static string DecodePassword(string encoded)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); }
            catch { return string.Empty; }
        }

        private void ShowError(string msg, bool isError)
        {
            LblError.Foreground = new SolidColorBrush(
                isError ? Color.Parse("#f04747") : Color.Parse("#43b581"));
            LblError.Text = msg;
            LblError.IsVisible = true;
        }
    }
}
