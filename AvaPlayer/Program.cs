using Avalonia;
using Avalonia.Media;
using Avalonia.OpenGL;
using AvaPlayer.Helpers;

namespace AvaPlayer;

internal sealed class Program
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
            // Fluent2's default family is the composite "fonts:Inter#Inter, $Default"; $Default
            // follows the platform default family. Skia takes that from fontconfig, and on
            // systems that register legacy bitmap fonts (e.g. Wine's WinFonts, where
            // "system-ui" can resolve to a .fon face) it has no SFNT tables at all. Avalonia
            // 12.2 reads the table directory of every matched face and throws for those,
            // aborting the first layout that needs a fallback glyph. Pin $Default to the
            // embedded Inter collection so fallback never starts from such a system face.
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "fonts:Inter#Inter",
            });

#if WINDOWS
        // Avalonia 12.2: opt in to the DWM composition swap chain. This lets
        // the Windows backend use independent flips, reducing latency and
        // making composition smoother.
        builder = builder.With(new Win32PlatformOptions
        {
            UseCompositionSwapChain = true,
        });
#endif

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
                // Avalonia 12.2: when X11 (not Wayland) ends up being used and
                // the app renders in software, pass frames to the X server via
                // shared memory instead of copying them, improving frame rate.
                .With(new X11PlatformOptions
                {
                    UseXShmFramebuffer = true,
                })
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
                    // Start from GL profiles every relevant Wayland driver can
                    // actually create. Avalonia picks the first entry whose EGL
                    // *config* exists and does not fall back when
                    // eglCreateContext later fails, so the default GL 4.0 first
                    // entry leaves panthor (RK3588 Mali-G610: GL 3.1 / GLES 3.1
                    // only) stuck in a failing render loop with no window.
                    GlProfiles = new List<GlVersion>
                    {
                        new GlVersion(GlProfileType.OpenGL, 3, 0),
                        new GlVersion(GlProfileType.OpenGLES, 3, 0),
                        new GlVersion(GlProfileType.OpenGLES, 2, 0),
                    },
                    // UseDmabufSwapchain intentionally left null: the backend
                    // decides from compositor + driver capabilities; hard-coding
                    // true is the least safe choice on e.g. NVIDIA stacks.
                    // WlDisplayName left null so the WAYLAND_DISPLAY environment
                    // variable is honoured. ForceDrawnDecorations is an
                    // [Experimental] testing knob (forces CSD, requires
                    // suppressing the AVALONIA_WAYLAND_FORCE_CSD compiler
                    // diagnostic) - not set; use normal platform decorations.
                });
        }
        // Tray caveat: on Wayland the tray is exported over DBus as a
        // StatusNotifierItem/AppIndicator. App.axaml therefore uses PNG assets
        // rather than Windows-only .ico resources. A desktop still needs a
        // StatusNotifier host (for example an AppIndicator/Wayland tray
        // extension) to display the item; without one no application can make a
        // tray icon visible.
#endif

#if DEBUG
        builder = builder.WithDeveloperTools();
#endif
        return builder.LogToTrace();
    }

}
