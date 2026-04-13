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
