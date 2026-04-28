using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Cli.Migrations;

internal sealed record Migration(int Version, string Name, string UpPath, string DownPath)
{
    private static readonly Regex FileName = new(
        @"^(?<version>\d{4})_(?<name>.+)\.(?<dir>up|down)\.sql$",
        RegexOptions.Compiled);

    public string ReadUp() => MigrationFileLoader.LoadResolved(UpPath);

    public string ReadDown() => MigrationFileLoader.LoadResolved(DownPath);

    public string UpChecksum()
    {
        // Checksum the EXPANDED content so changes to any included schema/* file
        // trigger drift detection — without this, schema edits would silently
        // diverge from applied substrate state. BLAKE3 is the substrate's only
        // hash function; SHA-256 has no business being in this codebase.
        byte[] hash = Blake3.Hash(Encoding.UTF8.GetBytes(ReadUp()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static Migration? TryParse(string directory, string fileName, IReadOnlyDictionary<int, string> downPathsByVersion)
    {
        Match m = FileName.Match(fileName);
        if (!m.Success || m.Groups["dir"].Value != "up")
        {
            return null;
        }
        int version = int.Parse(m.Groups["version"].Value, System.Globalization.CultureInfo.InvariantCulture);
        string name = m.Groups["name"].Value;
        string upPath = Path.Combine(directory, fileName);
        if (!downPathsByVersion.TryGetValue(version, out string? downPath))
        {
            throw new InvalidOperationException($"Migration {version:D4} '{name}' is missing its .down.sql pair.");
        }
        return new Migration(version, name, upPath, downPath);
    }

    public static IReadOnlyList<Migration> Discover(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Migration directory not found: {directory}");
        }

        Dictionary<int, string> downPathsByVersion = new();
        foreach (string file in Directory.EnumerateFiles(directory, "*.down.sql"))
        {
            Match m = FileName.Match(Path.GetFileName(file));
            if (m.Success)
            {
                int version = int.Parse(m.Groups["version"].Value, System.Globalization.CultureInfo.InvariantCulture);
                downPathsByVersion[version] = file;
            }
        }

        List<Migration> migrations = new();
        foreach (string file in Directory.EnumerateFiles(directory, "*.up.sql"))
        {
            Migration? mig = TryParse(directory, Path.GetFileName(file), downPathsByVersion);
            if (mig is not null)
            {
                migrations.Add(mig);
            }
        }
        migrations.Sort((a, b) => a.Version.CompareTo(b.Version));

        for (int i = 0; i < migrations.Count; i++)
        {
            int expected = i + 1;
            if (migrations[i].Version != expected)
            {
                throw new InvalidOperationException(
                    $"Migration numbering gap: expected {expected:D4}, found {migrations[i].Version:D4} ({migrations[i].Name}).");
            }
        }

        return migrations;
    }
}
