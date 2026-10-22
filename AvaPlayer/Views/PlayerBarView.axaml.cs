using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaPlayer.ViewModels;

namespace AvaPlayer.Views;

public partial class PlayerBarView : UserControl
{
    private TopLevel? _topLevel;

    public PlayerBarView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        ProgressSlider.AddHandler(PointerPressedEvent, OnSeekStarted, RoutingStrategies.Tunnel);
        ProgressSlider.AddHandler(PointerReleasedEvent, OnSeekCompleted, RoutingStrategies.Bubble, handledEventsToo: true);
        ProgressSlider.AddHandler(KeyUpEvent, OnSeekKeyUp, RoutingStrategies.Bubble, handledEventsToo: true);
        ProgressSlider.PointerCaptureLost += (_, _) =>
        {
            if (DataContext is PlayerBarViewModel vm) vm.IsUserSeeking = false;
        };
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => UpdatePlaybackLayout(e.NewSize);

    private void UpdatePlaybackLayout(Size size)
    {
        if (DataContext is not PlayerBarViewModel vm) return;
        var compact = size.Height < 300;
        PlaybackRoot.Margin = new Thickness(size.Width < 430 ? 24 : 48, compact ? 8 : 24);
        CoverBorder.IsVisible = !compact;
        ArtistText.IsVisible = size.Height >= 240;
        TrackDetails.Margin = new Thickness(0, compact ? 0 : 16, 0, 0);
        ProgressGrid.Margin = new Thickness(0, 8, 0, 0);
        ControlGrid.Margin = new Thickness(0, 8, 0, 0);
        var preferredCoverSize = Math.Clamp((size.Width - 112) * 0.65, 160, 400);
        // Leave equal space around the visible player, including room for volume below it.
        vm.CoverSize = Math.Round(Math.Min(preferredCoverSize, Math.Max(72, size.Height - 366)));
    }

    private void OnVolumeButtonClick(object? sender, RoutedEventArgs e)
    {
        VolumeInlinePanel.IsOpen = !VolumeInlinePanel.IsOpen;
        e.Handled = true;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(PointerPressedEvent, OnTopLevelPointerPressed, RoutingStrategies.Tunnel);
        _topLevel?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(PointerPressedEvent, OnTopLevelPointerPressed);
        _topLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        _topLevel = null;
        VolumeInlinePanel.IsOpen = false;
    }

    private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (VolumeInlinePanel.IsOpen && e.Source is Visual source &&
            !VolumeButton.IsVisualAncestorOf(source) && source != VolumeButton &&
            !VolumeInlinePanel.IsVisualAncestorOf(source) && source != VolumeInlinePanel)
            VolumeInlinePanel.IsOpen = false;
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !VolumeInlinePanel.IsOpen) return;
        VolumeInlinePanel.IsOpen = false;
        e.Handled = true;
    }

    private void OnSeekStarted(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is PlayerBarViewModel vm) vm.IsUserSeeking = true;
    }

    private async void OnSeekCompleted(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not PlayerBarViewModel vm) return;
        vm.IsUserSeeking = false;
        await vm.SeekCommand.ExecuteAsync(ProgressSlider.Value);
    }

    private async void OnSeekKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown
            && DataContext is PlayerBarViewModel vm)
            await vm.SeekCommand.ExecuteAsync(ProgressSlider.Value);
    }
}
