using System.Collections.Generic;
using System.IO;

namespace Hartonomous.Decomposers.Iso639;

internal static class Iso639Parser
{
    public static List<Iso639Record> ParseLanguages(string path)
    {
        List<Iso639Record> records = new(8000);
        bool header = true;

        foreach (string line in File.ReadLines(path))
        {
            if (header)
            {
                header = false;
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            string[] fields = line.Split('\t');
            if (fields.Length < 7)
            {
                continue;
            }

            records.Add(new Iso639Record(
                Id: fields[0],
                Part2b: fields[1].Length > 0 ? fields[1] : null,
                Part2t: fields[2].Length > 0 ? fields[2] : null,
                Part1: fields[3].Length > 0 ? fields[3] : null,
                Scope: fields[4].Length > 0 ? fields[4][0] : 'I',
                LanguageType: fields[5].Length > 0 ? fields[5][0] : 'L',
                RefName: fields[6]));
        }

        return records;
    }

    public static List<MacrolanguageMapping> ParseMacrolanguages(string path)
    {
        List<MacrolanguageMapping> mappings = new(500);
        bool header = true;

        foreach (string line in File.ReadLines(path))
        {
            if (header)
            {
                header = false;
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            string[] fields = line.Split('\t');
            if (fields.Length < 3)
            {
                continue;
            }

            mappings.Add(new MacrolanguageMapping(
                MacrolanguageId: fields[0],
                IndividualId: fields[1],
                Status: fields[2].Length > 0 ? fields[2][0] : 'A'));
        }

        return mappings;
    }

    public static List<NameIndexEntry> ParseNameIndex(string path)
    {
        List<NameIndexEntry> entries = new(9000);
        bool header = true;

        foreach (string line in File.ReadLines(path))
        {
            if (header)
            {
                header = false;
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            string[] fields = line.Split('\t');
            if (fields.Length < 3)
            {
                continue;
            }

            entries.Add(new NameIndexEntry(
                Id: fields[0],
                PrintName: fields[1],
                InvertedName: fields[2]));
        }

        return entries;
    }

    public static List<RetirementRecord> ParseRetirements(string path)
    {
        List<RetirementRecord> records = new(400);
        bool header = true;

        foreach (string line in File.ReadLines(path))
        {
            if (header)
            {
                header = false;
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            string[] fields = line.Split('\t');
            if (fields.Length < 6)
            {
                continue;
            }

            records.Add(new RetirementRecord(
                Id: fields[0],
                RefName: fields[1],
                RetReason: fields[2].Length > 0 ? fields[2][0] : '?',
                ChangeTo: fields[3].Length > 0 ? fields[3] : null,
                RetRemedy: fields[4].Length > 0 ? fields[4] : null,
                EffectiveDate: fields[5]));
        }

        return records;
    }
}
