using Avalonia.Headless;
using AvaPlayer.Views;

namespace AvaPlayer.Tests.UI;

[Collection("UI")]
public sealed class FontStartupTests : IDisposable
{
    private readonly HeadlessUnitTestSession _ui = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

    [Fact]
    public Task Main_window_renders_with_application_fonts_before_viewmodel_initialization() => _ui.Dispatch(() =>
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.True(frame.PixelSize.Width > 0);
            Assert.True(frame.PixelSize.Height > 0);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    public void Dispose() => _ui.Dispose();
}
