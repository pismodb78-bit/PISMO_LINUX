using System;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using MySql.Data.MySqlClient;

namespace PISMO.Views
{
    public partial class SettingsWindow : Window
    {
        private static string IpFile => Path.Combine(AppContext.BaseDirectory, "ip.txt");

        public SettingsWindow()
        {
            InitializeComponent();

            LoadConn();
            LoadTurn();

            BtnSaveConn.Click += BtnSaveConn_Click;
            BtnTestConn.Click += BtnTestConn_Click;
            BtnSaveTurn.Click += BtnSaveTurn_Click;
        }


        private void LoadConn()
        {
            try { if (File.Exists(IpFile)) TxtConn.Text = File.ReadAllText(IpFile).Trim(); }
            catch { /* игнор */ }
        }

        private void LoadTurn()
        {
            ChkTurnEnabled.IsChecked = TurnSettings.TurnEnabled;
            TxtTurnAddr.Text = TurnSettings.TurnServerAddress;
            TxtTurnPort.Text = TurnSettings.TurnServerPort.ToString();
            TxtTurnTtl.Text = TurnSettings.TurnCredentialsTtl.ToString();
            TxtTurnUser.Text = TurnSettings.TurnUsername;
            ChkTimeLimited.IsChecked = TurnSettings.UseTimeLimitedCredentials;
            CmbTransport.SelectedIndex =
                string.Equals(TurnSettings.TurnTransport, "tcp", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        private void BtnSaveConn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                File.WriteAllText(IpFile, (TxtConn.Text ?? "").Trim(), Encoding.UTF8);
                Status("Строка подключения сохранена. Перезапустите приложение.", ok: true);
            }
            catch (Exception ex)
            {
                Status("Не удалось сохранить: " + ex.Message, ok: false);
            }
        }

        private void BtnTestConn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string cs = (TxtConn.Text ?? "").Trim();
                if (!cs.Contains("=")) // формат host[:port]
                {
                    var parts = cs.Split(':');
                    var b = new MySqlConnectionStringBuilder
                    {
                        Server = parts[0].Trim(),
                        Port = parts.Length > 1 && uint.TryParse(parts[1].Trim(), out var p) ? p : 3306,
                        Database = "bdauth", UserID = "root", Password = "", CharacterSet = "utf8mb4",
                    };
                    cs = b.ConnectionString;
                }
                using var conn = new MySqlConnection(cs);
                conn.Open();
                Status("✓ Подключение успешно!", ok: true);
            }
            catch (Exception ex)
            {
                Status("✗ Ошибка: " + ex.Message, ok: false);
            }
        }

        private void BtnSaveTurn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TurnSettings.TurnEnabled = ChkTurnEnabled.IsChecked == true;
                TurnSettings.TurnServerAddress = (TxtTurnAddr.Text ?? "").Trim();
                if (int.TryParse(TxtTurnPort.Text, out var port)) TurnSettings.TurnServerPort = port;
                TurnSettings.TurnTransport = (CmbTransport.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "udp";
                if (int.TryParse(TxtTurnTtl.Text, out var ttl)) TurnSettings.TurnCredentialsTtl = ttl;
                TurnSettings.TurnUsername = (TxtTurnUser.Text ?? "").Trim();
                TurnSettings.UseTimeLimitedCredentials = ChkTimeLimited.IsChecked == true;
                Status("Настройки TURN сохранены.", ok: true);
            }
            catch (Exception ex)
            {
                Status("Не удалось сохранить TURN: " + ex.Message, ok: false);
            }
        }

        private void Status(string msg, bool ok)
        {
            LblStatus.Text = msg;
            LblStatus.Foreground = new SolidColorBrush(Color.Parse(ok ? "#43b581" : "#f04747"));
            LblStatus.IsVisible = true;
        }
    }
}
