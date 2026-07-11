using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PISMO.Views;

namespace PISMO
{
    public partial class App : Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Загружаем настройки TURN один раз при старте (как в Windows-версии).
                TurnSettings.Load();

                // Регистрируем платформенное аудио для звонков.
                // Linux — ALSA (arecord/aplay). Другие ОС можно добавить позже.
                if (OperatingSystem.IsLinux())
                    PISMO.Platform.PlatformServices.AudioDeviceFactory =
                        () => new PISMO.Platform.AlsaAudioDevice();

                // Приложение живёт, пока открыто хотя бы одно окно; при выходе из
                // аккаунта MainWindow закрывается и снова показывается LoginWindow.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                desktop.MainWindow = new LoginWindow();
                desktop.MainWindow.Show();
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
