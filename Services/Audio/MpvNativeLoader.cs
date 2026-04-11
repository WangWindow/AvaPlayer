using System.Reflection;
using System.Runtime.InteropServices;

namespace AvaPlayer.Services.Audio;

public static class MpvNativeLoader
{
    private static int _configured;

    public static void Configure()
    {
        if (Interlocked.Exchange(ref _configured, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(MpvInterop).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, MpvInterop.LibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in GetCandidatePaths())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        foreach (var systemName in GetSystemNames())
        {
            if (NativeLibrary.TryLoad(systemName, assembly, searchPath, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> GetCandidatePaths()
    {
        var baseDirectory = AppContext.BaseDirectory;
        foreach (var fileName in GetSystemNames())
        {
            yield return Path.Combine(baseDirectory, fileName);

            var runtimePath = GetRuntimePath(fileName);
            if (!string.IsNullOrWhiteSpace(runtimePath))
            {
                yield return Path.Combine(baseDirectory, "runtimes", runtimePath, "native", fileName);
            }
        }
    }

    private static string[] GetSystemNames() =>
        OperatingSystem.IsWindows()
            ? ["libmpv-2.dll"]
            : OperatingSystem.IsMacOS()
                ? ["libmpv.2.dylib", "libmpv.dylib"]
                : ["libmpv.so.2", "libmpv.so"];

    private static string? GetRuntimePath(string _)
    {
        if (OperatingSystem.IsWindows())
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "win-x64" : null;
        }

        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "linux-x64" : null;
        }

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "osx-arm64",
                Architecture.X64 => "osx-x64",
                _ => null
            };
        }

        return null;
    }
}
