using Avalonia.Controls;
using Avalonia.Input;
using AvaPlayer.ViewModels;

namespace AvaPlayer.Views;

public partial class PlayerBarView : UserControl
{
    public PlayerBarView()
    {
        InitializeComponent();

        ProgressSlider.AddHandler(PointerPressedEvent, OnSliderPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ProgressSlider.AddHandler(PointerReleasedEvent, OnSliderPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
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
}
