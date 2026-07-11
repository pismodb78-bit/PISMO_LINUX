using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PISMO.Views
{
    public partial class ChangePasswordWindow : Window
    {
        public ChangePasswordWindow()
        {
            InitializeComponent();
            BtnUpdate.Click += BtnUpdate_Click;
        }


        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            var r = AuthService.ChangePassword(TxtOld.Text ?? "", TxtNew.Text ?? "", TxtConfirm.Text ?? "");
            if (!r.Ok)
            {
                LblError.Text = r.Error;
                LblError.IsVisible = true;
                return;
            }

            await Dialogs.Info(this, "Пароль успешно изменён!");
            Close();
        }
    }
}
