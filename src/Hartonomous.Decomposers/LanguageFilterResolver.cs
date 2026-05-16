using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Hartonomous.Decomposers;

/// <summary>
/// Resolves a user-supplied <see cref="Hartonomous.Core.Decomposition.DecomposerConfig.LanguageFilter"/>
/// into an in-memory O(1) allow-set across all ISO 639 forms + BCP47 dialect
/// prefixes the substrate's reference vocabulary knows about.
///
/// User may supply ANY mix of forms — the resolver normalizes them all to one
/// canonical id per language via the alias map loaded from
/// <c>substrate.language</c>:
///   <list type="bullet">
///     <item>ISO 639-1 two-letter:  "en", "zh", "ja", "ko", "es", "it", "fr", "ru"</item>
///     <item>ISO 639-3 three-letter: "eng", "cmn", "jpn", "kor", "spa", "ita", "fra", "rus"</item>
///     <item>ISO 639-2 bibliographic: "ger" (= deu), "fre" (= fra), "rum" (= ron)</item>
///     <item>BCP47 dialect prefixes: "en-US", "zh-Hans-CN", "sr-Latn-RS" — the resolver
///           extracts the primary subtag and matches against the alias map.</item>
///     <item>Macrolanguage codes: "zh" expands to {cmn, yue, wuu, nan, hak, gan, hsn,
///           cdo, mnp, cjy} via a built-in expansion table.</item>
///   </list>
///
/// Per-emission check (<see cref="IsAllowed"/>) is one Dictionary lookup +
/// one HashSet.Contains — sub-microsecond — with a small LRU-style cache for
/// BCP47 dialect resolution.
///
/// A null filter means "unfiltered" — <see cref="IsAllowed"/> always returns true.
/// An empty user filter (filter supplied but matched nothing in the alias map)
/// means <see cref="IsAllowed"/> always returns false (the user asked for
/// specific languages; the substrate knows none of them).
/// </summary>
public sealed class LanguageFilterResolver
{
    private readonly bool _unfiltered;
    private readonly HashSet<int> _allowedCanonical;
    private readonly Dictionary<string, int> _aliasToCanonical;

    // Cache for BCP47-dialect lookups. Bounded to avoid memory growth under
    // adversarial input streams; bounded eviction via simple count cap with
    // first-write-wins (no LRU machinery — substrate emission codes are a
    // narrow vocabulary in practice).
    private const int Bcp47CacheCap = 4096;
    private readonly ConcurrentDictionary<string, bool> _bcp47Cache;

    private LanguageFilterResolver(
        bool unfiltered,
        HashSet<int> allowedCanonical,
        Dictionary<string, int> aliasToCanonical)
    {
        _unfiltered = unfiltered;
        _allowedCanonical = allowedCanonical;
        _aliasToCanonical = aliasToCanonical;
        _bcp47Cache = new ConcurrentDictionary<string, bool>(
            concurrencyLevel: Environment.ProcessorCount,
            capacity: 64,
            comparer: StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Construct a resolver from a pre-loaded alias map (load via
    /// <c>IReferenceDataReader.LoadLanguageAliasMapAsync</c> at decomposer
    /// startup) and a user-supplied filter. A null or empty filter produces
    /// an unfiltered resolver.
    /// </summary>
    public static LanguageFilterResolver Create(
        IReadOnlyCollection<string>? userFilter,
        Dictionary<string, int> aliasMap)
    {
        ArgumentNullException.ThrowIfNull(aliasMap);

        if (userFilter is null)
        {
            return new LanguageFilterResolver(
                unfiltered: true,
                allowedCanonical: new HashSet<int>(),
                aliasToCanonical: aliasMap);
        }

        HashSet<int> allowed = new();
        foreach (string entry in userFilter)
        {
            if (string.IsNullOrWhiteSpace(entry)) { continue; }
            string trimmed = entry.Trim();
            int? canonical = ResolveCanonical(trimmed, aliasMap);
            if (canonical is int id)
            {
                allowed.Add(id);
                // Expand known macrolanguage groupings so picking "zh" or "ar"
                // pulls in their member languages without the user having to
                // enumerate "cmn", "yue", "wuu", "arb", "arz", etc.
                foreach (int member in ExpandMacroMembers(trimmed, aliasMap))
                {
                    allowed.Add(member);
                }
            }
            // Unknown code: silently drop. Decomposers can opt to surface
            // unknown-code warnings via their own logging if needed.
        }

        return new LanguageFilterResolver(
            unfiltered: false,
            allowedCanonical: allowed,
            aliasToCanonical: aliasMap);
    }

    /// <summary>
    /// Returns true when <paramref name="languageCode"/> is in the filter (or
    /// when no filter is set). Handles ISO 639-1/2/3 + BCP47 dialect prefixes.
    /// Empty or null code returns true when unfiltered, false when filtered.
    /// </summary>
    public bool IsAllowed(string? languageCode)
    {
        if (_unfiltered) { return true; }
        if (string.IsNullOrEmpty(languageCode)) { return false; }

        // Direct lookup (covers iso639-1/2/3 forms; case-insensitive).
        if (_aliasToCanonical.TryGetValue(languageCode, out int id))
        {
            return _allowedCanonical.Contains(id);
        }

        // BCP47 dialect path: cached resolution by primary-subtag prefix.
        if (_bcp47Cache.TryGetValue(languageCode, out bool cached))
        {
            return cached;
        }

        int sep = languageCode.IndexOfAny(s_bcp47Separators);
        bool allowed;
        if (sep > 0)
        {
            string prefix = languageCode[..sep];
            allowed = _aliasToCanonical.TryGetValue(prefix, out int prefixId)
                  && _allowedCanonical.Contains(prefixId);
        }
        else
        {
            allowed = false;
        }

        // Bounded cache to avoid unbounded growth under adversarial input.
        if (_bcp47Cache.Count < Bcp47CacheCap)
        {
            _bcp47Cache.TryAdd(languageCode, allowed);
        }
        return allowed;
    }

    /// <summary>True when the resolver was constructed with a user filter.</summary>
    public bool IsFiltered => !_unfiltered;

    /// <summary>Count of canonical languages the filter accepts.</summary>
    public int AllowedLanguageCount => _allowedCanonical.Count;

    private static readonly char[] s_bcp47Separators = ['-', '_'];

    private static int? ResolveCanonical(string entry, Dictionary<string, int> aliasMap)
    {
        if (aliasMap.TryGetValue(entry, out int id)) { return id; }
        int sep = entry.IndexOfAny(s_bcp47Separators);
        if (sep > 0)
        {
            string prefix = entry[..sep];
            if (aliasMap.TryGetValue(prefix, out int prefixId)) { return prefixId; }
        }
        return null;
    }

    private static IEnumerable<int> ExpandMacroMembers(string entry, Dictionary<string, int> aliasMap)
    {
        string lower = entry.ToLowerInvariant();
        int sep = lower.IndexOfAny(s_bcp47Separators);
        string normalized = sep > 0 ? lower[..sep] : lower;

        if (!s_macroExpansions.TryGetValue(normalized, out string[]? members)) { yield break; }
        foreach (string member in members)
        {
            if (aliasMap.TryGetValue(member, out int id)) { yield return id; }
        }
    }

    // Hardcoded macrolanguage expansions per ISO 639-5. Each entry maps a macro
    // code (iso639-1 or iso639-3) to its dominant member iso639-3 codes. Covers
    // the macrolanguages with substantial seed-data presence; can extend to a
    // proper substrate.language_macrolanguage junction when the seed data is
    // available.
    private static readonly Dictionary<string, string[]> s_macroExpansions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Chinese (zh) — Mandarin, Cantonese, Wu, Min Nan, Hakka, Gan, Xiang, Min Dong, Min Bei, Jin
        ["zh"]  = ["cmn", "yue", "wuu", "nan", "hak", "gan", "hsn", "cdo", "mnp", "cjy"],
        ["zho"] = ["cmn", "yue", "wuu", "nan", "hak", "gan", "hsn", "cdo", "mnp", "cjy"],
        // Arabic (ar) — Standard, Egyptian, Mesopotamian, Levantine, Moroccan, Gulf, S. Levantine, Tunisian
        ["ar"]  = ["arb", "arz", "acm", "apc", "ary", "afb", "ajp", "aeb", "ayl", "ayh"],
        ["ara"] = ["arb", "arz", "acm", "apc", "ary", "afb", "ajp", "aeb", "ayl", "ayh"],
        // Persian/Farsi (fa) — Iranian Persian, Dari
        ["fa"]  = ["pes", "prs"],
        ["fas"] = ["pes", "prs"],
        // Malay (ms) — Standard Malay, Indonesian
        ["ms"]  = ["zsm", "ind"],
        ["msa"] = ["zsm", "ind"],
        // Norwegian (no) — Bokmål, Nynorsk
        ["no"]  = ["nob", "nno"],
        ["nor"] = ["nob", "nno"],
    };
}
