using System.Security.Cryptography;
using System.Text;

namespace AvaPlayer.Helpers;

public static class StableId
{
    public static string ForPath(string path)
    {
        var normalizedPath = FilePaths.Normalize(path);
        if (OperatingSystem.IsWindows()) normalizedPath = normalizedPath.ToLowerInvariant();
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        return Convert.ToHexString(bytes);
    }
}
