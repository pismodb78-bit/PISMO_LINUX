using Avalonia.Controls;

namespace PISMO.Views
{
    public partial class IncomingCallWindow : Window
    {
        public bool Accepted { get; private set; }

        public IncomingCallWindow(string callerName)
        {
            InitializeComponent();
            CallerName.Text = callerName ?? "";
            AvatarLetter.Text = string.IsNullOrEmpty(callerName) ? "?" : callerName.Substring(0, 1).ToUpper();

            BtnAccept.Click += (_, _) => { Accepted = true; Close(); };
            BtnReject.Click += (_, _) => { Accepted = false; Close(); };
        }
    }
}
