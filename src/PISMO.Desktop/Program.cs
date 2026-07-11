using System;
using Avalonia;

namespace PISMO
{
    internal static class Program
    {
        // Точка входа. В отличие от WinForms не нужен [STAThread] и
        // ApplicationConfiguration — Avalonia сама поднимает нужный backend
        // (X11/Wayland на Linux, Win32 на Windows, macOS на Mac).
        [STAThread]
        public static void Main(string[] args) => BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);

        // Используется дизайнером и превьюером Avalonia.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
