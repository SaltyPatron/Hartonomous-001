using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Hartonomous.Decomposers.Tatoeba;

/// <summary>
/// Streaming tab-separated readers for the three Tatoeba export files. Each call enumerates
/// one record per input line in bounded memory (<see cref="File.ReadLines(string)"/>).
/// <list type="bullet">
///   <item>Blank and whitespace-only lines are skipped.</item>
///   <item>Rows with fewer than the expected columns or non-integer IDs are skipped
///     silently — Tatoeba exports occasionally carry malformed trailers, which are
///     uninteresting rather than fatal.</item>
///   <item>No header row is assumed (the Tatoeba exports have none).</item>
/// </list>
/// </summary>
internal static class TatoebaCsvReader
{
    public static IEnumerable<TatoebaSentenceRow> ReadSentences(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sid))
            {
                continue;
            }
            yield return new TatoebaSentenceRow(sid, parts[1], parts[2]);
        }
    }

    public static IEnumerable<TatoebaLinkRow> ReadLinks(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length < 2)
            {
                continue;
            }
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int src) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tgt))
            {
                continue;
            }
            yield return new TatoebaLinkRow(src, tgt);
        }
    }

    public static IEnumerable<TatoebaAudioRow> ReadAudio(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sid) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int aid))
            {
                continue;
            }
            yield return new TatoebaAudioRow(sid, aid, parts[2]);
        }
    }
}
