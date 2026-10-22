using System.Net;
using AvaPlayer.Services.AlbumArt;
using AvaPlayer.Services.Audio;
using AvaPlayer.Services.Cache;
using AvaPlayer.Services.Database;
using AvaPlayer.Services.Lyrics;
using AvaPlayer.Services.MediaTransport;
using AvaPlayer.Services.Network;
using AvaPlayer.Services.PlaybackSession;
using AvaPlayer.Services.Playlist;
using AvaPlayer.Services.Settings;
using AvaPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AvaPlayer.Services;

public static class ApplicationServices
{
    public static ServiceProvider Create()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());

        services.AddHttpClient();
        services.ConfigureHttpClientDefaults(builder => builder
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                ResponseDrainTimeout = TimeSpan.FromSeconds(5),
                MaxConnectionsPerServer = 16,
            })
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestVersion = HttpVersion.Version20;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            }));

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
        services.AddSingleton<IPlaybackSessionClient, PlaybackSession.PlaybackSession>();
        services.AddScoped<ILyricPreferencesService, LyricsPreferenceService>();
        services.AddScoped<LyricsViewModel>();
        services.AddScoped<ILyricPresentationService>(sp => sp.GetRequiredService<LyricsViewModel>());
        services.AddScoped<SettingsViewModel>();
        services.AddScoped<PlayerBarViewModel>();
        services.AddScoped<PlaylistViewModel>();
        services.AddScoped<MainWindowViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

}
