using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Hartonomous.Decomposers.WordNet;

internal static class WordNetParser
{
    private static readonly char[] SpaceSep = [' '];

    public static List<SynsetRecord> ParseDataFile(string path)
    {
        List<SynsetRecord> records = new(82000);

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == ' ')
            {
                continue;
            }

            SynsetRecord? rec = ParseDataLine(line);
            if (rec is not null)
            {
                records.Add(rec);
            }
        }

        return records;
    }

    private static SynsetRecord? ParseDataLine(string line)
    {
        int glossIdx = line.IndexOf("| ", StringComparison.Ordinal);
        string gloss = glossIdx >= 0 ? line[(glossIdx + 2)..].Trim() : "";
        string data = glossIdx >= 0 ? line[..glossIdx] : line;

        string[] tokens = data.Split(SpaceSep, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 6)
        {
            return null;
        }

        int i = 0;
        int offset = int.Parse(tokens[i++], CultureInfo.InvariantCulture);
        int lexFileNum = int.Parse(tokens[i++], CultureInfo.InvariantCulture);
        char ssType = tokens[i++][0];
        int wordCount = int.Parse(tokens[i++], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        List<SynsetWord> words = new(wordCount);
        for (int w = 0; w < wordCount; w++)
        {
            string word = tokens[i++];
            int lexId = int.Parse(tokens[i++], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            words.Add(new SynsetWord(word, lexId));
        }

        int ptrCount = int.Parse(tokens[i++], CultureInfo.InvariantCulture);
        List<PointerRecord> pointers = new(ptrCount);
        for (int p = 0; p < ptrCount; p++)
        {
            string symbol = tokens[i++];
            int targetOffset = int.Parse(tokens[i++], CultureInfo.InvariantCulture);
            char targetPos = tokens[i++][0];
            string sourceTarget = tokens[i++];
            int srcWord = int.Parse(sourceTarget[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int tgtWord = int.Parse(sourceTarget[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            pointers.Add(new PointerRecord(symbol, targetOffset, targetPos, srcWord, tgtWord));
        }

        List<FrameRef> frames = [];
        if (i < tokens.Length && tokens[i] == "+")
        {
            i++;
            if (i < tokens.Length)
            {
                int frameCount = int.Parse(tokens[i++], CultureInfo.InvariantCulture);
                for (int f = 0; f < frameCount; f++)
                {
                    i++; // skip "+"
                    int frameNum = int.Parse(tokens[i++], CultureInfo.InvariantCulture);
                    int frameWordNum = int.Parse(tokens[i++], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    frames.Add(new FrameRef(frameNum, frameWordNum));
                }
            }
        }

        return new SynsetRecord(offset, lexFileNum, ssType, words, pointers, frames, gloss);
    }

    public static List<SenseIndexEntry> ParseSenseIndex(string path)
    {
        List<SenseIndexEntry> entries = new(207000);

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == ' ')
            {
                continue;
            }

            string[] parts = line.Split(SpaceSep, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                continue;
            }

            entries.Add(new SenseIndexEntry(
                SenseKey: parts[0],
                SynsetOffset: int.Parse(parts[1], CultureInfo.InvariantCulture),
                SenseNumber: int.Parse(parts[2], CultureInfo.InvariantCulture),
                TagCount: int.Parse(parts[3], CultureInfo.InvariantCulture)));
        }

        return entries;
    }

    public static List<MorphException> ParseExceptionFile(string path, char pos)
    {
        List<MorphException> exceptions = new(2500);

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Split(SpaceSep, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            string inflected = parts[0];
            List<string> bases = new(parts.Length - 1);
            for (int i = 1; i < parts.Length; i++)
            {
                bases.Add(parts[i]);
            }

            exceptions.Add(new MorphException(inflected, bases, pos));
        }

        return exceptions;
    }

    public static List<VerbSentence> ParseSentences(string path)
    {
        List<VerbSentence> sentences = new(170);

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            int spaceIdx = line.IndexOf(' ');
            if (spaceIdx < 0)
            {
                continue;
            }

            if (!int.TryParse(line.AsSpan(0, spaceIdx), CultureInfo.InvariantCulture, out int id))
            {
                continue;
            }

            sentences.Add(new VerbSentence(id, line[(spaceIdx + 1)..]));
        }

        return sentences;
    }

    public static List<VerbSentenceIndex> ParseSentenceIndex(string path)
    {
        List<VerbSentenceIndex> index = new(10000);

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Split(SpaceSep, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            string senseKey = parts[0];
            string[] idStrs = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
            List<int> ids = new(idStrs.Length);
            foreach (string idStr in idStrs)
            {
                if (int.TryParse(idStr.Trim(), CultureInfo.InvariantCulture, out int id))
                {
                    ids.Add(id);
                }
            }

            index.Add(new VerbSentenceIndex(senseKey, ids));
        }

        return index;
    }

    public static string PointerSymbolToRelation(string symbol) => symbol switch
    {
        "!" => "antonym",
        "@" => "hypernym",
        "@i" => "instance_hypernym",
        "~" => "hyponym",
        "~i" => "instance_hyponym",
        "#m" => "member_holonym",
        "#s" => "substance_holonym",
        "#p" => "part_holonym",
        "%m" => "member_meronym",
        "%s" => "substance_meronym",
        "%p" => "part_meronym",
        "=" => "attribute",
        "+" => "derivationally_related",
        ";c" => "domain_of_synset_topic",
        "-c" => "member_of_domain_topic",
        ";r" => "domain_of_synset_region",
        "-r" => "member_of_domain_region",
        ";u" => "domain_of_synset_usage",
        "-u" => "member_of_domain_usage",
        "*" => "entailment",
        ">" => "cause",
        "^" => "also_see",
        "$" => "verb_group",
        "&" => "similar_to",
        "<" => "participle_of_verb",
        @"\" => "pertainym",
        _ => "unknown_" + symbol,
    };

    public static char SsTypeToPos(char ssType) => ssType switch
    {
        'n' => 'n',
        'v' => 'v',
        'a' or 's' => 'a',
        'r' => 'r',
        _ => ssType,
    };

    /// <summary>
    /// Splits a WordNet gloss string into (definition, examples).
    /// Format: "definition text; \"example one\"; \"example two\""
    /// Examples are delimited by "; \"" and end with "\"".
    /// </summary>
    public static (string Definition, List<string> Examples) ParseGloss(string gloss)
    {
        // Find the first occurrence of "; \"" which marks the boundary between
        // definition and examples.
        int exampleStart = gloss.IndexOf("; \"", StringComparison.Ordinal);
        if (exampleStart < 0)
        {
            return (gloss.Trim(), []);
        }

        string definition = gloss[..exampleStart].Trim();
        string remainder = gloss[(exampleStart + 2)..]; // skip "; "

        List<string> examples = [];
        // Split on "; " to get individual quoted examples.
        foreach (string part in remainder.Split("\"; \"", StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = part.Trim().Trim('"').Trim();
            if (trimmed.Length > 0)
            {
                examples.Add(trimmed);
            }
        }

        return (definition, examples);
    }

    public static string PosCharToUdPos(char pos) => pos switch
    {
        'n' => "NOUN",
        'v' => "VERB",
        'a' or 's' => "ADJ",
        'r' => "ADV",
        _ => "X",
    };
}
