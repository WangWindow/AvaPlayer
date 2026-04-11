using Avalonia.Controls;
using Avalonia.Input;
using AvaPlayer.ViewModels;

namespace AvaPlayer.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnWindowKeyDown;
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                viewModel.PlayerBar.PlayPauseCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Left:
                await viewModel.PlayerBar.PreviousCommand.ExecuteAsync(null);
                e.Handled = true;
                break;

            case Key.Right:
                await viewModel.PlayerBar.NextCommand.ExecuteAsync(null);
                e.Handled = true;
                break;
        }
    }

    private void OnOverlayDismissPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ClosePlaylistCommand.Execute(null);
        }
    }
}
