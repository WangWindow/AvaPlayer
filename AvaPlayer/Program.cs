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
    {
        // UsePlatformDetect() must stay: it registers the base platform services
        // (Skia rendering, HarfBuzz text shaping, fonts, ...) plus the default
        // windowing backend (X11 on Linux). Do NOT hand-roll UseSkia() +
        // UseHarfBuzz() to "avoid X11": that would silently drop text-shaping
        // and other core services.
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();

#if LINUX_WAYLAND
        // Linux: prefer the native Wayland backend, but keep X11/XWayland working.
        // UseWaylandWithFallback() uses Wayland when a usable compositor is
        // available and falls back to the previously configured backend (X11,
        // from UsePlatformDetect) otherwise - the documented "call it after
        // UsePlatformDetect" pattern.
        // UseWayland() (no fallback) is deliberately NOT used: the Wayland
        // backend is still marked experimental by Avalonia and X11 remains
        // Avalonia's default on Linux, so keeping the fallback avoids breaking
        // X11-only sessions and XWayland.
        // LINUX_WAYLAND is defined by the *build machine* OS (AvaPlayer.csproj),
        // not by the runtime OS; OperatingSystem.IsLinux() is an AOT-safe
        // intrinsic guarding a Linux-built assembly executed elsewhere.
        if (OperatingSystem.IsLinux())
        {
            builder = builder
                .UseWaylandWithFallback()
                .With(new WaylandPlatformOptions
                {
                    // Reconnect automatically when the compositor connection is
                    // lost. Matches the backend default; written out to document
                    // intent (auto-disabled if DisplayFd were set - it isn't).
                    EnableReconnects = true,
                    // Keep the default managed dispatcher: all DBus work (MPRIS,
                    // tray) goes through managed Tmds.DBus.Protocol, so a GLib
                    // main loop is not needed. Matches the backend default.
                    UseGLibMainLoop = false,
                    // UseDmabufSwapchain intentionally left null: the backend
                    // decides from compositor + driver capabilities; hard-coding
                    // true is the least safe choice on e.g. NVIDIA stacks.
                    // GlProfiles left at the default probe order (OpenGL 4.0
                    // down to OpenGL ES 2.0); WlDisplayName left null so the
                    // WAYLAND_DISPLAY environment variable is honoured.
                    // ForceDrawnDecorations is an [Experimental] testing knob
                    // (forces CSD, requires suppressing the
                    // AVALONIA_WAYLAND_FORCE_CSD compiler diagnostic) - not set;
                    // the main window already draws its own title bar via
                    // ExtendClientAreaToDecorationsHint.
                });
        }
        // Tray caveat: on Wayland the tray is exported over DBus
        // StatusNotifierItem/AppIndicator, which generally expects PNG pixmaps
        // (or icon-theme names), while App.axaml feeds it .ico - the tray icon
        // may render poorly or be dropped on some Wayland desktops.
        // On X11 Avalonia uses XEmbed, which handles .ico fine.
        // Follow-up (out of scope here): ship a 22-32 px PNG.
#endif

#if DEBUG
        builder = builder.WithDeveloperTools();
#endif
        return builder.LogToTrace();
    }

}
