using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaPlayer.Helpers;
using AvaPlayer.Services.AlbumArt;
using AvaPlayer.Services.Audio;
using AvaPlayer.Services.Cache;
using AvaPlayer.Services.Database;
using AvaPlayer.Services.Lyrics;
using AvaPlayer.Services.MediaTransport;
using AvaPlayer.Services.PlaybackSession;
using AvaPlayer.Services.Settings;
using AvaPlayer.Services.Network;
using AvaPlayer.Services.Playlist;
using AvaPlayer.ViewModels;
using AvaPlayer.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AvaPlayer;

public partial class App : Application
{
    private const string LightweightModeSettingKey = "lightweight-mode-enabled";
    private const string TrayIconStyleSettingKey = "tray-icon-style";
    private const string DarkTrayIconResourceUri = "avares://AvaPlayer/Resources/logo-tray.ico";
    private const string LightTrayIconResourceUri = "avares://AvaPlayer/Resources/logo-tray-light.ico";

    private ServiceProvider? _services;
    private IServiceScope? _runtimeScope;
    private ILogger<App>? _logger;
    private IDatabaseService? _databaseService;
    private ISettingsService? _settings;
    private MainWindowViewModel? _mainWindowViewModel;
    private MainWindow? _mainWindow;
    private NativeMenuItem? _lightweightModeMenuItem;
    private IMediaTransportService? _mediaTransportService;
    private SingleInstanceManager? _singleInstanceManager;
    private WindowIcon? _darkTrayIcon;
    private WindowIcon? _lightTrayIcon;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private bool _isExiting;
    private bool _isLightweightModeEnabled;
    private bool _isApplyingLightweightMode;
    private bool _isReleasingMainWindow;
    private bool _isNetworkInitialized;
    private bool _isMediaTransportInitialized;
    private bool _isPlayerBarWired;
    private bool _isIdleRuntimeReleaseScheduled;
    private IDisposable? _mediaTransportSnapshotSubscription;
    private IPlaybackSessionClient? _playbackSession;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _services = ConfigureServices();
            _logger = _services.GetRequiredService<ILogger<App>>();
            _databaseService = _services.GetRequiredService<IDatabaseService>();
            _settings = _services.GetRequiredService<ISettingsService>();
            _playbackSession = _services.GetRequiredService<IPlaybackSessionClient>();
            _singleInstanceManager = Program.SingleInstance;

            _isLightweightModeEnabled = LoadLightweightModeSetting();
            if (!_isLightweightModeEnabled)
            {
                _ = EnsureRuntimeServices();
                EnsureMainWindow();
            }

            desktop.Exit += OnDesktopExit;
            desktop.ShutdownRequested += OnDesktopShutdownRequested;

            WireTrayMenu();
            WireTrayIconTheme();
            WireMediaTransport();
            WireSingleInstanceActivation();

            _ = InitializeApplicationAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());

        services.AddHttpClient();

        services.AddSingleton<ICacheService, CacheService>();
        services.AddSingleton<IDatabaseService, SqliteDatabaseService>();
        services.AddSingleton<ISettingsService, AppSettingsService>();
        services.AddSingleton<INetworkAccessService, NetworkAccessService>();
        services.AddSingleton<ITrackScannerService, TrackScannerService>();
        services.AddSingleton<IPlayerService, MiniAudioPlayerService>();
        services.AddSingleton<IPlaylistService, PlaylistService>();
        services.AddScoped<IAlbumArtProviderManager, AlbumArtProviderManager>();
        services.AddScoped<IAlbumArtService, AlbumArtService>();

        services.AddScoped<ILyricsProviderManager, LyricsProviderManager>();
        services.AddScoped<ILyricsService, LyricsService>();

        if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<IMediaTransportService, MprisMediaTransportService>();
        }
        else if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IMediaTransportService, SmtcMediaTransportService>();
        }
        else
        {
            services.AddSingleton<IMediaTransportService, NoopMediaTransportService>();
        }

        services.AddSingleton<IPlaybackPositionMemoryService, PlaybackPositionMemoryService>();
        services.AddSingleton<IPlaybackSessionClient, PlaybackSession>();
        services.AddScoped<ILyricPreferencesService, LyricsPreferenceService>();
        services.AddScoped<LyricsViewModel>();
        services.AddScoped<ILyricPresentationService>(sp => sp.GetRequiredService<LyricsViewModel>());
        services.AddScoped<SettingsViewModel>();
        services.AddScoped<PlayerBarViewModel>();
        services.AddScoped<PlaylistViewModel>();
        services.AddScoped<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }

    private async Task InitializeApplicationAsync()
    {
        if (_isLightweightModeEnabled)
        {
            _logger?.LogInformation("[App] 轻量模式启动，初始化运行时。");
        }

        await InitializeRuntimeAsync(hydrateVisuals: !_isLightweightModeEnabled);
    }

    private async Task InitializeRuntimeAsync(bool hydrateVisuals)
    {
        EnsureRuntimeServices();

        try
        {
            if (!_isNetworkInitialized && _services?.GetService<INetworkAccessService>() is { } networkService)
            {
                await networkService.InitializeAsync();
                _isNetworkInitialized = true;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[App] 初始化网络访问服务失败: {Message}", ex.Message);
        }

        try
        {
            if (!_isMediaTransportInitialized && _mediaTransportService is not null)
            {
                await _mediaTransportService.InitializeAsync();
                _isMediaTransportInitialized = true;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[App] 初始化系统媒体控制失败: {Message}", ex.Message);
        }

        try
        {
            if (_mainWindowViewModel is not null)
            {
                await _mainWindowViewModel.InitializeAsync(hydrateVisuals);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[App] 初始化主界面失败: {Message}", ex.Message);
            return;
        }

    }

    private MainWindowViewModel EnsureRuntimeServices()
    {
        if (_services is null)
        {
            throw new InvalidOperationException("应用服务尚未初始化。");
        }

        _runtimeScope ??= _services.CreateScope();
        var runtimeServices = _runtimeScope.ServiceProvider;
        _mainWindowViewModel ??= runtimeServices.GetRequiredService<MainWindowViewModel>();
        _mediaTransportService ??= runtimeServices.GetRequiredService<IMediaTransportService>();

        WirePlayerBarEvents();
        WireMediaTransport();
        return _mainWindowViewModel;
    }

    private bool LoadLightweightModeSetting()
    {
        if (_databaseService is null)
        {
            return false;
        }

        try
        {
            var setting = Task.Run(async () =>
            {
                await _databaseService.InitializeAsync();
                return await _settings!.GetAsync(LightweightModeSettingKey);
            }).GetAwaiter().GetResult();
            return bool.TryParse(setting, out var isEnabled) && isEnabled;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[App] 读取轻量模式设置失败: {Message}", ex.Message);
            return false;
        }
    }

    private MainWindow EnsureMainWindow()
    {
        if (_mainWindow is not null)
        {
            return _mainWindow;
        }

        if (_mainWindowViewModel is null)
        {
            throw new InvalidOperationException("主窗口视图模型尚未初始化。");
        }

        _mainWindow = new MainWindow
        {
            DataContext = _mainWindowViewModel
        };
        _mainWindow.Closing += OnMainWindowClosing;
        _mainWindow.Closed += OnMainWindowClosed;

        if (_desktop is not null)
        {
            _desktop.MainWindow = _mainWindow;
        }

        return _mainWindow;
    }

    private void WireTrayMenu()
    {
        _lightweightModeMenuItem = TrayIcon.GetIcons(this)?
            .Select(icon => icon.Menu)
            .OfType<NativeMenu>()
            .SelectMany(menu => menu.Items.OfType<NativeMenuItem>())
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "轻量模式", StringComparison.Ordinal));

        WirePlayerBarEvents();
        SyncLightweightModeMenuState();
    }

    private void WirePlayerBarEvents()
    {
        if (_isPlayerBarWired || _mainWindowViewModel is null)
        {
            return;
        }

        _mainWindowViewModel.PlayerBar.TrackChanged += OnTrackChanged;
        _mainWindowViewModel.Settings.PropertyChanged += OnSettingsPropertyChanged;
        _isPlayerBarWired = true;
    }

    private void WireTrayIconTheme()
    {
        _darkTrayIcon = LoadWindowIcon(DarkTrayIconResourceUri);
        _lightTrayIcon = LoadWindowIcon(LightTrayIconResourceUri);

        // Read persisted tray icon style from DB (initialized earlier by
        // LoadLightweightModeSetting). This ensures the tray icon respects
        // the user's saved preference even when starting in lightweight
        // (tray-only) mode, before the ViewModel is ever created.
        var style = "dark";
        try
        {
            if (_settings is not null)
            {
                var saved = Task.Run(async () =>
                    await _settings.GetAsync(TrayIconStyleSettingKey)
                ).GetAwaiter().GetResult();
                if (saved is "light" or "dark")
                    style = saved;
            }
        }
        catch
        {
            // Default to dark on any error
        }

        ApplyTrayIconStyle(style);
    }

    private void WireMediaTransport()
    {
        if (_mediaTransportService is null)
        {
            return;
        }

        if (_mediaTransportSnapshotSubscription is not null)
        {
            return;
        }

        _mediaTransportService.PlayRequested += OnMediaTransportPlayRequested;
        _mediaTransportService.PauseRequested += OnMediaTransportPauseRequested;
        _mediaTransportService.NextRequested += OnMediaTransportNextRequested;
        _mediaTransportService.PreviousRequested += OnMediaTransportPreviousRequested;
        _mediaTransportService.SeekRequested += OnMediaTransportSeekRequested;

        _mediaTransportSnapshotSubscription = _playbackSession?.Subscribe(OnMediaTransportSnapshot);
    }

    private async void OnMediaTransportPlayRequested(object? sender, EventArgs e)
    {
        try { await (_playbackSession?.ResumeAsync() ?? Task.CompletedTask); }
        catch (Exception ex) { _logger?.LogError(ex, "[App] 媒体控制播放失败"); }
    }

    private async void OnMediaTransportPauseRequested(object? sender, EventArgs e)
    {
        try { await (_playbackSession?.PauseAsync() ?? Task.CompletedTask); }
        catch (Exception ex) { _logger?.LogError(ex, "[App] 媒体控制暂停失败"); }
    }

    private async void OnMediaTransportNextRequested(object? sender, EventArgs e)
    {
        try { await (_playbackSession?.NextAsync() ?? Task.CompletedTask); }
        catch (Exception ex) { _logger?.LogError(ex, "[App] 媒体控制下一首失败"); }
    }

    private async void OnMediaTransportPreviousRequested(object? sender, EventArgs e)
    {
        try { await (_playbackSession?.PreviousAsync() ?? Task.CompletedTask); }
        catch (Exception ex) { _logger?.LogError(ex, "[App] 媒体控制上一首失败"); }
    }

    private async void OnMediaTransportSeekRequested(object? sender, TimeSpan position)
    {
        try { await (_playbackSession?.SeekAsync(position.TotalSeconds) ?? Task.CompletedTask); }
        catch (Exception ex) { _logger?.LogError(ex, "[App] 媒体控制跳转失败"); }
    }

    private void OnMediaTransportSnapshot(PlaybackSnapshot snapshot)
    {
        if (_mediaTransportService is null)
        {
            return;
        }

        _ = _mediaTransportService.UpdateTrackAsync(snapshot.CurrentTrack);
        _mediaTransportService.UpdatePlaybackState(snapshot.IsPlaying);
        _mediaTransportService.UpdatePosition(
            TimeSpan.FromSeconds(Math.Max(0, snapshot.Position)),
            TimeSpan.FromSeconds(Math.Max(0, snapshot.Duration)));
        _mediaTransportService.UpdatePlaybackMode(snapshot.PlaybackMode);

        // A natural track end is reported by the audio service as a transient
        // non-playing state before TrackEnded enqueues the auto-advance command.
        // Releasing the lightweight runtime on that transient state can race
        // with auto-advance and leave the player stuck until the tray Play
        // command reactivates it. Only release after the session has settled
        // into the terminal Stopped state (end of queue or failed transition).
        if (snapshot.Status == PlaybackStatus.Stopped
            && snapshot.HasTrack
            && _isLightweightModeEnabled && _mainWindow is null)
        {
            ScheduleIdleRuntimeRelease();
        }

    }

    private void WireSingleInstanceActivation()
    {
        if (_singleInstanceManager is null)
        {
            return;
        }

        _singleInstanceManager.ActivationRequested += OnSingleInstanceActivationRequested;

        var pendingRequests = _singleInstanceManager.ConsumePendingActivationRequests();
        for (var index = 0; index < pendingRequests; index++)
        {
            Dispatcher.UIThread.Post(ShowMainWindow);
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.TrayIconStyle)
            && sender is SettingsViewModel settings)
        {
            Dispatcher.UIThread.Post(() => ApplyTrayIconStyle(settings.TrayIconStyle));
        }
    }

    private void OnTrackChanged(object? sender, Models.Track? track)
    {
        if (_mediaTransportService is null)
        {
            return;
        }

        _ = _mediaTransportService.UpdateTrackAsync(track);
    }

    private void OnTrayIconClick(object? sender, EventArgs e) => ShowMainWindow();

    private void OnShowWindowClick(object? sender, EventArgs e) => ShowMainWindow();

    private async void OnLightweightModeClick(object? sender, EventArgs e)
    {
        if (sender is NativeMenuItem menuItem)
        {
            _lightweightModeMenuItem ??= menuItem;
        }

        await SetLightweightModeEnabledAsync(!_isLightweightModeEnabled);
    }

    private async void OnPreviousTrackClick(object? sender, EventArgs e)
    {
        try { await (_playbackSession?.PreviousAsync() ?? Task.CompletedTask); }
        catch (Exception ex) { _logger?.LogError(ex, "[App] 托盘上一首失败"); }
    }

    private async void OnPlayPauseClick(object? sender, EventArgs e)
    {
        try { await (_playbackSession?.TogglePlayPauseAsync() ?? Task.CompletedTask); }
        catch (Exception ex) { _logger?.LogError(ex, "[App] 托盘播放暂停失败"); }
    }

    private async void OnNextTrackClick(object? sender, EventArgs e)
    {
        try { await (_playbackSession?.NextAsync() ?? Task.CompletedTask); }
        catch (Exception ex) { _logger?.LogError(ex, "[App] 托盘下一首失败"); }
    }



    private async void OnExitClick(object? sender, EventArgs e)
    {
        _isExiting = true;

        await PersistPlaybackSessionAsync();
        _desktop?.Shutdown();
    }

    private async void ShowMainWindow() => await ShowMainWindowAsync();

    private async Task ShowMainWindowAsync()
    {
        try
        {
            await InitializeRuntimeAsync(hydrateVisuals: true);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[App] 恢复主窗口视觉状态失败: {Message}", ex.Message);
            return;
        }

        var mainWindow = EnsureMainWindow();

        if (!mainWindow.IsVisible)
        {
            mainWindow.Show();
        }

        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }

    private void OnSingleInstanceActivationRequested(object? sender, EventArgs e)
    {
        _logger?.LogInformation("[SingleInstance] 收到新的启动请求，激活现有窗口。");
        Dispatcher.UIThread.Post(ShowMainWindow);
    }

    private async Task SetLightweightModeEnabledAsync(bool isEnabled)
    {
        if (_isApplyingLightweightMode)
        {
            return;
        }

        var previousState = _isLightweightModeEnabled;
        if (previousState == isEnabled)
        {
            SyncLightweightModeMenuState();
            return;
        }

        _isApplyingLightweightMode = true;
        _isLightweightModeEnabled = isEnabled;
        SyncLightweightModeMenuState();

        if (isEnabled)
        {
            _logger?.LogInformation("[LightweightMode] 切换到轻量模式。");
        }
        else
        {
            _logger?.LogInformation("[LightweightMode] 切换到正常模式。");
        }

        try
        {
            if (_databaseService is not null)
            {
                try
                {
                    await _settings!.SetAsync(LightweightModeSettingKey, isEnabled.ToString());
                }
                catch (Exception ex)
                {
                    _isLightweightModeEnabled = previousState;
                    SyncLightweightModeMenuState();
                    _logger?.LogError(ex, "[LightweightMode] 保存设置失败: {Message}", ex.Message);
                    return;
                }
            }

            if (isEnabled)
            {
                await EnterLightweightModeAsync();
            }
            else
            {
                await ExitLightweightModeAsync();
            }
        }
        finally
        {
            _isApplyingLightweightMode = false;
        }
    }

    private async Task EnterLightweightModeAsync()
    {
        _logger?.LogInformation("[LightweightMode] 正在释放主窗口并保留托盘。");

        if (_mainWindow is null)
        {
            await PersistPlaybackSessionAsync();
            _mainWindowViewModel?.ReleaseWindowState();
            DisposeRuntimeIfIdle();
            return;
        }

        await CloseMainWindowAsync();
    }

    private async Task ExitLightweightModeAsync()
    {
        _logger?.LogInformation("[LightweightMode] 正在恢复主窗口。");
        await ShowMainWindowAsync();
    }

    private async Task CloseMainWindowAsync()
    {
        if (_mainWindow is null)
        {
            return;
        }

        await PersistPlaybackSessionAsync();

        _isReleasingMainWindow = true;
        try
        {
            _mainWindow.Close();
        }
        finally
        {
            _isReleasingMainWindow = false;
        }
    }

    private async Task PersistPlaybackSessionAsync()
    {
        if (_playbackSession is null) return;
        await _playbackSession.PersistAsync();
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isExiting || _isReleasingMainWindow)
        {
            return;
        }

        if (_isLightweightModeEnabled)
        {
            _ = PersistPlaybackSessionAsync();
            return;
        }

        e.Cancel = true;

        if (sender is Window window)
        {
            window.Hide();
        }

        _ = PersistPlaybackSessionAsync();
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window)
        {
            return;
        }

        window.Closing -= OnMainWindowClosing;
        window.Closed -= OnMainWindowClosed;
        window.DataContext = null;
        window.Content = null;

        if (ReferenceEquals(_mainWindow, window))
        {
            _mainWindow = null;
        }

        if (_desktop is not null && ReferenceEquals(_desktop.MainWindow, window))
        {
            _desktop.MainWindow = null;
        }

        if (_isLightweightModeEnabled || _isReleasingMainWindow)
        {
            _mainWindowViewModel?.ReleaseWindowState();
            DisposeRuntimeIfIdle();
        }
    }

    private void DisposeRuntimeIfIdle()
    {
        if (_runtimeScope is null)
            return;

        _isIdleRuntimeReleaseScheduled = false;

        // Keep scope alive while playing so PlaybackSession can handle
        // auto-advance and media transport projection.
        if (_playbackSession?.LatestSnapshot.IsPlaying == true)
        {
            if (_mainWindowViewModel is not null)
            {
                _mainWindowViewModel.PlayerBar.SuspendVisualHydration();
                _mainWindowViewModel.ReleaseWindowState();
                _mainWindowViewModel = null;
            }
            return;
        }

        UnwireRuntimeEvents();
        _runtimeScope.Dispose();
        _runtimeScope = null;
        _mainWindowViewModel = null;
        _isNetworkInitialized = false;
        _isPlayerBarWired = false;
    }

    private void UnwirePlayerBarEvents()
    {
        if (_mainWindowViewModel is null)
            return;

        if (_isPlayerBarWired)
        {
            _mainWindowViewModel.PlayerBar.TrackChanged -= OnTrackChanged;
            _mainWindowViewModel.Settings.PropertyChanged -= OnSettingsPropertyChanged;
            _isPlayerBarWired = false;
        }
    }

    private void ScheduleIdleRuntimeRelease()
    {
        if (_isIdleRuntimeReleaseScheduled)
        {
            return;
        }

        _isIdleRuntimeReleaseScheduled = true;
        _ = ReleaseIdleRuntimeAsync();
    }

    private async Task ReleaseIdleRuntimeAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (!_isLightweightModeEnabled || _mainWindow is not null)
        {
            _isIdleRuntimeReleaseScheduled = false;
            return;
        }

        await PersistPlaybackSessionAsync();
        _mainWindowViewModel?.ReleaseWindowState();
        DisposeRuntimeIfIdle();
    }

    private void UnwireRuntimeEvents()
    {
        UnwirePlayerBarEvents();
    }

    private void UnwireMediaTransportCommands()
    {
        if (_mediaTransportService is null)
        {
            return;
        }

        _mediaTransportService.PlayRequested -= OnMediaTransportPlayRequested;
        _mediaTransportService.PauseRequested -= OnMediaTransportPauseRequested;
        _mediaTransportService.NextRequested -= OnMediaTransportNextRequested;
        _mediaTransportService.PreviousRequested -= OnMediaTransportPreviousRequested;
        _mediaTransportService.SeekRequested -= OnMediaTransportSeekRequested;
        _mediaTransportSnapshotSubscription?.Dispose();
        _mediaTransportSnapshotSubscription = null;
    }

    private void OnDesktopShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        _isExiting = true;
        Task.Run(PersistPlaybackSessionAsync).GetAwaiter().GetResult();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_desktop is not null)
        {
            _desktop.Exit -= OnDesktopExit;
        }

        if (_singleInstanceManager is not null)
        {
            _singleInstanceManager.ActivationRequested -= OnSingleInstanceActivationRequested;
        }

        UnwireRuntimeEvents();
        UnwireMediaTransportCommands();

        if (_desktop is not null)
        {
            _desktop.ShutdownRequested -= OnDesktopShutdownRequested;
        }

        if (_mainWindow is not null)
        {
            _mainWindow.Closing -= OnMainWindowClosing;
            _mainWindow.Closed -= OnMainWindowClosed;
        }

        _runtimeScope?.Dispose();
        _services?.Dispose();
    }

    private void SyncLightweightModeMenuState()
    {
        if (_lightweightModeMenuItem is not null)
        {
            _lightweightModeMenuItem.IsChecked = _isLightweightModeEnabled;
        }
    }

    private void ApplyTrayIconStyle(string style)
    {
        var trayIcons = TrayIcon.GetIcons(this);
        if (trayIcons is null)
        {
            return;
        }

        var selectedIcon = style == "light" ? _lightTrayIcon : _darkTrayIcon;
        if (selectedIcon is null)
        {
            return;
        }

        foreach (var trayIcon in trayIcons)
        {
            trayIcon.Icon = selectedIcon;
        }
    }

    private static WindowIcon LoadWindowIcon(string resourceUri)
    {
        using var iconStream = AssetLoader.Open(new Uri(resourceUri));
        return new WindowIcon(iconStream);
    }
}
