namespace AvaPlayer.Services.Cache;

public interface ICacheService
{
    string RootPath { get; }
    string GetCategoryPath(string category);
    string GetFilePath(string category, string fileName);
    Task ClearAsync(CancellationToken cancellationToken = default);
    Task ClearCategoryAsync(string category, CancellationToken cancellationToken = default);
}
