using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Hartonomous.Decomposers.Tatoeba;

internal sealed class TatoebaAudioIndex : IDisposable
{
    private readonly Dictionary<int, AudioSource> _sources = new();
    private readonly List<ZipArchive> _archives = [];

    public int Count => _sources.Count;

    public static TatoebaAudioIndex Build(string audioRoot)
    {
        TatoebaAudioIndex index = new();
        if (!Directory.Exists(audioRoot))
        {
            return index;
        }

        foreach (string path in Directory.EnumerateFiles(audioRoot, "*.mp3", SearchOption.AllDirectories))
        {
            if (TryParseAudioId(path, out int audioId))
            {
                index.Add(audioId, AudioSource.FromFile(path));
            }
        }

        foreach (string archivePath in Directory.EnumerateFiles(audioRoot, "*.zip", SearchOption.AllDirectories))
        {
            ZipArchive archive = ZipFile.OpenRead(archivePath);
            index._archives.Add(archive);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || entry.Length == 0)
                {
                    continue;
                }

                if (TryParseAudioId(entry.FullName, out int audioId))
                {
                    index.Add(audioId, AudioSource.FromZipEntry(archivePath, entry));
                }
            }
        }

        return index;
    }

    public Stream? OpenRead(int audioId)
    {
        return _sources.TryGetValue(audioId, out AudioSource? source)
            ? source.OpenRead()
            : null;
    }

    public void Dispose()
    {
        foreach (ZipArchive archive in _archives)
        {
            archive.Dispose();
        }
    }

    private void Add(int audioId, AudioSource source)
    {
        if (!_sources.TryGetValue(audioId, out AudioSource? existing)
            || string.CompareOrdinal(source.SortKey, existing.SortKey) < 0)
        {
            _sources[audioId] = source;
        }
    }

    private static bool TryParseAudioId(string path, out int audioId)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        return int.TryParse(stem, out audioId);
    }

    private sealed record AudioSource(string SortKey, string? FilePath, ZipArchiveEntry? ZipEntry)
    {
        public static AudioSource FromFile(string path) => new($"0:{path}", path, null);

        public static AudioSource FromZipEntry(string archivePath, ZipArchiveEntry entry) =>
            new($"1:{archivePath}:{entry.FullName}", null, entry);

        public Stream OpenRead()
        {
            return FilePath is not null
                ? File.OpenRead(FilePath)
                : ZipEntry!.Open();
        }
    }
}
