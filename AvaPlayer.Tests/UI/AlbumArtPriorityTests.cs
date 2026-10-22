using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AvaPlayer.Models;
using AvaPlayer.Services.AlbumArt;
using AvaPlayer.Services.Cache;
using AvaPlayer.Services.Network;
using AvaPlayer.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvaPlayer.Tests.UI;

[Collection("UI")]
public sealed class AlbumArtPriorityTests : IDisposable
{
    private readonly HeadlessUnitTestSession _ui = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

    [Fact]
    public Task Updated_embedded_front_cover_overrides_download_cache() => _ui.Dispatch(async () =>
    {
        using var directory = new TestDirectory();
        using var cache = new CacheService(directory.FilePath("cache"));
        var path = directory.CreateWave();
        var remote = new RemoteCover(CreatePng(1, 1));
        var manager = new ArtProviders(new EmbeddedAlbumArtProvider(NullLogger<EmbeddedAlbumArtProvider>.Instance), remote);
        var service = new AlbumArtService(cache, manager, new OnlineNetwork(), NullLogger<AlbumArtService>.Instance);
        var track = new Track { FilePath = path, Title = "Song", Artist = "Artist", Album = "Album" };
        using var downloaded = await service.GetAlbumArtAsync(track);
        Assert.NotNull(downloaded);
        Assert.Equal(1, remote.Calls);
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Pictures =
            [
                new TagLib.Picture(new TagLib.ByteVector(CreatePng(1, 2))) { Type = TagLib.PictureType.BackCover },
                new TagLib.Picture(new TagLib.ByteVector(CreatePng(2, 1))) { Type = TagLib.PictureType.FrontCover }
            ];
            file.Save();
        }
        using var embedded = await service.GetAlbumArtAsync(track);
        Assert.NotNull(embedded);
        Assert.Equal(2, embedded.PixelSize.Width / embedded.PixelSize.Height);
        Assert.Equal(1, remote.Calls);
        return true;
    }, CancellationToken.None);

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    public void Dispose() => _ui.Dispose();

    private sealed class ArtProviders(params IAlbumArtProvider[] providers) : IAlbumArtProviderManager
    {
        public IReadOnlyList<IAlbumArtProvider> Providers { get; } = providers;
    }

    private sealed class RemoteCover(byte[] bytes) : IAlbumArtProvider
    {
        public int Calls { get; private set; }
        public string Name => "Test download";
        public bool RequiresNetwork => true;
        public Task<byte[]?> GetAlbumArtAsync(Track track, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<byte[]?>(bytes);
        }
    }

    private sealed class OnlineNetwork : INetworkAccessService
    {
        public bool IsNetworkEnabled { get; set; } = true;
        public bool IsEnabled => IsNetworkEnabled;
        public event EventHandler<bool>? NetworkAccessChanged { add { } remove { } }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PersistAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
