using System.ComponentModel;
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
    private ServiceProvider? _services;
    private MainWindowViewModel? _mainWindowViewModel;
    private MainWindow? _mainWindow;
    private IMediaTransportService? _mediaTransportService;
    private IPlayerService? _playerService;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            _services = ConfigureServices();
            _mainWindowViewModel = _services.GetRequiredService<MainWindowViewModel>();
            _mediaTransportService = _services.GetRequiredService<IMediaTransportService>();
            _playerService = _services.GetRequiredService<IPlayerService>();

            _mainWindow = new MainWindow
            {
                DataContext = _mainWindowViewModel
            };

            desktop.MainWindow = _mainWindow;
            desktop.Exit += OnDesktopExit;

            WireTrayMenu();
            WireMediaTransport();

            _ = _mainWindowViewModel.InitializeAsync();
            _ = _mediaTransportService.InitializeAsync();
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

    private void WireTrayMenu()
    {
        if (_mainWindowViewModel is null)
        {
            return;
        }

        _mainWindowViewModel.PlayerBar.PropertyChanged += OnPlayerBarPropertyChanged;
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

    private void OnExitClick(object? sender, EventArgs e)
    {
        _desktop?.Shutdown();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
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

        _mediaTransportService?.Dispose();
        _services?.Dispose();
    }
}
