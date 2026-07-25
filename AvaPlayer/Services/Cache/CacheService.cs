namespace AvaPlayer.Services.Cache;

public sealed class CacheService : ICacheService
{
    public CacheService()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localData)
            ? AppContext.BaseDirectory
            : localData;

        RootPath = Path.Combine(root, "AvaPlayer", "cache");
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public string GetCategoryPath(string category)
    {
        var path = Path.Combine(RootPath, category);
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetFilePath(string category, string fileName) =>
        Path.Combine(GetCategoryPath(category), fileName);

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        foreach (var directory in Directory.EnumerateDirectories(RootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(directory, true);
        }

        foreach (var file in Directory.EnumerateFiles(RootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(file);
        }

        Directory.CreateDirectory(RootPath);
        return Task.CompletedTask;
    }

    public Task ClearCategoryAsync(string category, CancellationToken cancellationToken = default)
    {
        var path = GetCategoryPath(category);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }
}
