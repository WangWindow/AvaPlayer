using System.ComponentModel;
using System.Runtime;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AvaPlayer.Services.AlbumArt;
using AvaPlayer.Services.Audio;
using AvaPlayer.Services.Cache;
using AvaPlayer.Services.Database;
using AvaPlayer.Services.Lyrics;
using AvaPlayer.Services.MediaTransport;
using AvaPlayer.Services.Playlist;
using AvaPlayer.ViewModels;
using AvaPlayer.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AvaPlayer;

public partial class App : Application
{
    private const string LightweightModeSettingKey = "lightweight-mode-enabled";

    private ServiceProvider? _services;
    private IDatabaseService? _databaseService;
    private MainWindowViewModel? _mainWindowViewModel;
    private MainWindow? _mainWindow;
    private NativeMenuItem? _lightweightModeMenuItem;
    private IMediaTransportService? _mediaTransportService;
    private IPlayerService? _playerService;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private bool _isExiting;
    private bool _isLightweightModeEnabled;
    private bool _isReleasingMainWindow;

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
            _databaseService = _services.GetRequiredService<IDatabaseService>();
            _mainWindowViewModel = _services.GetRequiredService<MainWindowViewModel>();
            _mediaTransportService = _services.GetRequiredService<IMediaTransportService>();
            _playerService = _services.GetRequiredService<IPlayerService>();

            _isLightweightModeEnabled = LoadLightweightModeSetting();
            if (!_isLightweightModeEnabled)
            {
                EnsureMainWindow();
            }

            desktop.Exit += OnDesktopExit;
            desktop.ShutdownRequested += OnDesktopShutdownRequested;

            WireTrayMenu();
            WireMediaTransport();

            _ = InitializeApplicationAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddHttpClient();

        services.AddSingleton<ICacheService, CacheService>();
        services.AddSingleton<IDatabaseService, SqliteDatabaseService>();
        services.AddSingleton<ITrackScannerService, TrackScannerService>();
        services.AddSingleton<IPlaylistService, PlaylistService>();
        services.AddSingleton<IPlayerService, MpvPlayerService>();
        services.AddSingleton<IAlbumArtService, AlbumArtService>();

        services.AddSingleton<ILyricsProvider, LrcLibProvider>();
        services.AddSingleton<ILyricsProvider, NetEaseProvider>();
        services.AddSingleton<ILyricsProvider, LyricsOvhProvider>();
        services.AddSingleton<ILyricsService, LyricsService>();

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

        services.AddSingleton<PlayerBarViewModel>();
        services.AddSingleton<PlaylistViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }

    private async Task InitializeApplicationAsync()
    {
        if (_mainWindowViewModel is null || _mediaTransportService is null)
        {
            return;
        }

        try
        {
            await _mediaTransportService.InitializeAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[App] 初始化系统媒体控制失败: {ex.Message}");
        }

        try
        {
            await _mainWindowViewModel.InitializeAsync(hydrateVisuals: !_isLightweightModeEnabled);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[App] 初始化主界面失败: {ex.Message}");
            return;
        }

        await SyncMediaTransportAsync();
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
            Console.Error.WriteLine($"[App] 读取轻量模式设置失败: {ex.Message}");
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
        if (_mainWindowViewModel is null)
        {
            return;
        }

        _mainWindowViewModel.PlayerBar.PropertyChanged += OnPlayerBarPropertyChanged;
        _lightweightModeMenuItem = TrayIcon.GetIcons(this)?
            .Select(icon => icon.Menu)
            .OfType<NativeMenu>()
            .SelectMany(menu => menu.Items.OfType<NativeMenuItem>())
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "轻量模式", StringComparison.Ordinal));

        if (_lightweightModeMenuItem is not null)
        {
            _lightweightModeMenuItem.IsChecked = _isLightweightModeEnabled;
        }
    }

    private void WireMediaTransport()
    {
        if (_mainWindowViewModel is null || _mediaTransportService is null || _playerService is null)
        {
            return;
        }

        _mainWindowViewModel.PlayerBar.TrackChanged += OnTrackChanged;

        _mediaTransportService.PlayRequested += (_, _) =>
            Dispatcher.UIThread.Post(() => _mainWindowViewModel.PlayerBar.ResumeCommand.Execute(null));
        _mediaTransportService.PauseRequested += (_, _) =>
            Dispatcher.UIThread.Post(() => _mainWindowViewModel.PlayerBar.PauseCommand.Execute(null));
        _mediaTransportService.NextRequested += (_, _) =>
            Dispatcher.UIThread.Post(async () => await _mainWindowViewModel.PlayerBar.NextCommand.ExecuteAsync(null));
        _mediaTransportService.PreviousRequested += (_, _) =>
            Dispatcher.UIThread.Post(async () => await _mainWindowViewModel.PlayerBar.PreviousCommand.ExecuteAsync(null));
        _mediaTransportService.SeekRequested += (_, position) =>
            Dispatcher.UIThread.Post(() => _playerService.Seek(position.TotalSeconds));

        _playerService.PlaybackStateChanged += OnPlaybackStateChanged;
        _playerService.PositionChanged += OnPlayerPositionChanged;
        _mediaTransportService.UpdatePlaybackMode(_mainWindowViewModel.PlayerBar.PlaybackMode);
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
        if (sender is not NativeMenuItem menuItem)
        {
            return;
        }

        _lightweightModeMenuItem ??= menuItem;
        await SetLightweightModeEnabledAsync(menuItem.IsChecked);
    }

    private void OnPreviousTrackClick(object? sender, EventArgs e)
    {
        if (_mainWindowViewModel is null)
        {
            return;
        }

        _ = _mainWindowViewModel.PlayerBar.PreviousCommand.ExecuteAsync(null);
    }

    private void OnPlayPauseClick(object? sender, EventArgs e)
    {
        _mainWindowViewModel?.PlayerBar.PlayPauseCommand.Execute(null);
    }

    private void OnNextTrackClick(object? sender, EventArgs e)
    {
        if (_mainWindowViewModel is null)
        {
            return;
        }

        _ = _mainWindowViewModel.PlayerBar.NextCommand.ExecuteAsync(null);
    }

    private async void OnExitClick(object? sender, EventArgs e)
    {
        _isExiting = true;
        await PersistPlaybackSessionAsync();
        _desktop?.Shutdown();
    }

    private async void ShowMainWindow()
    {
        if (_mainWindowViewModel is null)
        {
            return;
        }

        try
        {
            await _mainWindowViewModel.EnsureWindowStateAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[App] 恢复主窗口视觉状态失败: {ex.Message}");
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

    private async Task SetLightweightModeEnabledAsync(bool isEnabled)
    {
        var previousState = _isLightweightModeEnabled;
        _isLightweightModeEnabled = isEnabled;
        if (_lightweightModeMenuItem is not null)
        {
            _lightweightModeMenuItem.IsChecked = isEnabled;
        }

        if (_databaseService is not null)
        {
            try
            {
                await _databaseService.SaveSettingAsync(LightweightModeSettingKey, isEnabled.ToString());
            }
            catch (Exception ex)
            {
                _isLightweightModeEnabled = previousState;
                if (_lightweightModeMenuItem is not null)
                {
                    _lightweightModeMenuItem.IsChecked = previousState;
                }
                Console.Error.WriteLine($"[App] 保存轻量模式设置失败: {ex.Message}");
                return;
            }
        }

        if (isEnabled)
        {
            await CloseMainWindowAsync();
        }
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
            TrimLightweightMemory();
        }
    }

    private void OnDesktopShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        _isExiting = true;
        Task.Run(PersistPlaybackSessionAsync).GetAwaiter().GetResult();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_mainWindowViewModel is not null)
        {
            _mainWindowViewModel.PlayerBar.PropertyChanged -= OnPlayerBarPropertyChanged;
            _mainWindowViewModel.PlayerBar.TrackChanged -= OnTrackChanged;
        }

        if (_playerService is not null)
        {
            _playerService.PlaybackStateChanged -= OnPlaybackStateChanged;
            _playerService.PositionChanged -= OnPlayerPositionChanged;
        }

        if (_desktop is not null)
        {
            _desktop.ShutdownRequested -= OnDesktopShutdownRequested;
        }

        if (_mainWindow is not null)
        {
            _mainWindow.Closing -= OnMainWindowClosing;
            _mainWindow.Closed -= OnMainWindowClosed;
        }

        _mediaTransportService?.Dispose();
        _services?.Dispose();
    }

    private static void TrimLightweightMemory()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
