using System;
using System.Collections.Generic;
using System.IO;

namespace Hartonomous.Decomposers.Ud;

internal static class UdConllUParser
{
    /// <summary>
    /// Stream parse a CoNLL-U file into sentence records. Blank lines separate sentences;
    /// comment lines ("#") carry metadata; '_' is the underscore-sentinel for "absent".
    /// MWT spans (ID "n-m") and empty nodes (ID "n.m") are preserved as-is in the ID
    /// field so downstream code can decide how to handle them.
    /// </summary>
    public static IEnumerable<UdSentenceRecord> Parse(string path)
    {
        List<UdTokenRecord> tokens = new(64);
        string? sentId = null;
        string? text = null;
        int ordinal = 0;

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                if (tokens.Count > 0)
                {
                    ordinal++;
                    yield return new UdSentenceRecord(
                        sentId ?? $"ord-{ordinal}",
                        text,
                        tokens);
                    tokens = new List<UdTokenRecord>(64);
                    sentId = null;
                    text = null;
                }
                continue;
            }

            if (line[0] == '#')
            {
                ParseComment(line, ref sentId, ref text);
                continue;
            }

            UdTokenRecord? tok = ParseTokenLine(line);
            if (tok is not null)
            {
                tokens.Add(tok);
            }
        }

        if (tokens.Count > 0)
        {
            ordinal++;
            yield return new UdSentenceRecord(
                sentId ?? $"ord-{ordinal}",
                text,
                tokens);
        }
    }

    private static void ParseComment(string line, ref string? sentId, ref string? text)
    {
        int eq = line.IndexOf('=');
        if (eq < 0)
        {
            return;
        }

        string key = line[1..eq].Trim();
        string value = line[(eq + 1)..].Trim();
        if (key.Equals("sent_id", StringComparison.Ordinal))
        {
            sentId = value;
        }
        else if (key.Equals("text", StringComparison.Ordinal))
        {
            text = value;
        }
    }

    private static UdTokenRecord? ParseTokenLine(string line)
    {
        string[] cols = line.Split('\t');
        if (cols.Length < 10)
        {
            return null;
        }

        string id = cols[0];
        string form = cols[1];
        string? lemma = NullIfUnderscore(cols[2]);
        string? upos = NullIfUnderscore(cols[3]);
        string? xpos = NullIfUnderscore(cols[4]);
        IReadOnlyList<UdMorphFeature> feats = ParseFeats(cols[5]);
        string? head = NullIfUnderscore(cols[6]);
        string? deprel = NullIfUnderscore(cols[7]);
        string? deps = NullIfUnderscore(cols[8]);
        string? misc = NullIfUnderscore(cols[9]);

        return new UdTokenRecord(id, form, lemma, upos, xpos, feats, head, deprel, deps, misc);
    }

    private static string? NullIfUnderscore(string v) =>
        v.Length == 1 && v[0] == '_' ? null : v;

    private static IReadOnlyList<UdMorphFeature> ParseFeats(string column)
    {
        if (column.Length == 1 && column[0] == '_')
        {
            return Array.Empty<UdMorphFeature>();
        }

        string[] parts = column.Split('|');
        List<UdMorphFeature> feats = new(parts.Length);
        foreach (string p in parts)
        {
            int eq = p.IndexOf('=');
            if (eq <= 0 || eq == p.Length - 1)
            {
                continue;
            }
            feats.Add(new UdMorphFeature(p[..eq], p[(eq + 1)..]));
        }
        return feats;
    }
}
