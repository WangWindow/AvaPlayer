using System.ComponentModel;
using System.Runtime;
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
    private const string DarkTrayIconResourceUri = "avares://AvaPlayer/Resources/logo-tray-light.ico";
    private const string LightTrayIconResourceUri = "avares://AvaPlayer/Resources/logo-tray.ico";

    private ServiceProvider? _services;
    private IServiceScope? _runtimeScope;
    private ILogger<App>? _logger;
    private IDatabaseService? _databaseService;
    private MainWindowViewModel? _mainWindowViewModel;
    private MainWindow? _mainWindow;
    private NativeMenuItem? _lightweightModeMenuItem;
    private IMediaTransportService? _mediaTransportService;
    private IPlayerService? _playerService;
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
    private bool _isMediaTransportCommandsWired;
    private bool _isMediaTransportStateWired;
    private bool _isIdleRuntimeReleaseScheduled;
    private int _pendingTrayPlaybackOperations;

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
        services.AddSingleton<INetworkAccessService, NetworkAccessService>();
        services.AddSingleton<ITrackScannerService, TrackScannerService>();
        services.AddScoped<IPlaylistService, PlaylistService>();
        services.AddScoped<IPlayerService, MiniAudioPlayerService>();
        services.AddScoped<IAlbumArtService, AlbumArtService>();

        services.AddScoped<ILyricsProvider, LrcLibProvider>();
        services.AddScoped<ILyricsProvider, NetEaseProvider>();
        services.AddScoped<ILyricsProvider, LyricsOvhProvider>();
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

        services.AddScoped<PlayerBarViewModel>();
        services.AddScoped<PlaylistViewModel>();
        services.AddScoped<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }

    private async Task InitializeApplicationAsync()
    {
        if (_isLightweightModeEnabled && _mainWindowViewModel is null)
        {
            return;
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

        await SyncMediaTransportAsync();
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
        _playerService ??= runtimeServices.GetRequiredService<IPlayerService>();

        WirePlayerBarEvents();
        WireMediaTransport();
        return _mainWindowViewModel;
    }

    private async Task<MainWindowViewModel> EnsureRuntimeForTrayAsync()
    {
        await InitializeRuntimeAsync(hydrateVisuals: false);
        if (_mainWindowViewModel is null)
        {
            throw new InvalidOperationException("主窗口视图模型尚未初始化。");
        }

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
                return await _databaseService.GetSettingAsync(LightweightModeSettingKey);
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

        _mainWindowViewModel.PlayerBar.PropertyChanged += OnPlayerBarPropertyChanged;
        _mainWindowViewModel.PlayerBar.TrackChanged += OnTrackChanged;
        _isPlayerBarWired = true;
    }

    private void WireTrayIconTheme()
    {
        _darkTrayIcon = LoadWindowIcon(DarkTrayIconResourceUri);
        _lightTrayIcon = LoadWindowIcon(LightTrayIconResourceUri);
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
        UpdateTrayIconTheme();
    }

    private void WireMediaTransport()
    {
        if (_mediaTransportService is null)
        {
            return;
        }

        if (!_isMediaTransportCommandsWired)
        {
            _mediaTransportService.PlayRequested += OnMediaTransportPlayRequested;
            _mediaTransportService.PauseRequested += OnMediaTransportPauseRequested;
            _mediaTransportService.NextRequested += OnMediaTransportNextRequested;
            _mediaTransportService.PreviousRequested += OnMediaTransportPreviousRequested;
            _mediaTransportService.SeekRequested += OnMediaTransportSeekRequested;
            _isMediaTransportCommandsWired = true;
        }

        if (_mainWindowViewModel is null || _playerService is null || _isMediaTransportStateWired)
        {
            return;
        }

        _playerService.PlaybackStateChanged += OnPlaybackStateChanged;
        _playerService.PositionChanged += OnPlayerPositionChanged;
        _mediaTransportService.UpdatePlaybackMode(_mainWindowViewModel.PlayerBar.PlaybackMode);
        _isMediaTransportStateWired = true;
    }

    private void OnMediaTransportPlayRequested(object? sender, EventArgs e)
    {
        _ = HandleMediaTransportPlayRequestedAsync();
    }

    private void OnMediaTransportPauseRequested(object? sender, EventArgs e)
    {
        _ = HandleMediaTransportPauseRequestedAsync();
    }

    private void OnMediaTransportNextRequested(object? sender, EventArgs e)
    {
        _ = HandleMediaTransportNextRequestedAsync();
    }

    private void OnMediaTransportPreviousRequested(object? sender, EventArgs e)
    {
        _ = HandleMediaTransportPreviousRequestedAsync();
    }

    private async Task HandleMediaTransportPlayRequestedAsync()
    {
        await ExecuteTrayPlaybackCommandAsync(
            playerBar =>
            {
                playerBar.ResumeCommand.Execute(null);
                return Task.CompletedTask;
            },
            "媒体控制播放");
    }

    private async Task HandleMediaTransportPauseRequestedAsync()
    {
        await ExecuteTrayPlaybackCommandAsync(
            playerBar =>
            {
                playerBar.PauseCommand.Execute(null);
                return Task.CompletedTask;
            },
            "媒体控制暂停");
    }

    private async Task HandleMediaTransportNextRequestedAsync()
    {
        await ExecuteTrayPlaybackCommandAsync(
            playerBar => playerBar.NextCommand.ExecuteAsync(null),
            "媒体控制下一首");
    }

    private async Task HandleMediaTransportPreviousRequestedAsync()
    {
        await ExecuteTrayPlaybackCommandAsync(
            playerBar => playerBar.PreviousCommand.ExecuteAsync(null),
            "媒体控制上一首");
    }

    private void OnMediaTransportSeekRequested(object? sender, TimeSpan position)
    {
        _playerService?.Seek(position.TotalSeconds);
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

    private void OnPlayerBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_mediaTransportService is null || sender is not PlayerBarViewModel playerBar)
        {
            return;
        }

        if (e.PropertyName == nameof(PlayerBarViewModel.PlaybackMode))
        {
            _mediaTransportService.UpdatePlaybackMode(playerBar.PlaybackMode);
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

    private void OnPlaybackStateChanged(object? sender, bool isPlaying)
    {
        _mediaTransportService?.UpdatePlaybackState(isPlaying);
        if (!isPlaying && _isLightweightModeEnabled && _mainWindow is null)
        {
            ScheduleIdleRuntimeRelease();
        }
    }

    private void OnPlayerPositionChanged(object? sender, double position)
    {
        if (_mediaTransportService is null || _playerService is null)
        {
            return;
        }

        _mediaTransportService.UpdatePosition(
            TimeSpan.FromSeconds(position),
            TimeSpan.FromSeconds(Math.Max(0, _playerService.Duration)));
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
        await ExecuteTrayPlaybackCommandAsync(
            playerBar => playerBar.PreviousCommand.ExecuteAsync(null),
            "托盘上一首");
    }

    private async void OnPlayPauseClick(object? sender, EventArgs e)
    {
        await ExecuteTrayPlaybackCommandAsync(
            playerBar =>
            {
                playerBar.PlayPauseCommand.Execute(null);
                return Task.CompletedTask;
            },
            "托盘播放暂停");
    }

    private async void OnNextTrackClick(object? sender, EventArgs e)
    {
        await ExecuteTrayPlaybackCommandAsync(
            playerBar => playerBar.NextCommand.ExecuteAsync(null),
            "托盘下一首");
    }

    private async Task ExecuteTrayPlaybackCommandAsync(
        Func<PlayerBarViewModel, Task> command,
        string operationName)
    {
        Interlocked.Increment(ref _pendingTrayPlaybackOperations);

        try
        {
            var viewModel = await EnsureRuntimeForTrayAsync();
            var commandCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await command(viewModel.PlayerBar);
                    commandCompletion.SetResult(true);
                }
                catch (Exception ex)
                {
                    commandCompletion.SetException(ex);
                }
            });
            await commandCompletion.Task;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[App] {Operation}执行失败: {Message}", operationName, ex.Message);
        }
        finally
        {
            if (Interlocked.Decrement(ref _pendingTrayPlaybackOperations) == 0)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (Volatile.Read(ref _pendingTrayPlaybackOperations) == 0
                        && _isLightweightModeEnabled
                        && _mainWindow is null
                        && _playerService?.IsPlaying != true)
                    {
                        ScheduleIdleRuntimeRelease();
                    }
                });
            }
        }
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
                    await _databaseService.SaveSettingAsync(LightweightModeSettingKey, isEnabled.ToString());
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
            await TrimLightweightMemoryAsync("轻量模式无窗口");
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
        if (_mainWindowViewModel is null)
        {
            return;
        }

        await _mainWindowViewModel.PlayerBar.PersistSessionAsync();
    }

    private async Task SyncMediaTransportAsync()
    {
        if (_mainWindowViewModel is null || _mediaTransportService is null)
        {
            return;
        }

        await _mediaTransportService.UpdateTrackAsync(_mainWindowViewModel.PlayerBar.CurrentTrack);
        _mediaTransportService.UpdatePlaybackMode(_mainWindowViewModel.PlayerBar.PlaybackMode);

        if (_playerService is null)
        {
            return;
        }

        _mediaTransportService.UpdatePlaybackState(_playerService.IsPlaying);
        _mediaTransportService.UpdatePosition(
            TimeSpan.FromSeconds(Math.Max(0, _mainWindowViewModel.PlayerBar.Position)),
            TimeSpan.FromSeconds(Math.Max(0, _mainWindowViewModel.PlayerBar.Duration)));
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
            _ = TrimLightweightMemoryAsync("主窗口关闭后");
        }
    }

    private void DisposeRuntimeIfIdle()
    {
        if (_runtimeScope is null)
        {
            return;
        }

        if (Volatile.Read(ref _pendingTrayPlaybackOperations) > 0)
        {
            _isIdleRuntimeReleaseScheduled = false;
            return;
        }

        if (_playerService?.IsPlaying == true)
        {
            _isIdleRuntimeReleaseScheduled = false;

            // Release ViewModel visual state and event wiring, but keep player service alive
            if (_mainWindowViewModel is not null)
            {
                _mainWindowViewModel.PlayerBar.SuspendVisualHydration();
                _mainWindowViewModel.ReleaseWindowState();
                _mainWindowViewModel = null;
            }

            _logger?.LogInformation("[LightweightMode] 当前正在播放，保留播放运行时，已释放 UI 状态。");
            return;
        }

        UnwireRuntimeEvents();
        _runtimeScope.Dispose();
        _runtimeScope = null;
        _mainWindowViewModel = null;
        _playerService = null;
        _isNetworkInitialized = false;
        _isPlayerBarWired = false;
        _isMediaTransportStateWired = false;
        _isIdleRuntimeReleaseScheduled = false;
        _logger?.LogInformation("[LightweightMode] 已释放空闲播放/UI运行时。");
    }

    private void UnwirePlayerBarEvents()
    {
        if (_mainWindowViewModel is null)
            return;

        if (_isPlayerBarWired)
        {
            _mainWindowViewModel.PlayerBar.PropertyChanged -= OnPlayerBarPropertyChanged;
            _mainWindowViewModel.PlayerBar.TrackChanged -= OnTrackChanged;
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
        await TrimLightweightMemoryAsync("轻量模式播放停止后");
    }

    private void UnwireRuntimeEvents()
    {
        if (_mainWindowViewModel is not null && _isPlayerBarWired)
        {
            _mainWindowViewModel.PlayerBar.PropertyChanged -= OnPlayerBarPropertyChanged;
            _mainWindowViewModel.PlayerBar.TrackChanged -= OnTrackChanged;
        }

        if (_playerService is not null && _isMediaTransportStateWired)
        {
            _playerService.PlaybackStateChanged -= OnPlaybackStateChanged;
            _playerService.PositionChanged -= OnPlayerPositionChanged;
            _isMediaTransportStateWired = false;
        }
    }

    private void UnwireMediaTransportCommands()
    {
        if (_mediaTransportService is null || !_isMediaTransportCommandsWired)
        {
            return;
        }

        _mediaTransportService.PlayRequested -= OnMediaTransportPlayRequested;
        _mediaTransportService.PauseRequested -= OnMediaTransportPauseRequested;
        _mediaTransportService.NextRequested -= OnMediaTransportNextRequested;
        _mediaTransportService.PreviousRequested -= OnMediaTransportPreviousRequested;
        _mediaTransportService.SeekRequested -= OnMediaTransportSeekRequested;
        _isMediaTransportCommandsWired = false;
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

        ActualThemeVariantChanged -= OnActualThemeVariantChanged;

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

    private static async Task TrimLightweightMemoryAsync(string reason)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private void SyncLightweightModeMenuState()
    {
        if (_lightweightModeMenuItem is not null)
        {
            _lightweightModeMenuItem.IsChecked = _isLightweightModeEnabled;
        }
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e) => UpdateTrayIconTheme();

    private void UpdateTrayIconTheme()
    {
        var trayIcons = TrayIcon.GetIcons(this);
        if (trayIcons is null)
        {
            return;
        }

        var selectedIcon = ActualThemeVariant == ThemeVariant.Light
            ? _lightTrayIcon ?? _darkTrayIcon
            : _darkTrayIcon ?? _lightTrayIcon;

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
