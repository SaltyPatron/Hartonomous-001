using System;
using System.Collections.Generic;
using System.IO;

namespace Hartonomous.Decomposers.Wiktionary;

/// <summary>
/// Streaming reader variant with byte-progress counters for large JSONL dumps.
/// </summary>
internal sealed class WiktionaryJsonlStreamingReader : IEnumerable<WiktEntry>, IDisposable
{
    private readonly FileStream _fs;
    private readonly StreamReader _sr;
    private readonly string[]? _prefilter;

    public long TotalBytes { get; }

    public long BytesRead => _fs.Position;

    public long EntriesParsed { get; private set; }

    public WiktionaryJsonlStreamingReader(string path, IReadOnlyCollection<string>? langCodeFilter)
    {
        _fs = File.OpenRead(path);
        _sr = new StreamReader(_fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20);
        _prefilter = WiktionaryJsonlParser.BuildPrefilter(langCodeFilter);
        TotalBytes = _fs.Length;
    }

    public IEnumerator<WiktEntry> GetEnumerator()
    {
        string? line;
        while ((line = _sr.ReadLine()) is not null)
        {
            if (_prefilter is not null && !WiktionaryJsonlParser.LineCarriesAllowedLangCode(line, _prefilter))
            {
                continue;
            }
            WiktEntry? entry = WiktionaryJsonlParser.ParseLine(line);
            if (entry is null)
            {
                continue;
            }
            EntriesParsed++;
            yield return entry;
        }
    }

    public IEnumerable<WiktionaryJsonlLineChunk> ReadChunks(int targetLinesPerChunk)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetLinesPerChunk, 1);

        List<string> lines = new(targetLinesPerChunk);
        long chunkIndex = 0;
        string? line;
        while ((line = _sr.ReadLine()) is not null)
        {
            if (_prefilter is not null && !WiktionaryJsonlParser.LineCarriesAllowedLangCode(line, _prefilter))
            {
                continue;
            }

            lines.Add(line);
            if (lines.Count >= targetLinesPerChunk)
            {
                yield return new WiktionaryJsonlLineChunk(chunkIndex++, lines, _fs.Position, TotalBytes);
                lines = new List<string>(targetLinesPerChunk);
            }
        }

        if (lines.Count > 0)
        {
            yield return new WiktionaryJsonlLineChunk(chunkIndex, lines, _fs.Position, TotalBytes);
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        _sr.Dispose();
        _fs.Dispose();
    }
}
