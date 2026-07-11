using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PISMO.Views
{
    public partial class RegisterWindow : Window
    {
        public RegisterWindow()
        {
            InitializeComponent();
            BtnRegister.Click += BtnRegister_Click;
            LnkBack.Click += (_, _) => Close();
        }


        private async void BtnRegister_Click(object sender, RoutedEventArgs e)
        {
            var r = AuthService.Register(TxtName.Text, TxtSurname.Text, TxtLogin.Text, TxtPass.Text ?? "");
            if (!r.Ok)
            {
                LblError.Text = r.Error;
                LblError.IsVisible = true;
                return;
            }

            await Dialogs.Info(this, "Аккаунт создан! Теперь войдите.");
            Close();
        }
    }
}
