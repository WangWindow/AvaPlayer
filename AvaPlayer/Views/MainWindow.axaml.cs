using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaPlayer.ViewModels;

namespace AvaPlayer.Views;

public partial class MainWindow : Window
{
    private SettingsViewModel? _settings;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += (_, _) => DetachSettings();
        WindowDecorations = WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = true;
        KeyDown += OnWindowKeyDown;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty) UpdateMaximizeRestoreIcon();
        if (change.Property == WindowStateProperty || change.Property == ActualTransparencyLevelProperty)
            ApplyCornerPreference();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachSettings();
        _settings = (DataContext as MainWindowViewModel)?.Settings;
        if (_settings is not null) _settings.PropertyChanged += OnSettingsChanged;
        ApplyCornerPreference();
    }

    private void DetachSettings()
    {
        if (_settings is not null) _settings.PropertyChanged -= OnSettingsChanged;
        _settings = null;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.UseRoundedWindowCorners)) ApplyCornerPreference();
    }

    private void ApplyCornerPreference()
    {
        var rounded = _settings?.UseRoundedWindowCorners != false;
        Win32Properties.SetWindowCornerPreference(this, rounded
            ? Win32Properties.WindowCornerPreference.Round
            : Win32Properties.WindowCornerPreference.DoNotRound);
        var normal = WindowState == WindowState.Normal;
        if (WindowSurface is not null)
            WindowSurface.CornerRadius = new CornerRadius(rounded && normal &&
                ActualTransparencyLevel != WindowTransparencyLevel.None ? 12 : 0);
        if (ResizeGrips is not null) ResizeGrips.IsVisible = normal && CanResize;
    }

    private void OnResizeGripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (CanResize && WindowState == WindowState.Normal &&
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            sender is Control { Tag: string name } && Enum.TryParse<WindowEdge>(name, out var edge))
        {
            BeginResizeDrag(edge, e);
            e.Handled = true;
        }
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_settings is null) return;
        var size = await _settings.LoadWindowSizeAsync();
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var available = screen?.WorkingArea.Size.ToSize(screen.Scaling) ?? new Size(1280, 900);
        MinWidth = Math.Min(MinWidth, available.Width);
        MinHeight = Math.Min(MinHeight, available.Height);
        Width = Math.Clamp(size.Width, MinWidth, available.Width);
        Height = Math.Clamp(size.Height, MinHeight, available.Height);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_settings is not null && WindowState == WindowState.Normal)
            _ = _settings.SaveWindowSizeAsync(Bounds.Size);
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not MainWindowViewModel vm) return;
        if (e.Key == Key.Escape)
        {
            if (vm.IsPlaylistVisible) vm.ClosePlaylistCommand.Execute(null);
            else if (vm.PlayerBar.IsSettingsVisible) vm.PlayerBar.ToggleSettingsCommand.Execute(null);
            else return;
            e.Handled = true;
            return;
        }
        // Keep normal typing, slider navigation and button activation available.
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.P)
        {
            await vm.PlayerBar.PlayPauseCommand.ExecuteAsync(null);
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Right)
        {
            await vm.PlayerBar.NextCommand.ExecuteAsync(null);
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Left)
        {
            await vm.PlayerBar.PreviousCommand.ExecuteAsync(null);
            e.Handled = true;
        }
    }

    private void OnOverlayDismissPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ClosePlaylistCommand.Execute(null);
        }
    }

    // ── Custom titlebar handlers ──

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
        else if (e.ClickCount == 1 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeRestoreClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateMaximizeRestoreIcon()
    {
        if (MaximizeRestoreIcon is null)
            return;

        MaximizeRestoreIcon.Icon = WindowState == WindowState.Maximized
            ? FluentIcons.Common.Icon.FullScreenMinimize
            : FluentIcons.Common.Icon.FullScreenMaximize;
    }
}
