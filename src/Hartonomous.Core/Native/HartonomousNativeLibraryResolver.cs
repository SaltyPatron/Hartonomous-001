using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace Hartonomous.Core.Native;

internal static class HartonomousNativeLibraryResolver
{
    private const string LibraryName = "hartonomous";
    private static int s_registered;

    internal static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref s_registered, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(HartonomousNativeLibraryResolver).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        foreach (string candidate in EnumerateCandidatePaths(assembly))
        {
            if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private static string[] EnumerateCandidatePaths(Assembly assembly)
    {
        string fileName = GetPlatformLibraryFileName();
        string? assemblyDirectory = Path.GetDirectoryName(assembly.Location);
        string baseDirectory = AppContext.BaseDirectory;

        if (string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            return new[]
            {
                Path.Combine(baseDirectory, fileName)
            };
        }

        string? repoRoot = FindRepoRoot(baseDirectory) ?? FindRepoRoot(assemblyDirectory);
        if (repoRoot is null)
        {
            return new[]
            {
                Path.Combine(baseDirectory, fileName),
                Path.Combine(assemblyDirectory, fileName)
            };
        }

        return new[]
        {
            Path.Combine(baseDirectory, fileName),
            Path.Combine(assemblyDirectory, fileName),
            Path.Combine(repoRoot, "ext", "libhartonomous", "build", "bin", fileName),
            Path.Combine(repoRoot, "ext", "libhartonomous", "build", fileName)
        };
    }

    private static string GetPlatformLibraryFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "hartonomous.dll";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "libhartonomous.dylib";
        }

        return "libhartonomous.so";
    }

    private static string? FindRepoRoot(string startDirectory)
    {
        DirectoryInfo? current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Hartonomous.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
