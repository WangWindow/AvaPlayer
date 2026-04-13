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
    private const double DirectSnapThreshold = 1.2;

    private LyricsViewModel? _viewModel;
    private CancellationTokenSource? _scrollAnimationCts;
    private int _lastCenteredLineIndex = -1;
    private double _lastTargetOffset;

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

        UpdateSpacerHeights();
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
        await Dispatcher.UIThread.InvokeAsync(UpdateSpacerHeights, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        var targetOffset = CalculateTargetOffset(lineIndex);
        await AnimateScrollToAsync(targetOffset);
        _lastCenteredLineIndex = lineIndex;
        _lastTargetOffset = targetOffset;
    }

    private double CalculateTargetOffset(int lineIndex)
    {
        if (_viewModel is null || lineIndex < 0 || lineIndex >= _viewModel.Lines.Count)
        {
            return 0;
        }

        if (TryGetVisualTargetOffset(lineIndex) is { } visualTargetOffset)
        {
            return visualTargetOffset;
        }

        var lineHeight = _viewModel.EstimatedLineHeight;
        if (_lastCenteredLineIndex >= 0)
        {
            var lineDelta = lineIndex - _lastCenteredLineIndex;
            if (Math.Abs(lineDelta) <= 2)
            {
                return ClampOffset(_lastTargetOffset + lineDelta * lineHeight);
            }
        }

        var spacerHeight = TopSpacer.Bounds.Height;
        var target = spacerHeight + lineIndex * lineHeight + lineHeight / 2d - LyricsScroller.Viewport.Height / 2d;
        return ClampOffset(target);
    }

    private async Task AnimateScrollToAsync(double targetOffset)
    {
        var startOffset = LyricsScroller.Offset.Y;
        if (Math.Abs(targetOffset - startOffset) < DirectSnapThreshold)
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
        var durationMs = Math.Clamp(200 + (int)(distance * 0.22), 220, 420);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            while (stopwatch.ElapsedMilliseconds < durationMs)
            {
                token.ThrowIfCancellationRequested();

                var progress = stopwatch.Elapsed.TotalMilliseconds / durationMs;
                var easedProgress = EaseInOutCubic(progress);
                var currentOffset = startOffset + (targetOffset - startOffset) * easedProgress;

                await Dispatcher.UIThread.InvokeAsync(
                    () => LyricsScroller.Offset = LyricsScroller.Offset.WithY(currentOffset),
                    DispatcherPriority.Render);

                await Task.Delay(10, token);
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
        if (e.PropertyName == nameof(LyricsViewModel.EstimatedLineHeight) ||
            e.PropertyName == nameof(LyricsViewModel.HasLyrics))
        {
            ResetScrollAnchor();
            UpdateSpacerHeights();
        }
    }

    private void OnLyricsScrollerSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ResetScrollAnchor();
        UpdateSpacerHeights();
    }

    private void UpdateSpacerHeights()
    {
        if (LyricsScroller.Bounds.Height <= 0)
        {
            return;
        }

        var halfViewport = LyricsScroller.Bounds.Height / 2d;
        TopSpacer.Height = halfViewport;
        BottomSpacer.Height = halfViewport;
    }

    private double ClampOffset(double offset) =>
        Math.Clamp(offset, 0, Math.Max(0, LyricsScroller.Extent.Height - LyricsScroller.Viewport.Height));

    private double? TryGetVisualTargetOffset(int lineIndex)
    {
        if (_viewModel is null || lineIndex < 0 || lineIndex >= _viewModel.Lines.Count)
        {
            return null;
        }

        var targetLine = _viewModel.Lines[lineIndex];
        var lineButton = LyricsItems
            .GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => ReferenceEquals(button.DataContext, targetLine));

        if (lineButton is null)
        {
            return null;
        }

        var lineCenter = lineButton.TranslatePoint(
            new Point(lineButton.Bounds.Width / 2d, lineButton.Bounds.Height / 2d),
            LyricsScroller);

        return lineCenter.HasValue
            ? ClampOffset(LyricsScroller.Offset.Y + lineCenter.Value.Y - LyricsScroller.Viewport.Height / 2d)
            : null;
    }

    private void ResetScrollAnchor()
    {
        _lastCenteredLineIndex = -1;
        _lastTargetOffset = LyricsScroller.Offset.Y;
    }

    private static double EaseInOutCubic(double progress)
    {
        if (progress <= 0)
        {
            return 0;
        }

        if (progress >= 1)
        {
            return 1;
        }

        return progress < 0.5
            ? 4d * progress * progress * progress
            : 1d - Math.Pow(-2d * progress + 2d, 3d) / 2d;
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _scrollAnimationCts?.Cancel();
        _scrollAnimationCts?.Dispose();
        _scrollAnimationCts = null;
        ResetScrollAnchor();

        if (_viewModel is not null)
        {
            _viewModel.ScrollToLineRequested -= OnScrollToLineRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }
}
