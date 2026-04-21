using System;
using System.Collections.Generic;

namespace Hartonomous.Decomposers.Wiktionary;

/// <summary>
/// Best-effort mapping from Wiktionary <c>pos</c> strings to Universal Dependencies UPOS
/// codes. Wiktionary uses a much larger POS vocabulary (character, letter, name, phrase,
/// proverb, abbreviation, etc.); mappings to the 17 UD UPOS tags are only emitted where
/// there is a clean semantic equivalent. Anything not in this table returns <c>null</c> —
/// the decomposer skips the <c>entity_pos</c> junction for such entries, which is the
/// correct honest behavior: no best-guess projection onto a UPOS that doesn't fit.
/// </summary>
internal static class WiktPosMap
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["noun"] = "NOUN",
        ["name"] = "PROPN",
        ["proper-noun"] = "PROPN",
        ["proper noun"] = "PROPN",
        ["verb"] = "VERB",
        ["adj"] = "ADJ",
        ["adjective"] = "ADJ",
        ["adv"] = "ADV",
        ["adverb"] = "ADV",
        ["pron"] = "PRON",
        ["pronoun"] = "PRON",
        ["det"] = "DET",
        ["determiner"] = "DET",
        ["article"] = "DET",
        ["num"] = "NUM",
        ["numeral"] = "NUM",
        ["number"] = "NUM",
        ["conj"] = "CCONJ",
        ["conjunction"] = "CCONJ",
        ["prep"] = "ADP",
        ["preposition"] = "ADP",
        ["postp"] = "ADP",
        ["postposition"] = "ADP",
        ["adp"] = "ADP",
        ["intj"] = "INTJ",
        ["interjection"] = "INTJ",
        ["particle"] = "PART",
        ["punct"] = "PUNCT",
        ["punctuation"] = "PUNCT",
        ["symbol"] = "SYM",
        ["sym"] = "SYM",
        ["aux"] = "AUX",
        ["auxiliary"] = "AUX",
    };

    public static string? ToUpos(string wiktPos)
    {
        return Map.TryGetValue(wiktPos, out string? upos) ? upos : null;
    }
}
