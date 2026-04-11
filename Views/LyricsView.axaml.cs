using Avalonia.Controls;
using Avalonia.Threading;
using AvaPlayer.ViewModels;

namespace AvaPlayer.Views;

public partial class LyricsView : UserControl
{
    private LyricsViewModel? _viewModel;

    public LyricsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToLineRequested -= OnScrollToLineRequested;
        }

        _viewModel = DataContext as LyricsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ScrollToLineRequested += OnScrollToLineRequested;
        }
    }

    private void OnScrollToLineRequested(object? sender, int lineIndex)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var offsetY = Math.Max(0, lineIndex * 36d - LyricsScroller.Viewport.Height / 2d + 18d);
            LyricsScroller.Offset = LyricsScroller.Offset.WithY(offsetY);
        }, DispatcherPriority.Render);
    }
}
