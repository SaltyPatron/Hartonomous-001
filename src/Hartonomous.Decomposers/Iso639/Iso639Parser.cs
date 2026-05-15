using System.Collections.Generic;
using System.IO;

namespace Hartonomous.Decomposers.Iso639;

/// <summary>
/// One ISO 639-2 record (Library of Congress alpha-3 + alpha-2 + English/French names).
/// </summary>
/// <param name="Alpha3Bibliographic">Three-letter bibliographic code (the historical / French-derived one for languages with bibliographic vs terminologic split; otherwise same as terminologic).</param>
/// <param name="Alpha3Terminologic">Three-letter terminologic code (the linguistic preference; null when same as bibliographic).</param>
/// <param name="Alpha2">Two-letter ISO 639-1 code; null when none exists.</param>
/// <param name="EnglishName">English name (may contain semicolons for multi-name entries).</param>
/// <param name="FrenchName">French name (may contain semicolons for multi-name entries).</param>
internal sealed record Iso6392Record(
    string Alpha3Bibliographic,
    string? Alpha3Terminologic,
    string? Alpha2,
    string EnglishName,
    string FrenchName);

/// <summary>
/// One IANA BCP47 language-subtag-registry record. The registry has Type ∈
/// {language, extlang, script, region, variant, grandfathered, redundant};
/// this DTO covers the load-bearing language + script + region types.
/// </summary>
/// <param name="Type">"language" / "extlang" / "script" / "region" / "variant" / "grandfathered" / "redundant".</param>
/// <param name="Subtag">The canonical subtag (e.g. "en", "Latn", "US", "1996").</param>
/// <param name="Descriptions">Description lines (a registry record may have multiple Description: lines).</param>
/// <param name="Added">ISO-8601 date the subtag was added to the registry.</param>
/// <param name="SuppressScript">"Suppress-Script" subtag if present (e.g. "Latn" for "en" — script is implicit).</param>
/// <param name="Scope">"Scope" value if present ("macrolanguage" / "collection" / "private-use" / "special").</param>
/// <param name="Macrolanguage">"Macrolanguage" code if present (the parent macrolanguage).</param>
/// <param name="DeprecatedDate">"Deprecated" date if present (subtag is deprecated as of this date).</param>
/// <param name="PreferredValue">"Preferred-Value" if present (the canonical replacement when deprecated).</param>
/// <param name="Prefix">Prefix subtag list if Type = extlang/variant.</param>
internal sealed record Bcp47Record(
    string Type,
    string Subtag,
    List<string> Descriptions,
    string? Added,
    string? SuppressScript,
    string? Scope,
    string? Macrolanguage,
    string? DeprecatedDate,
    string? PreferredValue,
    List<string> Prefix);

internal static class Iso639Parser
{
    /// <summary>
    /// Parse the LoC-published ISO-639-2_utf-8.txt — pipe-delimited
    /// (`alpha3b | alpha3t | alpha2 | English | French`) with CRLF line
    /// endings and BOM. Returns the full list of language records (one per
    /// 639-2 language, ~500 entries).
    /// </summary>
    public static List<Iso6392Record> ParseIso639_2(string path)
    {
        List<Iso6392Record> records = new(600);
        foreach (string raw in File.ReadLines(path))
        {
            // Strip BOM on the first line + trim CR.
            string line = raw.TrimStart('﻿').TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }
            string[] fields = line.Split('|');
            if (fields.Length < 5)
            {
                continue;
            }
            records.Add(new Iso6392Record(
                Alpha3Bibliographic: fields[0].Trim(),
                Alpha3Terminologic: fields[1].Length > 0 ? fields[1].Trim() : null,
                Alpha2: fields[2].Length > 0 ? fields[2].Trim() : null,
                EnglishName: fields[3].Trim(),
                FrenchName: fields[4].Trim()));
        }
        return records;
    }

    /// <summary>
    /// Parse the IANA-published BCP47 language-subtag-registry. Format:
    /// record-separator-based (records separated by lines containing only
    /// "%%"); within each record, `Key: Value` lines. The registry covers
    /// language / extlang / script / region / variant / grandfathered /
    /// redundant subtag types. Returns the full list (~9000 entries as of
    /// 2026-05-05).
    /// </summary>
    public static List<Bcp47Record> ParseBcp47Registry(string path)
    {
        List<Bcp47Record> records = new(10000);
        string type = string.Empty;
        string subtag = string.Empty;
        List<string> descriptions = new();
        string? added = null;
        string? suppressScript = null;
        string? scope = null;
        string? macrolanguage = null;
        string? deprecatedDate = null;
        string? preferredValue = null;
        List<string> prefix = new();
        string? currentKey = null;
        System.Text.StringBuilder currentVal = new();

        void Flush()
        {
            if (subtag.Length > 0 && type.Length > 0)
            {
                records.Add(new Bcp47Record(
                    Type: type,
                    Subtag: subtag,
                    Descriptions: new List<string>(descriptions),
                    Added: added,
                    SuppressScript: suppressScript,
                    Scope: scope,
                    Macrolanguage: macrolanguage,
                    DeprecatedDate: deprecatedDate,
                    PreferredValue: preferredValue,
                    Prefix: new List<string>(prefix)));
            }
            type = string.Empty; subtag = string.Empty;
            descriptions.Clear();
            added = null; suppressScript = null; scope = null; macrolanguage = null;
            deprecatedDate = null; preferredValue = null;
            prefix.Clear();
            currentKey = null;
            currentVal.Clear();
        }

        void Commit()
        {
            if (currentKey is null) { return; }
            string val = currentVal.ToString();
            switch (currentKey)
            {
                case "Type": type = val; break;
                case "Subtag": subtag = val; break;
                case "Tag": subtag = val; break; // grandfathered / redundant use Tag instead of Subtag
                case "Description": descriptions.Add(val); break;
                case "Added": added = val; break;
                case "Suppress-Script": suppressScript = val; break;
                case "Scope": scope = val; break;
                case "Macrolanguage": macrolanguage = val; break;
                case "Deprecated": deprecatedDate = val; break;
                case "Preferred-Value": preferredValue = val; break;
                case "Prefix": prefix.Add(val); break;
                // Comments / Author / etc. ignored.
            }
            currentKey = null;
            currentVal.Clear();
        }

        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.TrimEnd('\r');
            if (line == "%%")
            {
                Commit();
                Flush();
                continue;
            }
            // Continuation lines start with whitespace and append to the prior key's value.
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                if (currentKey is not null)
                {
                    currentVal.Append(' ').Append(line.TrimStart());
                }
                continue;
            }
            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }
            // New Key: Value line — commit prior, start new.
            Commit();
            currentKey = line.Substring(0, colon).Trim();
            currentVal.Append(line.Substring(colon + 1).TrimStart());
        }
        Commit();
        Flush();
        return records;
    }

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
