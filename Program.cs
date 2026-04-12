using System;
using Avalonia;
using AvaPlayer.Helpers;

namespace AvaPlayer;

sealed class Program
{
    internal static SingleInstanceManager? SingleInstance { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        using var singleInstance = SingleInstanceManager.Create("AvaPlayer");
        if (!singleInstance.IsPrimaryInstance)
        {
            Console.Error.WriteLine("[SingleInstance] 检测到 AvaPlayer 已在运行，尝试唤醒现有实例。");
            singleInstance.TrySignalPrimaryInstance();
            return;
        }

        SingleInstance = singleInstance;
        singleInstance.StartListening();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            SingleInstance = null;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
