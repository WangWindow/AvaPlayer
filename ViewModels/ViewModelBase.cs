using CommunityToolkit.Mvvm.ComponentModel;

namespace AvaPlayer.ViewModels;

public abstract class ViewModelBase : ObservableObject, IDisposable
{
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Dispose(true);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}
