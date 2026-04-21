using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hartonomous.Decomposers.Ud;

internal static class UdTreebankScanner
{
    /// <summary>
    /// Enumerate every UD_{Language}-{Treebank} directory under <paramref name="rootDir"/>
    /// with its list of CoNLL-U files (train/dev/test splits). Directories without any
    /// .conllu files are skipped.
    /// </summary>
    public static List<UdTreebankInfo> Scan(string rootDir)
    {
        if (!Directory.Exists(rootDir))
        {
            throw new DirectoryNotFoundException($"UD root directory not found: {rootDir}");
        }

        List<UdTreebankInfo> banks = [];
        foreach (string dir in Directory.EnumerateDirectories(rootDir, "UD_*", SearchOption.TopDirectoryOnly))
        {
            string dirName = Path.GetFileName(dir);
            int dash = dirName.IndexOf('-', StringComparison.Ordinal);
            string languageName;
            string treebankName;
            if (dash > 3)
            {
                languageName = dirName[3..dash];
                treebankName = dirName[(dash + 1)..];
            }
            else
            {
                languageName = dirName[3..];
                treebankName = "";
            }

            string[] conlluFiles = Directory.GetFiles(dir, "*.conllu", SearchOption.TopDirectoryOnly);
            if (conlluFiles.Length == 0)
            {
                continue;
            }
            Array.Sort(conlluFiles, StringComparer.Ordinal);

            string? isoCode = ExtractIsoPrefix(conlluFiles[0]);
            banks.Add(new UdTreebankInfo(
                DirectoryName: dirName,
                TreebankName: treebankName,
                LanguageName: languageName,
                LanguageCode: isoCode,
                ConlluFiles: conlluFiles));
        }

        banks.Sort((a, b) => string.CompareOrdinal(a.DirectoryName, b.DirectoryName));
        return banks;
    }

    /// <summary>
    /// Pull the two/three-letter prefix before the first underscore in a filename like
    /// "en_ewt-ud-train.conllu" → "en". Caller maps to ISO 639-3 via the language table.
    /// </summary>
    private static string? ExtractIsoPrefix(string conlluFilePath)
    {
        string fileName = Path.GetFileNameWithoutExtension(conlluFilePath);
        int underscore = fileName.IndexOf('_', StringComparison.Ordinal);
        if (underscore <= 0)
        {
            return null;
        }
        string prefix = fileName[..underscore];
        return prefix.Length is 2 or 3 ? prefix : null;
    }
}
