using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaPlayer.ViewModels;

namespace AvaPlayer.Views;

public partial class PlayerBarView : UserControl
{
    private const int VolumePopoverAnimationDurationMs = 180;

    private TopLevel? _topLevel;
    private PlayerBarViewModel? _viewModel;
    private bool _isVolumePopoverOpen;
    private CancellationTokenSource? _volumePopoverAnimationCts;

    public PlayerBarView()
    {
        InitializeComponent();

        ProgressSlider.AddHandler(PointerPressedEvent, OnSliderPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ProgressSlider.AddHandler(PointerReleasedEvent, OnSliderPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnSliderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is PlayerBarViewModel viewModel)
        {
            viewModel.IsUserSeeking = true;
        }
    }

    private void OnSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is PlayerBarViewModel viewModel)
        {
            viewModel.IsUserSeeking = false;
            viewModel.SeekCommand.Execute(viewModel.Position);
        }
    }

    private async void OnVolumeButtonClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (_isVolumePopoverOpen)
        {
            await CloseVolumePopoverAsync();
        }
        else
        {
            await OpenVolumePopoverAsync();
        }
    }

    private async Task OpenVolumePopoverAsync()
    {
        CancelVolumePopoverAnimation();

        VolumePopoverCard.Classes.Remove("open");
        VolumeButton.Classes.Add("open");
        VolumePopoverCard.IsHitTestVisible = true;
        _isVolumePopoverOpen = true;

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        VolumePopoverCard.Classes.Add("open");
    }

    private async Task CloseVolumePopoverAsync()
    {
        if (!_isVolumePopoverOpen)
        {
            return;
        }

        CancelVolumePopoverAnimation();

        var animationCts = new CancellationTokenSource();
        _volumePopoverAnimationCts = animationCts;
        var token = animationCts.Token;

        _isVolumePopoverOpen = false;
        VolumeButton.Classes.Remove("open");
        VolumePopoverCard.Classes.Remove("open");

        try
        {
            await Task.Delay(VolumePopoverAnimationDurationMs, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_volumePopoverAnimationCts, animationCts))
            {
                _volumePopoverAnimationCts.Dispose();
                _volumePopoverAnimationCts = null;
            }
            else
            {
                animationCts.Dispose();
            }
        }

        VolumePopoverCard.IsHitTestVisible = false;
    }

    private async void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_isVolumePopoverOpen || IsWithinVolumeControls(e.Source))
        {
            return;
        }

        await CloseVolumePopoverAsync();
    }

    private async void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_isVolumePopoverOpen)
        {
            return;
        }

        e.Handled = true;
        await CloseVolumePopoverAsync();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as PlayerBarViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerBarViewModel.IsSettingsVisible) &&
            _viewModel?.IsSettingsVisible == true &&
            _isVolumePopoverOpen)
        {
            _ = CloseVolumePopoverAsync();
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        OnDataContextChanged(this, EventArgs.Empty);
        DetachTopLevelHandlers();
        _topLevel = TopLevel.GetTopLevel(this);

        if (_topLevel is null)
        {
            return;
        }

        _topLevel.AddHandler(PointerPressedEvent, OnTopLevelPointerPressed, RoutingStrategies.Tunnel);
        _topLevel.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        CancelVolumePopoverAnimation();
        _isVolumePopoverOpen = false;
        VolumePopoverCard.IsHitTestVisible = false;
        VolumePopoverCard.Classes.Remove("open");
        VolumeButton.Classes.Remove("open");
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }
        DetachTopLevelHandlers();
    }

    private void DetachTopLevelHandlers()
    {
        if (_topLevel is null)
        {
            return;
        }

        _topLevel.RemoveHandler(PointerPressedEvent, OnTopLevelPointerPressed);
        _topLevel.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        _topLevel = null;
    }

    private void CancelVolumePopoverAnimation()
    {
        _volumePopoverAnimationCts?.Cancel();
        _volumePopoverAnimationCts?.Dispose();
        _volumePopoverAnimationCts = null;
    }

    private bool IsWithinVolumeControls(object? source) =>
        IsDescendantOrSelf(source, VolumeButton) || IsDescendantOrSelf(source, VolumePopoverCard);

    private static bool IsDescendantOrSelf(object? source, Visual target)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, target))
            {
                return true;
            }
        }

        return false;
    }
}
