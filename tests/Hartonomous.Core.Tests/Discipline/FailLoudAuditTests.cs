using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Hartonomous.Core.Tests.Discipline;

/// <summary>
/// Fail-loud standard: no catch block silently swallows an exception.
/// Every catch must either rethrow, throw a new contextual exception, or be
/// explicitly marked as a substrate boundary via the // BOUNDARY: &lt;reason&gt;
/// comment on the line containing the catch keyword.
/// </summary>
public sealed class FailLoudAuditTests
{
    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Hartonomous.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("Hartonomous.slnx not found walking up from test output");
    }

    [Fact]
    public void EveryCatchInSrc_RethrowsOrIsMarkedBoundary()
    {
        string src = Path.Combine(RepoRoot(), "src");
        Assert.True(Directory.Exists(src), $"src directory missing: {src}");

        List<string> violations = new();

        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                Match m = Regex.Match(line, @"(^|\s)catch\s*(\(|\{)");
                if (!m.Success)
                {
                    continue;
                }

                if (line.Contains("// BOUNDARY:", StringComparison.Ordinal))
                {
                    continue;
                }

                string bodySegment = SliceCatchBody(lines, i);
                if (Regex.IsMatch(bodySegment, @"\bthrow\b"))
                {
                    continue;
                }

                violations.Add($"{file}:{i + 1}: catch without rethrow or // BOUNDARY: marker");
            }
        }

        Assert.True(violations.Count == 0, "Fail-loud violations:\n" + string.Join("\n", violations));
    }

    private static string SliceCatchBody(string[] lines, int catchLineIdx)
    {
        int depth = 0;
        bool seenOpenBrace = false;
        System.Text.StringBuilder sb = new();
        for (int i = catchLineIdx; i < lines.Length; i++)
        {
            string line = lines[i];
            foreach (char c in line)
            {
                if (c == '{')
                {
                    depth++;
                    seenOpenBrace = true;
                }
                else if (c == '}')
                {
                    depth--;
                    if (seenOpenBrace && depth == 0)
                    {
                        return sb.ToString();
                    }
                }
            }
            sb.Append(line).Append('\n');
            if (seenOpenBrace && depth == 0)
            {
                return sb.ToString();
            }
        }
        return sb.ToString();
    }
}
