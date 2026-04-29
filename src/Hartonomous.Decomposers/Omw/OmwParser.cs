using System;
using System.Collections.Generic;
using System.IO;

namespace Hartonomous.Decomposers.Omw;

internal static class OmwParser
{
    public static List<OmwTabEntry> ParseTabFile(string path)
    {
        List<OmwTabEntry> entries = new(50_000);

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            string synsetCode = parts[0];
            string langRelation = parts[1];
            string word = parts[2];

            // Skip rows where the word column is empty or whitespace-only.
            // Real OMW data has these (e.g. wn-wikt-eng.tab has 56 rows where
            // the Wiktionary extraction couldn't pull a lemma — synset code
            // and "eng:lemma" present, word empty). They're not valid lemma
            // entries; downstream substrate emission would be a no-op at
            // best, a degenerate-physicality crash at worst.
            if (string.IsNullOrWhiteSpace(word))
            {
                continue;
            }

            int colonIdx = langRelation.IndexOf(':');
            if (colonIdx < 0)
            {
                continue;
            }

            string langCode = langRelation[..colonIdx];
            string relation = langRelation[(colonIdx + 1)..];

            entries.Add(new OmwTabEntry(synsetCode, langCode, relation, word));
        }

        return entries;
    }

    public static List<OmwSourceInfo> DiscoverTabFiles(string wnsDir)
    {
        List<OmwSourceInfo> sources = [];

        if (!Directory.Exists(wnsDir))
        {
            return sources;
        }

        foreach (string dir in Directory.GetDirectories(wnsDir))
        {
            string dirName = Path.GetFileName(dir);

            if (dirName is "cldr")
            {
                foreach (string f in Directory.GetFiles(dir, "wn-cldr-*.tab"))
                {
                    string lang = ExtractLangFromFilename(f, "wn-cldr-");
                    sources.Add(new OmwSourceInfo(f, lang, OmwSourceTier.Cldr));
                }
            }
            else if (dirName is "wikt")
            {
                foreach (string f in Directory.GetFiles(dir, "wn-wikt-*.tab"))
                {
                    string lang = ExtractLangFromFilename(f, "wn-wikt-");
                    sources.Add(new OmwSourceInfo(f, lang, OmwSourceTier.Wiktionary));
                }
            }
            else if (dirName is "en")
            {
                continue;
            }
            else
            {
                foreach (string f in Directory.GetFiles(dir, "wn-data-*.tab"))
                {
                    string lang = ExtractLangFromFilename(f, "wn-data-");
                    sources.Add(new OmwSourceInfo(f, lang, OmwSourceTier.Curated));
                }
            }
        }

        return sources;
    }

    private static string ExtractLangFromFilename(string path, string prefix)
    {
        string filename = Path.GetFileNameWithoutExtension(path);
        return filename.StartsWith(prefix, StringComparison.Ordinal)
            ? filename[prefix.Length..]
            : filename;
    }
}
