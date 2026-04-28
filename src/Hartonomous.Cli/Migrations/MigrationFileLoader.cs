using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Hartonomous.Cli.Migrations;

/// <summary>
/// Resolves <c>-- @include path/relative/to/sql/root.sql</c> directives inside
/// migration .up.sql / .down.sql files. Lets a migration stage file act as a
/// thin manifest that pulls together one-object-per-file schema sources from
/// <c>sql/schema/...</c>, while keeping schema/* as the single source of truth.
/// Includes are resolved recursively so a migration can include a "table-set"
/// file that itself includes individual table files.
/// </summary>
internal static class MigrationFileLoader
{
    private static readonly Regex IncludeDirective = new(
        @"^\s*--\s*@include\s+(?<path>\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static string LoadResolved(string filePath)
    {
        string sqlRoot = FindSqlRoot(filePath);
        return ExpandIncludes(filePath, sqlRoot);
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
