using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using AvaPlayer.ViewModels;

namespace AvaPlayer.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _viewModel;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unsubscribe();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        Subscribe();
    }

    private void Subscribe()
    {
        if (_viewModel is not null || DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void Unsubscribe()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Reset the scroll position whenever the page opens. The title lives
        // outside the ScrollViewer, so only the content moves.
        if (e.PropertyName == nameof(SettingsViewModel.IsSettingsVisible) &&
            _viewModel?.IsSettingsVisible == true)
        {
            SettingsScroll.Offset = default;
        }
    }
}
