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
    private const int VolumeInlineAnimationDurationMs = 200;

    private TopLevel? _topLevel;
    private PlayerBarViewModel? _viewModel;
    private bool _isVolumeInlinePanelOpen;
    private CancellationTokenSource? _volumeInlineAnimationCts;

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

        if (_isVolumeInlinePanelOpen)
        {
            await CloseVolumeInlinePanelAsync();
        }
        else
        {
            await OpenVolumeInlinePanelAsync();
        }
    }

    private async Task OpenVolumeInlinePanelAsync()
    {
        CancelVolumeInlineAnimation();

        VolumeInlinePanel.Classes.Remove("open");
        VolumeButton.Classes.Add("open");
        VolumeInlinePanel.IsHitTestVisible = true;
        _isVolumeInlinePanelOpen = true;

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        VolumeInlinePanel.Classes.Add("open");
    }

    private async Task CloseVolumeInlinePanelAsync()
    {
        if (!_isVolumeInlinePanelOpen)
        {
            return;
        }

        CancelVolumeInlineAnimation();

        var animationCts = new CancellationTokenSource();
        _volumeInlineAnimationCts = animationCts;
        var token = animationCts.Token;

        _isVolumeInlinePanelOpen = false;
        VolumeButton.Classes.Remove("open");
        VolumeInlinePanel.Classes.Remove("open");

        try
        {
            await Task.Delay(VolumeInlineAnimationDurationMs, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_volumeInlineAnimationCts, animationCts))
            {
                _volumeInlineAnimationCts.Dispose();
                _volumeInlineAnimationCts = null;
            }
            else
            {
                animationCts.Dispose();
            }
        }

        VolumeInlinePanel.IsHitTestVisible = false;
    }

    private async void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_isVolumeInlinePanelOpen || IsWithinVolumeControls(e.Source))
        {
            return;
        }

        await CloseVolumeInlinePanelAsync();
    }

    private async void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_isVolumeInlinePanelOpen)
        {
            return;
        }

        e.Handled = true;
        await CloseVolumeInlinePanelAsync();
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
            _isVolumeInlinePanelOpen)
        {
            _ = CloseVolumeInlinePanelAsync();
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
        CancelVolumeInlineAnimation();
        _isVolumeInlinePanelOpen = false;
        VolumeInlinePanel.IsHitTestVisible = false;
        VolumeInlinePanel.Classes.Remove("open");
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

    private void CancelVolumeInlineAnimation()
    {
        _volumeInlineAnimationCts?.Cancel();
        _volumeInlineAnimationCts?.Dispose();
        _volumeInlineAnimationCts = null;
    }

    private bool IsWithinVolumeControls(object? source) =>
        IsDescendantOrSelf(source, VolumeButton) || IsDescendantOrSelf(source, VolumeInlinePanel);

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
