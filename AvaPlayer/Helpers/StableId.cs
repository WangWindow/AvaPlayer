using System.Security.Cryptography;
using System.Text;

namespace AvaPlayer.Helpers;

public static class StableId
{
    public static string ForPath(string path)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return Convert.ToHexString(bytes);
    }
}
