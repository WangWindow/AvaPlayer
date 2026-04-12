using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaPlayer.ViewModels;

namespace AvaPlayer.Views;

public partial class LyricsView : UserControl
{
    private const double MinimumVerticalPadding = 120;

    private LyricsViewModel? _viewModel;
    private CancellationTokenSource? _scrollAnimationCts;

    public LyricsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        LyricsScroller.SizeChanged += OnLyricsScrollerSizeChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToLineRequested -= OnScrollToLineRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as LyricsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ScrollToLineRequested += OnScrollToLineRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateScrollerPadding();
    }

    private void OnScrollToLineRequested(object? sender, int lineIndex)
    {
        if (_viewModel is null || !_viewModel.IsAutoCenterEnabled)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => _ = CenterLineAsync(lineIndex), DispatcherPriority.Render);
    }

    private async Task CenterLineAsync(int lineIndex)
    {
        UpdateScrollerPadding();

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        var targetOffset = CalculateTargetOffset(lineIndex);
        await AnimateScrollToAsync(targetOffset);
    }

    private double CalculateTargetOffset(int lineIndex)
    {
        if (_viewModel is null || lineIndex < 0 || lineIndex >= _viewModel.Lines.Count)
        {
            return 0;
        }

        var targetLine = _viewModel.Lines[lineIndex];
        var lineButton = LyricsScroller
            .GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => ReferenceEquals(button.DataContext, targetLine));

        if (lineButton is not null)
        {
            var lineCenter = lineButton.TranslatePoint(
                new Point(lineButton.Bounds.Width / 2d, lineButton.Bounds.Height / 2d),
                LyricsScroller);

            if (lineCenter.HasValue)
            {
                return ClampOffset(
                    LyricsScroller.Offset.Y + lineCenter.Value.Y - LyricsScroller.Viewport.Height / 2d);
            }
        }

        var lineHeight = _viewModel.EstimatedLineHeight;
        var target = LyricsScroller.Padding.Top + lineIndex * lineHeight - LyricsScroller.Viewport.Height / 2d + lineHeight / 2d;
        return ClampOffset(target);
    }

    private async Task AnimateScrollToAsync(double targetOffset)
    {
        var startOffset = LyricsScroller.Offset.Y;
        if (Math.Abs(targetOffset - startOffset) < 0.5)
        {
            LyricsScroller.Offset = LyricsScroller.Offset.WithY(targetOffset);
            return;
        }

        _scrollAnimationCts?.Cancel();
        _scrollAnimationCts?.Dispose();

        var animationCts = new CancellationTokenSource();
        _scrollAnimationCts = animationCts;
        var token = animationCts.Token;
        var distance = Math.Abs(targetOffset - startOffset);
        var durationMs = Math.Clamp(170 + (int)(distance * 0.18), 180, 320);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            while (stopwatch.ElapsedMilliseconds < durationMs)
            {
                token.ThrowIfCancellationRequested();

                var progress = stopwatch.Elapsed.TotalMilliseconds / durationMs;
                var easedProgress = 1d - Math.Pow(1d - progress, 3d);
                var currentOffset = startOffset + (targetOffset - startOffset) * easedProgress;

                await Dispatcher.UIThread.InvokeAsync(
                    () => LyricsScroller.Offset = LyricsScroller.Offset.WithY(currentOffset),
                    DispatcherPriority.Render);

                await Task.Delay(16, token);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_scrollAnimationCts, animationCts))
            {
                _scrollAnimationCts.Dispose();
                _scrollAnimationCts = null;
            }
            else
            {
                animationCts.Dispose();
            }
        }

        LyricsScroller.Offset = LyricsScroller.Offset.WithY(targetOffset);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.EstimatedLineHeight))
        {
            UpdateScrollerPadding();
        }
    }

    private void OnLyricsScrollerSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateScrollerPadding();

    private void UpdateScrollerPadding()
    {
        var estimatedLineHeight = _viewModel?.EstimatedLineHeight ?? 60;
        if (LyricsScroller.Bounds.Height <= 0)
        {
            return;
        }

        var verticalPadding = Math.Max(
            MinimumVerticalPadding,
            (LyricsScroller.Bounds.Height - estimatedLineHeight) / 2d);

        LyricsScroller.Padding = new Thickness(
            LyricsScroller.Padding.Left > 0 ? LyricsScroller.Padding.Left : 30,
            verticalPadding,
            LyricsScroller.Padding.Right > 0 ? LyricsScroller.Padding.Right : 30,
            verticalPadding);
    }

    private double ClampOffset(double offset) =>
        Math.Clamp(offset, 0, Math.Max(0, LyricsScroller.Extent.Height - LyricsScroller.Viewport.Height));

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _scrollAnimationCts?.Cancel();
        _scrollAnimationCts?.Dispose();
        _scrollAnimationCts = null;

        if (_viewModel is not null)
        {
            _viewModel.ScrollToLineRequested -= OnScrollToLineRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }
}
