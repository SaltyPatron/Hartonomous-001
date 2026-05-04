using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Hartonomous.Decomposers.Wiktionary;

/// <summary>
/// Streams one <see cref="WiktEntry"/> per line from a raw wiktextract JSONL file.
/// <list type="bullet">
///   <item>Bounded memory: reads one line, parses it, yields the record, moves on. The 20.4GB
///     source file does not need to fit in memory.</item>
///   <item>Blank / whitespace-only lines are skipped. Lines that do not carry the minimum shape
///     (word + lang_code + pos) are skipped — the raw dump does contain a handful of
///     redirect-only entries that carry no lexical content.</item>
///   <item>Missing optional arrays become empty lists (never null) — downstream code treats
///     absence of a field the same as an empty list.</item>
/// </list>
/// </summary>
internal static class WiktionaryJsonlParser
{
    private static readonly IReadOnlyList<string> EmptyStrings = Array.Empty<string>();
    private static readonly IReadOnlyList<WiktExample> EmptyExamples = Array.Empty<WiktExample>();
    private static readonly IReadOnlyList<WiktSense> EmptySenses = Array.Empty<WiktSense>();
    private static readonly IReadOnlyList<WiktForm> EmptyForms = Array.Empty<WiktForm>();
    private static readonly IReadOnlyList<WiktSound> EmptySounds = Array.Empty<WiktSound>();
    private static readonly IReadOnlyList<WiktHyphenation> EmptyHyphs = Array.Empty<WiktHyphenation>();
    private static readonly IReadOnlyList<WiktTranslation> EmptyTranslations = Array.Empty<WiktTranslation>();
    private static readonly IReadOnlyList<WiktRelation> EmptyRelations = Array.Empty<WiktRelation>();
    private static readonly IReadOnlyList<WiktEtymologyTemplate> EmptyEtymTemplates = Array.Empty<WiktEtymologyTemplate>();

    public static IEnumerable<WiktEntry> Parse(string path)
        => Parse(path, langCodeFilter: null);

    /// <summary>
    /// Streams entries with an optional substring pre-filter on <c>lang_code</c>.
    /// When <paramref name="langCodeFilter"/> is non-null, lines whose raw text does not
    /// contain any <c>"lang_code":"&lt;code&gt;"</c> token are skipped before
    /// <see cref="JsonDocument.Parse"/> runs. The full multilingual master dump
    /// (raw-wiktextract-data.jsonl, ~21 GB) carries ~10M entries of which only a
    /// fraction match any given filter; the substring scan rejects the rest at
    /// memory-bandwidth speed instead of paying full JSON-parse cost on each line.
    /// False positives (e.g., a French entry whose translations array references
    /// "lang_code":"en") still parse fully and are rejected by the entry-level
    /// LanguageAllowed check in the decomposer — correct, just no speed win on those.
    /// </summary>
    public static IEnumerable<WiktEntry> Parse(string path, IReadOnlyCollection<string>? langCodeFilter)
    {
        string[]? prefilter = BuildPrefilter(langCodeFilter);
        foreach (string line in File.ReadLines(path))
        {
            if (prefilter is not null && !LineCarriesAllowedLangCode(line, prefilter))
            {
                continue;
            }
            WiktEntry? entry = ParseLine(line);
            if (entry is not null)
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Streaming reader variant — exposes <see cref="BytesRead"/> /
    /// <see cref="TotalBytes"/> so the caller can report progress as a
    /// fraction of the input file consumed. Entry count alone tells you
    /// nothing about how far through a 2.9 GB JSONL file you are; bytes
    /// consumed do.
    /// </summary>
    public sealed class StreamingReader : IEnumerable<WiktEntry>, IDisposable
    {
        private readonly FileStream _fs;
        private readonly StreamReader _sr;
        private readonly string[]? _prefilter;
        public long TotalBytes { get; }
        public long BytesRead => _fs.Position;
        public long EntriesParsed { get; private set; }

        public StreamingReader(string path, IReadOnlyCollection<string>? langCodeFilter)
        {
            _fs = File.OpenRead(path);
            _sr = new StreamReader(_fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20);
            _prefilter = BuildPrefilter(langCodeFilter);
            TotalBytes = _fs.Length;
        }

        public IEnumerator<WiktEntry> GetEnumerator()
        {
            string? line;
            while ((line = _sr.ReadLine()) is not null)
            {
                if (_prefilter is not null && !LineCarriesAllowedLangCode(line, _prefilter))
                {
                    continue;
                }
                WiktEntry? entry = ParseLine(line);
                if (entry is null)
                {
                    continue;
                }
                EntriesParsed++;
                yield return entry;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose()
        {
            _sr.Dispose();
            _fs.Dispose();
        }
    }

    private static string[]? BuildPrefilter(IReadOnlyCollection<string>? langCodeFilter)
    {
        if (langCodeFilter is null || langCodeFilter.Count == 0)
        {
            return null;
        }
        // wiktextract's JSONL is compact (no whitespace between key and value), but
        // handle the spaced variant too in case the source is ever reformatted.
        string[] patterns = new string[langCodeFilter.Count * 2];
        int i = 0;
        foreach (string code in langCodeFilter)
        {
            patterns[i++] = "\"lang_code\":\"" + code + "\"";
            patterns[i++] = "\"lang_code\": \"" + code + "\"";
        }
        return patterns;
    }

    private static bool LineCarriesAllowedLangCode(string line, string[] patterns)
    {
        foreach (string p in patterns)
        {
            if (line.Contains(p, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public static WiktEntry? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? word = GetString(root, "word");
        string? lang = GetString(root, "lang");
        string? langCode = GetString(root, "lang_code");
        string? pos = GetString(root, "pos");
        if (word is null || langCode is null || pos is null)
        {
            return null;
        }

        return new WiktEntry(
            Word: word,
            Lang: lang ?? string.Empty,
            LangCode: langCode,
            Pos: pos,
            EtymologyNumber: GetInt(root, "etymology_number"),
            EtymologyText: GetString(root, "etymology_text"),
            EtymologyTemplates: ParseEtymTemplates(root),
            Senses: ParseSenses(root),
            Forms: ParseForms(root),
            Sounds: ParseSounds(root),
            Hyphenations: ParseHyphenations(root),
            Translations: ParseTranslations(root),
            Synonyms: ParseRelations(root, "synonyms"),
            Antonyms: ParseRelations(root, "antonyms"),
            Hypernyms: ParseRelations(root, "hypernyms"),
            Hyponyms: ParseRelations(root, "hyponyms"),
            Meronyms: ParseRelations(root, "meronyms"),
            CoordinateTerms: ParseRelations(root, "coordinate_terms"),
            Derived: ParseRelations(root, "derived"),
            Related: ParseRelations(root, "related"));
    }

    private static IReadOnlyList<WiktSense> ParseSenses(JsonElement root)
    {
        if (!root.TryGetProperty("senses", out JsonElement senses) ||
            senses.ValueKind != JsonValueKind.Array)
        {
            return EmptySenses;
        }

        List<WiktSense> list = new(senses.GetArrayLength());
        foreach (JsonElement s in senses.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            list.Add(new WiktSense(
                Glosses: GetStringArray(s, "glosses"),
                RawGlosses: GetStringArray(s, "raw_glosses"),
                Tags: GetStringArray(s, "tags"),
                Categories: GetStringArray(s, "categories"),
                Examples: ParseExamples(s),
                Senseid: GetStringArray(s, "senseid"),
                Wikidata: GetStringArray(s, "wikidata")));
        }
        return list;
    }

    private static IReadOnlyList<WiktExample> ParseExamples(JsonElement sense)
    {
        if (!sense.TryGetProperty("examples", out JsonElement examples) ||
            examples.ValueKind != JsonValueKind.Array)
        {
            return EmptyExamples;
        }

        List<WiktExample> list = new(examples.GetArrayLength());
        foreach (JsonElement ex in examples.EnumerateArray())
        {
            if (ex.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? text = GetString(ex, "text");
            if (text is null)
            {
                continue;
            }

            list.Add(new WiktExample(
                Text: text,
                Type: GetString(ex, "type"),
                Ref: GetString(ex, "ref")));
        }
        return list;
    }

    private static IReadOnlyList<WiktForm> ParseForms(JsonElement root)
    {
        if (!root.TryGetProperty("forms", out JsonElement forms) ||
            forms.ValueKind != JsonValueKind.Array)
        {
            return EmptyForms;
        }

        List<WiktForm> list = new(forms.GetArrayLength());
        foreach (JsonElement f in forms.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? form = GetString(f, "form");
            if (form is null)
            {
                continue;
            }

            list.Add(new WiktForm(form, GetStringArray(f, "tags")));
        }
        return list;
    }

    private static IReadOnlyList<WiktSound> ParseSounds(JsonElement root)
    {
        if (!root.TryGetProperty("sounds", out JsonElement sounds) ||
            sounds.ValueKind != JsonValueKind.Array)
        {
            return EmptySounds;
        }

        List<WiktSound> list = new(sounds.GetArrayLength());
        foreach (JsonElement s in sounds.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            list.Add(new WiktSound(
                Ipa: GetString(s, "ipa"),
                Enpr: GetString(s, "enpr"),
                Tags: GetStringArray(s, "tags"),
                Audio: GetString(s, "audio"),
                OggUrl: GetString(s, "ogg_url"),
                Mp3Url: GetString(s, "mp3_url")));
        }
        return list;
    }

    private static IReadOnlyList<WiktHyphenation> ParseHyphenations(JsonElement root)
    {
        if (!root.TryGetProperty("hyphenations", out JsonElement hyphs) ||
            hyphs.ValueKind != JsonValueKind.Array)
        {
            return EmptyHyphs;
        }

        List<WiktHyphenation> list = new(hyphs.GetArrayLength());
        foreach (JsonElement h in hyphs.EnumerateArray())
        {
            if (h.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            IReadOnlyList<string> parts = GetStringArray(h, "parts");
            if (parts.Count == 0)
            {
                continue;
            }
            list.Add(new WiktHyphenation(parts));
        }
        return list;
    }

    private static IReadOnlyList<WiktTranslation> ParseTranslations(JsonElement root)
    {
        if (!root.TryGetProperty("translations", out JsonElement translations) ||
            translations.ValueKind != JsonValueKind.Array)
        {
            return EmptyTranslations;
        }

        List<WiktTranslation> list = new(translations.GetArrayLength());
        foreach (JsonElement t in translations.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? word = GetString(t, "word");
            if (word is null)
            {
                continue;
            }

            list.Add(new WiktTranslation(
                LangName: GetString(t, "lang"),
                LangCode: GetString(t, "code") ?? GetString(t, "lang_code"),
                Word: word,
                Sense: GetString(t, "sense"),
                Roman: GetString(t, "roman"),
                Tags: GetStringArray(t, "tags")));
        }
        return list;
    }

    private static IReadOnlyList<WiktRelation> ParseRelations(JsonElement root, string fieldName)
    {
        if (!root.TryGetProperty(fieldName, out JsonElement rels) ||
            rels.ValueKind != JsonValueKind.Array)
        {
            return EmptyRelations;
        }

        List<WiktRelation> list = new(rels.GetArrayLength());
        foreach (JsonElement r in rels.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? word = GetString(r, "word");
            if (word is null)
            {
                continue;
            }

            list.Add(new WiktRelation(
                Word: word,
                Source: GetString(r, "source"),
                Tags: GetStringArray(r, "tags"),
                SenseIndex: null));
        }
        return list;
    }

    private static IReadOnlyList<WiktEtymologyTemplate> ParseEtymTemplates(JsonElement root)
    {
        if (!root.TryGetProperty("etymology_templates", out JsonElement templates) ||
            templates.ValueKind != JsonValueKind.Array)
        {
            return EmptyEtymTemplates;
        }

        List<WiktEtymologyTemplate> list = new(templates.GetArrayLength());
        foreach (JsonElement et in templates.EnumerateArray())
        {
            if (et.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? name = GetString(et, "name");
            if (name is null)
            {
                continue;
            }

            Dictionary<string, string> args = new(StringComparer.Ordinal);
            if (et.TryGetProperty("args", out JsonElement argsEl) &&
                argsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in argsEl.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        args[p.Name] = p.Value.GetString() ?? string.Empty;
                    }
                }
            }

            list.Add(new WiktEtymologyTemplate(
                Name: name,
                Args: args,
                Expansion: GetString(et, "expansion")));
        }
        return list;
    }

    private static string? GetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out JsonElement v))
        {
            return null;
        }
        if (v.ValueKind == JsonValueKind.String)
        {
            string? s = v.GetString();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        return null;
    }

    private static int? GetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out JsonElement v))
        {
            return null;
        }
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n))
        {
            return n;
        }
        return null;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out JsonElement arr) ||
            arr.ValueKind != JsonValueKind.Array)
        {
            return EmptyStrings;
        }

        List<string> list = new(arr.GetArrayLength());
        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string? s = item.GetString();
                if (!string.IsNullOrEmpty(s))
                {
                    list.Add(s);
                }
            }
        }
        return list;
    }
}
