using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Hartonomous.Cli.Bootstrap;

/// <summary>
/// Resolves canonical schema <c>-- @include</c> directives from
/// <c>sql/schema/bootstrap.sql</c>.
/// </summary>
internal static class BootstrapSqlLoader
{
    private static readonly Regex IncludeDirective = new(
        @"^\s*--\s*@include\s+(?<path>\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static string LoadResolved(string manifestPath)
    {
        string sqlRoot = FindSqlRoot(manifestPath);
        return ExpandIncludes(manifestPath, sqlRoot);
    }

    private static string FindSqlRoot(string anyFilePath)
    {
        DirectoryInfo? dir = new FileInfo(anyFilePath).Directory;
        while (dir is not null && !string.Equals(dir.Name, "sql", StringComparison.Ordinal))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException(
                $"Could not find 'sql' root directory walking up from {anyFilePath}");
        }

        return dir.FullName;
    }

    private static string ExpandIncludes(string filePath, string sqlRoot)
    {
        string content = File.ReadAllText(filePath);
        return IncludeDirective.Replace(content, match =>
        {
            string includePath = match.Groups["path"].Value
                .Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.Combine(sqlRoot, includePath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException(
                    $"@include not found: '{includePath}' (resolved to '{fullPath}') referenced from '{filePath}'");
            }

            return ExpandIncludes(fullPath, sqlRoot);
        });
    }
}