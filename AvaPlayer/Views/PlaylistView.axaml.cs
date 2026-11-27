using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Platform.Storage;
using System.ComponentModel;
using AvaPlayer.ViewModels;

namespace AvaPlayer.Views;

public partial class PlaylistView : UserControl
{
    private Popup? _selectorPopup;
    private PlayerBarViewModel? _playerBar;

    public PlaylistView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PlaylistSelector.Loaded += (_, _) => BindSelectorPopupBackground();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BindSelectorPopupBackground();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_playerBar is not null)
        {
            _playerBar.PropertyChanged -= OnPlayerBarPropertyChanged;
            _playerBar = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void BindSelectorPopupBackground()
    {
        PlaylistSelector.ApplyTemplate();
        _selectorPopup ??= PlaylistSelector.GetTemplateDescendants().OfType<Popup>().FirstOrDefault();
        if (_selectorPopup is null)
        {
            return;
        }

        _selectorPopup.Opened -= OnSelectorPopupOpened;
        _selectorPopup.Opened += OnSelectorPopupOpened;
        if (_selectorPopup.IsOpen)
        {
            ApplySelectorPopupBackground();
        }
    }

    private void OnSelectorPopupOpened(object? sender, EventArgs e) => ApplySelectorPopupBackground();

    private void ApplySelectorPopupBackground()
    {
        if (_selectorPopup?.Child is not Border border
            || TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel main)
        {
            return;
        }

        if (!ReferenceEquals(_playerBar, main.PlayerBar))
        {
            if (_playerBar is not null)
            {
                _playerBar.PropertyChanged -= OnPlayerBarPropertyChanged;
            }

            _playerBar = main.PlayerBar;
            _playerBar.PropertyChanged += OnPlayerBarPropertyChanged;
        }

        border.Background = _playerBar.BackgroundBrush;
    }

    private void OnPlayerBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerBarViewModel.BackgroundBrush)
            && _selectorPopup?.Child is Border border)
        {
            border.Background = _playerBar?.BackgroundBrush;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is PlaylistViewModel viewModel)
        {
            viewModel.FolderPickRequested = PickFolderAsync;
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return null;
        }

        if (!topLevel.StorageProvider.CanPickFolder)
        {
            Console.Error.WriteLine("[Playlist] 当前平台不支持文件夹选择。");
            return null;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择音乐文件夹",
            AllowMultiple = false
        });

        if (folders.Count == 0)
        {
            return null;
        }

        var folderPath = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            Console.Error.WriteLine($"[Playlist] 选择的文件夹无法映射为本地路径: {folders[0].Path}");
        }

        return folderPath;
    }
}
