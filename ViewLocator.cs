using Avalonia.Controls;
using Avalonia.Controls.Templates;
using AvaPlayer.ViewModels;
using AvaPlayer.Views;

namespace AvaPlayer;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// </summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        MainWindowViewModel => new MainWindow(),
        _ => new TextBlock { Text = $"Not found: {param?.GetType().FullName}" }
    };

    public bool Match(object? data) => data is ViewModelBase;
}
