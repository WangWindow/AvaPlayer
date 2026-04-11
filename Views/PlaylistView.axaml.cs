using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AvaPlayer.ViewModels;

namespace AvaPlayer.Views;

public partial class PlaylistView : UserControl
{
    public PlaylistView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is PlaylistViewModel viewModel)
        {
            viewModel.FolderPickRequested = PickFolderAsync;
        }
    }

    private async Task<IStorageFolder?> PickFolderAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return null;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择音乐文件夹",
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0] : null;
    }
}
