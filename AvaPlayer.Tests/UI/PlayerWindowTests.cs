using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaPlayer.Models;
using Track = AvaPlayer.Models.Track;
using AvaPlayer.Services.AlbumArt;
using AvaPlayer.Services.Cache;
using AvaPlayer.Services.Lyrics;
using AvaPlayer.Services.Network;
using AvaPlayer.Services.Settings;
using AvaPlayer.Tests.Fakes;
using AvaPlayer.Tests.Support;
using AvaPlayer.ViewModels;
using AvaPlayer.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvaPlayer.Tests.UI;

[Collection("UI")]
public sealed class PlayerWindowTests : IDisposable
{
    private readonly HeadlessUnitTestSession _ui = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

    public void Dispose() => _ui.Dispose();

    [Theory]
    [InlineData(720, 480, false, 1.0)]
    [InlineData(720, 480, true, 1.5)]
    [InlineData(1040, 760, true, 1.0)]
    [InlineData(1040, 760, false, 2.0)]
    [InlineData(1440, 900, false, 1.0)]
    public Task Sidebar_playback_and_settings_remain_accessible_at_supported_sizes(int width, int height, bool dark, double scaling) => _ui.Dispatch(async () =>
    {
        using var fixture = new WindowFixture();
        var window = fixture.Window;
        window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        window.Show();
        await fixture.ViewModel.InitializeAsync();
        window.SetRenderScaling(scaling);
        window.Width = width;
        window.Height = height;
        Click(window, window.FindControl<Button>("PlaylistToggle")!);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var library = window.FindControl<PlaylistView>("LibraryContent")!;
        Assert.True(library.IsEffectivelyVisible);
        Assert.InRange(library.Bounds.Height, height - 180, height);
        var drawer = window.FindControl<Border>("PlaylistDrawer")!;
        Assert.Equal(380, drawer.Bounds.Width);
        Assert.Equal(0, drawer.Bounds.X);
        Assert.Empty(window.GetVisualDescendants().OfType<Expander>());
        await AdvanceAnimationsAsync();
        window.UpdateLayout();
        var subfolders = library.FindControl<ToggleButton>("SubfoldersToggle")!;
        Assert.True(subfolders.IsChecked);
        Click(window, subfolders);
        Assert.False(fixture.ViewModel.Playlist.IncludeSubfolders);
        Click(window, subfolders);
        Assert.True(fixture.ViewModel.Playlist.IncludeSubfolders);
        var tools = library.FindControl<Grid>("LibraryTools")!;
        var trackList = library.FindControl<ListBox>("TrackList")!;
        Assert.InRange(trackList.TranslatePoint(default, library)!.Value.Y, 0, 110);
        Assert.Equal(tools, subfolders.Parent);
        Assert.Equal(tools, library.FindControl<Button>("RescanButton")!.Parent);
        var selector = library.GetVisualDescendants().OfType<ComboBox>().Single();
        Assert.True(selector.IsEffectivelyVisible);
        Assert.True(selector.Bounds.Width > 0);
        var playButton = window.GetVisualDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "播放 / 暂停");
        var playPosition = playButton.TranslatePoint(default, window)!.Value;
        Assert.InRange(playPosition.Y + playButton.Bounds.Height, 0, height);
        fixture.ViewModel.Playlist.MarkCurrentTrack(fixture.ViewModel.Playlist.Tracks[0].Track);
        SaveFrame(window, $"sidebar-{width}-{height}-{scaling}.png");
        fixture.ViewModel.ClosePlaylistCommand.Execute(null);
        Assert.True(library.IsEffectivelyEnabled);
        await AdvanceAnimationsAsync();
        window.UpdateLayout();
        Assert.False(library.IsEffectivelyVisible, $"VM={fixture.ViewModel.IsPlaylistVisible}, presenter={window.FindControl<RevealPresenter>("PlaylistReveal")!.IsOpen}, opacity={window.FindControl<RevealPresenter>("PlaylistReveal")!.Opacity}, enabled={library.IsEffectivelyEnabled}");
        fixture.Session.Publish(fixture.Session.LatestSnapshot with
        {
            CurrentTrack = fixture.ViewModel.Playlist.Tracks[0].Track,
            Duration = 120
        });
        await fixture.ViewModel.Settings.RefreshCacheCommand.ExecuteAsync(null);
        window.UpdateLayout();
        playPosition = playButton.TranslatePoint(default, window)!.Value;
        Assert.InRange(playPosition.Y + playButton.Bounds.Height, 0, height);
        fixture.ViewModel.Lyrics.LoadLyrics([
            new() { Time = TimeSpan.Zero, Text = "夜空中最亮的星" },
            new() { Time = TimeSpan.FromSeconds(12), Text = "能否听清" },
            new() { Time = TimeSpan.FromSeconds(24), Text = "那仰望的人心底的孤独和叹息" },
            new() { Time = TimeSpan.FromSeconds(36), Text = "Shining through the night" },
            new() { Time = TimeSpan.FromSeconds(48), Text = "请照亮我前行" }
        ]);
        fixture.ViewModel.Lyrics.UpdatePosition(24);
        window.UpdateLayout();
        await Task.Delay(500);
        Assert.Equal(width, window.Bounds.Width);
        var backdrop = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("player-backdrop"));
        Assert.Equal(dark ? 1 : 0.12, backdrop.Opacity);
        Assert.Null(window.FindControl<Border>("PlayerPanel")!.Background);
        Assert.Null(window.FindControl<Border>("LyricsPanel")!.Background);
        var lyric = window.GetVisualDescendants().OfType<TextBlock>().First(b => b.Classes.Contains("lyric-line"));
        Assert.Equal(dark ? Colors.White : Color.Parse("#1A1A1A"), Assert.IsAssignableFrom<ISolidColorBrush>(lyric.Foreground).Color);
        SaveFrame(window, $"player-{width}-{height}-{scaling}.png");

        var settingsToggle = window.FindControl<Button>("SettingsToggle")!;
        Assert.IsType<FluentIcons.Avalonia.FluentIcon>(settingsToggle.Content);
        Click(window, settingsToggle);
        window.UpdateLayout();
        Assert.True(fixture.ViewModel.PlayerBar.IsSettingsVisible);
        await AdvanceAnimationsAsync();
        window.UpdateLayout();
        var settings = window.GetVisualDescendants().OfType<SettingsView>().Single();
        Assert.True(settings.IsEffectivelyVisible);
        Assert.Contains(settings.GetVisualDescendants().OfType<Button>(),
            b => AutomationProperties.GetName(b) == "刷新歌词与封面");
        var settingsPanel = settings.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("settings-panel"));
        Assert.Equal(new Thickness(0), settingsPanel.BorderThickness);
        Assert.All(settings.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("settings-row")),
            row => Assert.InRange(row.Bounds.Height, 0, 84));
        Assert.Equal(FluentIcons.Common.Icon.ChevronLeft,
            Assert.IsType<FluentIcons.Avalonia.FluentIcon>(settingsToggle.Content).Icon);
        var corners = settings.GetVisualDescendants().OfType<ToggleSwitch>()
            .Single(toggle => AutomationProperties.GetName(toggle) == "窗口圆角");
        corners.IsChecked = false;
        Assert.Equal(new CornerRadius(0), window.FindControl<Border>("WindowSurface")!.CornerRadius);
        corners.IsChecked = true;
        SaveFrame(window, $"settings-{width}-{height}-{scaling}.png");
        Click(window, settingsToggle);
        Assert.False(fixture.ViewModel.PlayerBar.IsSettingsVisible);
        window.Close();
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Search_and_slider_keyboard_do_not_trigger_global_playback_commands() => _ui.Dispatch(async () =>
    {
        using var fixture = new WindowFixture();
        var window = fixture.Window;
        window.Show();
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.IsPlaylistVisible = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var search = window.GetVisualDescendants().OfType<TextBox>()
            .Single(t => t.PlaceholderText == "搜索当前播放列表");
        search.Focus();
        window.KeyTextInput("Song Two");
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Assert.Equal("Song Two", fixture.ViewModel.Playlist.SearchText);
        Assert.Single(fixture.ViewModel.Playlist.VisibleTracks);
        Assert.Null(fixture.Session.LastPlayedTrack);
        window.Close();
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Sidebar_first_open_and_reopen_fade_and_slide_from_the_closed_state() => _ui.Dispatch(async () =>
    {
        using var fixture = new WindowFixture();
        var window = fixture.Window;
        window.Show();
        await fixture.ViewModel.InitializeAsync();
        await AdvanceAnimationsAsync();
        var reveal = window.FindControl<RevealPresenter>("PlaylistReveal")!;
        var drawer = window.FindControl<Border>("PlaylistDrawer")!;
        for (var opening = 0; opening < 2; opening++)
        {
            Assert.False(reveal.IsVisible);
            var toggle = window.FindControl<Button>("PlaylistToggle")!;
            toggle.Command!.Execute(toggle.CommandParameter);
            window.UpdateLayout();
            var scrim = window.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Classes.Contains("playlist-drawer-scrim"));
            Assert.InRange(drawer.Opacity, 0, 0.05);
            Assert.InRange(drawer.RenderTransform!.Value.M31, -36, -34);
            Assert.InRange(scrim.Opacity, 0, 0.05);
            await AdvanceAnimationsAsync(100);
            Assert.InRange(drawer.Opacity, 0.05, 0.95);
            Assert.InRange(drawer.RenderTransform.Value.M31, -34, -1);
            Assert.InRange(scrim.Opacity, 0.05, 0.95);
            SaveFrame(window, $"sidebar-opening-{opening}.png");
            await AdvanceAnimationsAsync();
            Assert.Equal(1, drawer.Opacity);
            Assert.Equal(0, drawer.RenderTransform.Value.M31);
            Click(window, window.FindControl<Button>("PlaylistToggle")!);
            await AdvanceAnimationsAsync();
        }
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(720, 480)]
    [InlineData(1040, 760)]
    [InlineData(1440, 900)]
    public Task Reveals_reverse_cleanly_and_volume_expands_inside_the_player(int width, int height) => _ui.Dispatch(async () =>
    {
        using var fixture = new WindowFixture();
        var window = fixture.Window;
        window.Show();
        await fixture.ViewModel.InitializeAsync();
        window.Width = width;
        window.Height = height;
        window.UpdateLayout();
        await AdvanceAnimationsAsync();
        var settingsButton = window.FindControl<Button>("SettingsToggle")!;
        var settings = window.FindControl<RevealPresenter>("SettingsReveal")!;
        var lyricsPanel = window.FindControl<Border>("LyricsPanel")!;
        fixture.ViewModel.Lyrics.LoadLyrics([
            new() { Time = TimeSpan.Zero, Text = "Every shalala every wo'wo" },
            new() { Time = TimeSpan.FromSeconds(10), Text = "Still shines" },
            new() { Time = TimeSpan.FromSeconds(20), Text = "Every shing-a-ling-a-ling" }
        ]);
        fixture.ViewModel.Lyrics.UpdatePosition(10);
        Action noOverlappingText = () => Assert.False(settings.IsEffectivelyVisible && lyricsPanel.IsEffectivelyVisible);
        Click(window, settingsButton);
        Assert.False(lyricsPanel.IsEffectivelyVisible);
        await AdvanceAnimationsAsync(80, noOverlappingText);
        Assert.True(settings.IsOpen, $"VM={fixture.ViewModel.PlayerBar.IsSettingsVisible}, visible={settings.IsVisible}, context={settings.DataContext?.GetType().Name}");
        Assert.InRange(settings.Opacity, 0, 1);
        Assert.InRange(settings.RenderTransform!.Value.M32, -18, 0);
        Assert.Equal(0, settings.RenderTransform.Value.M31);
        Click(window, settingsButton);
        Assert.False(lyricsPanel.IsEffectivelyVisible);
        Assert.True(settings.IsEffectivelyEnabled);
        await AdvanceAnimationsAsync(40, noOverlappingText);
        Click(window, settingsButton);
        await AdvanceAnimationsAsync();
        Assert.True(settings.IsVisible);
        Assert.Equal(1, settings.Opacity);
        Assert.Equal(0, settings.RenderTransform!.Value.M32);
        Click(window, settingsButton);
        await AdvanceAnimationsAsync();
        Assert.False(settings.IsVisible);
        Assert.True(lyricsPanel.IsEffectivelyVisible);

        var volumeButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) == "音量");
        Assert.Null(volumeButton.Flyout);
        var volume = window.GetVisualDescendants().OfType<RevealPresenter>()
            .Single(presenter => presenter.Name == "VolumeInlinePanel");
        var cover = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "CoverBorder");
        var coverBounds = cover.Bounds;
        var coverPosition = cover.TranslatePoint(default, window);
        var controls = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "ControlGrid");
        var controlsPosition = controls.TranslatePoint(default, window);
        var player = window.FindControl<Border>("PlayerPanel")!;
        var playButton = window.GetVisualDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "播放 / 暂停");
        var coverTop = cover.TranslatePoint(default, player)!.Value.Y;
        var controlsBottom = playButton.TranslatePoint(new Point(0, playButton.Bounds.Height), player)!.Value.Y;
        Assert.InRange(Math.Abs(coverTop - (player.Bounds.Height - controlsBottom)), 0, 1);
        SaveFrame(window, $"player-centered-{width}-{height}.png");
        Assert.InRange(cover.Bounds.Width, width >= 1040 ? 250 : 72, width / 2d - 96);
        Click(window, volumeButton);
        await AdvanceAnimationsAsync();
        Assert.Equal(coverBounds, cover.Bounds);
        Assert.Equal(coverPosition, cover.TranslatePoint(default, window));
        Assert.Equal(controlsPosition, controls.TranslatePoint(default, window));
        var slider = window.GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "VolumeSlider");
        Assert.True(slider.IsEffectivelyVisible);
        Assert.Equal(window, TopLevel.GetTopLevel(slider));
        Assert.InRange(volume.Bounds.Height, 28, 80);
        Assert.InRange(slider.TranslatePoint(default, window)!.Value.Y + slider.Bounds.Height, 0, height);
        slider.Focus();
        var initialVolume = fixture.ViewModel.PlayerBar.Volume;
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.True(fixture.ViewModel.PlayerBar.Volume > initialVolume);
        fixture.ViewModel.PlayerBar.Volume = 50;
        window.UpdateLayout();
        var playerCenter = playButton.TranslatePoint(new Point(playButton.Bounds.Width / 2, 0), window)!.Value.X;
        var sliderCenter = slider.TranslatePoint(new Point(slider.Bounds.Width / 2, 0), window)!.Value.X;
        Assert.InRange(Math.Abs(playerCenter - sliderCenter), 0, 0.5);
        var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
        var thumbCenter = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, 0), window)!.Value.X;
        Assert.InRange(Math.Abs(playerCenter - thumbCenter), 0, 0.5);
        SaveFrame(window, $"volume-{width}-{height}.png");
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await AdvanceAnimationsAsync();
        Assert.False(volume.IsVisible);
        Assert.Equal(coverBounds, cover.Bounds);
        Assert.Equal(coverPosition, cover.TranslatePoint(default, window));
        Click(window, volumeButton);
        await AdvanceAnimationsAsync();
        var outside = new Point(width - 40, 80);
        window.MouseDown(outside, MouseButton.Left);
        window.MouseUp(outside, MouseButton.Left);
        await AdvanceAnimationsAsync();
        Assert.False(volume.IsVisible);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Rescan_keeps_song_rows_and_search_active_without_white_disabled_surfaces() => _ui.Dispatch(async () =>
    {
        using var fixture = new WindowFixture();
        var window = fixture.Window;
        window.RequestedThemeVariant = ThemeVariant.Dark;
        window.Show();
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.IsPlaylistVisible = true;
        await AdvanceAnimationsAsync();
        window.UpdateLayout();
        var library = window.FindControl<PlaylistView>("LibraryContent")!;
        var rows = library.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("playlist-item")).ToArray();
        var colors = rows.Select(RowBackground).ToArray();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.PlaylistService.RescanHandler = token => gate.Task.WaitAsync(token);
        Click(window, library.FindControl<Button>("RescanButton")!);
        var scan = fixture.ViewModel.Playlist.RescanPlaylistCommand.ExecutionTask!;
        Assert.NotNull(scan);
        try
        {
            window.UpdateLayout();
            Assert.True(fixture.ViewModel.Playlist.IsScanning);
            Assert.All(rows, row => Assert.True(row.IsEffectivelyEnabled));
            Assert.Equal(colors, rows.Select(RowBackground));
            Assert.True(library.GetVisualDescendants().OfType<TextBox>()
                .Single(t => t.PlaceholderText == "搜索当前播放列表").IsEffectivelyEnabled);
            var add = library.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetName(b) == "添加歌单");
            Assert.False(add.IsEffectivelyEnabled);
            Assert.Equal(0, RowBackground(add).A);
            var subfolders = library.FindControl<ToggleButton>("SubfoldersToggle")!;
            Assert.True(subfolders.IsChecked);
            Assert.False(subfolders.IsEffectivelyEnabled);
            Assert.True(RowBackground(subfolders).A > 0);
            SaveFrame(window, "sidebar-scanning.png");
        }
        finally
        {
            gate.TrySetResult();
            await scan;
        }
        Assert.False(fixture.ViewModel.Playlist.IsScanning);
        return true;
    }, CancellationToken.None);

    private static Color RowBackground(Button button)
    {
        var presenter = button.GetVisualDescendants().OfType<ContentPresenter>()
            .Single(p => ReferenceEquals(p.TemplatedParent, button));
        return (presenter.Background as ISolidColorBrush)?.Color ?? Colors.Transparent;
    }

    [Fact]
    public Task Track_context_menu_removes_a_song_and_still_offers_bulk_selection() => _ui.Dispatch(async () =>
    {
        using var fixture = new WindowFixture();
        var window = fixture.Window;
        window.Show();
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.IsPlaylistVisible = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var list = window.FindControl<PlaylistView>("LibraryContent")!.FindControl<ListBox>("TrackList")!;
        var row = list.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("playlist-item"));
        var item = Assert.IsType<TrackItemViewModel>(row.DataContext);
        var center = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Right);
        window.MouseUp(center, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        var menu = row.ContextMenu!;
        Assert.True(menu.IsOpen);
        var remove = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "从歌单移除"));
        Assert.Same(item, remove.CommandParameter);
        var bulk = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "选择多首歌曲"));
        Assert.NotNull(bulk.Command);
        bulk.Command.Execute(bulk.CommandParameter);
        Assert.True(fixture.ViewModel.Playlist.IsEditMode);
        fixture.ViewModel.Playlist.CancelEditCommand.Execute(null);
        await Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(remove.Command)
            .ExecuteAsync(remove.CommandParameter);
        menu.Close();
        Assert.DoesNotContain(fixture.ViewModel.Playlist.Tracks, t => t.Track.FilePath == item.Track.FilePath);
        Assert.Single(fixture.ViewModel.Playlist.Tracks);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Refresh_clears_metadata_and_negative_cache_but_keeps_settings() => _ui.Dispatch(async () =>
    {
        using var fixture = new WindowFixture();
        await fixture.Cache.SetTextAsync("lyrics", "test", "old");
        await fixture.Cache.MarkMissingAsync("album-art", "missing", TimeSpan.FromDays(1));
        await fixture.Settings.SetAsync("current-track-path", "keep.wav");
        await fixture.ViewModel.Settings.RefreshCacheCommand.ExecuteAsync(null);
        Assert.Null(await fixture.Cache.GetTextAsync("lyrics", "test"));
        Assert.False(await fixture.Cache.IsKnownMissingAsync("album-art", "missing"));
        Assert.Equal("keep.wav", await fixture.Settings.GetAsync("current-track-path"));
        return true;
    }, CancellationToken.None);

    private static async Task AdvanceAnimationsAsync(int milliseconds = 320, Action? onFrame = null)
    {
        // Headless does not advance the render clock while awaiting wall time.
        for (var elapsed = 0; elapsed <= milliseconds; elapsed += 20)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            onFrame?.Invoke();
            await Task.Delay(20);
        }
    }

    private static void Click(Window window, Button button)
    {
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void SaveFrame(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("AVAPLAYER_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        window.MouseMove(new Point(window.Bounds.Width / 2, window.Bounds.Height - 12));
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }

    private sealed class WindowFixture : IDisposable
    {
        private readonly TestDirectory _directory = new();
        public FakePlaybackSessionClient Session { get; } = new();
        public FakeSettingsService Settings { get; } = new();
        public FakePlaylistService PlaylistService { get; } = new();
        public CacheService Cache { get; }
        public MainWindowViewModel ViewModel { get; }
        public MainWindow Window { get; }

        public WindowFixture()
        {
            Cache = new CacheService(_directory.FilePath("cache"));
            var preferences = new LyricsPreferenceService(Settings, NullLogger<LyricsPreferenceService>.Instance);
            var lyrics = new LyricsViewModel(preferences, NullLogger<LyricsViewModel>.Instance);
            var playlist = PlaylistService;
            playlist.AddPlaylist(new PlaylistInfo { Id = "p1", Name = "我的音乐", FolderPath = _directory.Root, TrackCount = 2 });
            playlist.AddTrack(new Track { Id = "1", FilePath = "1.wav", Title = "Song One", Artist = "Artist", DurationSeconds = 120 });
            playlist.AddTrack(new Track { Id = "2", FilePath = "2.wav", Title = "Song Two", Artist = "Artist", DurationSeconds = 120 });
            var player = new PlayerBarViewModel(new EmptyAlbumArt(), new EmptyLyrics(), Settings, lyrics, Session,
                NullLogger<PlayerBarViewModel>.Instance);
            ViewModel = new MainWindowViewModel(player,
                new PlaylistViewModel(playlist, NullLogger<PlaylistViewModel>.Instance),
                new SettingsViewModel(Settings, new NetworkAccessService(new AvaPlayer.Services.Database.SqliteDatabaseService(_directory.FilePath("test.db")), NullLogger<NetworkAccessService>.Instance),
                    preferences, new FakePlaybackPositionMemoryService(), Cache), lyrics, playlist, Session);
            Window = new MainWindow { DataContext = ViewModel };
        }

        public void Dispose()
        {
            Window.Close();
            ViewModel.Dispose();
            ViewModel.PlayerBar.Dispose();
            ViewModel.Playlist.Dispose();
            ViewModel.Settings.Dispose();
            ViewModel.Lyrics.Dispose();
            Cache.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class EmptyAlbumArt : IAlbumArtService
    {
        public Task<Bitmap?> GetAlbumArtAsync(Track track, CancellationToken cancellationToken = default) => Task.FromResult<Bitmap?>(null);
    }

    private sealed class EmptyLyrics : ILyricsService
    {
        public Task<IReadOnlyList<LyricLine>> GetLyricsAsync(Track track, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LyricLine>>([]);
    }
}

[CollectionDefinition("UI", DisableParallelization = true)]
public sealed class UiCollection;
