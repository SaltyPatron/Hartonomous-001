using System;
using System.IO;

namespace Hartonomous.Cli.Configuration;

internal static class CliPathResolver
{
    public static string Resolve(string dataRoot, string configuredPath)
    {
        string normalizedRoot = NormalizeSeparators(dataRoot);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return normalizedRoot;
        }

        string normalizedPath = NormalizeSeparators(configuredPath);
        return Path.IsPathRooted(normalizedPath)
            ? normalizedPath
            : Path.Combine(normalizedRoot, normalizedPath);
    }

    private static string NormalizeSeparators(string path)
    {
        return OperatingSystem.IsWindows()
            ? path.Replace('/', Path.DirectorySeparatorChar)
            : path.Replace('\\', Path.DirectorySeparatorChar);
    }
}
