using System;
using System.Collections.Generic;

namespace Hartonomous.Decomposers.Wiktionary;

/// <summary>
/// Translates Wiktionary form-level tags (e.g., <c>plural</c>, <c>past-participle</c>)
/// into Universal Dependencies morphological features (e.g., <c>Number=Plur</c>,
/// <c>VerbForm=Part|Tense=Past</c>). Multi-feature expansions allowed — a single tag
/// can produce multiple (key, value) pairs.
/// <para>
/// Tags that have no clean UD equivalent produce an empty list. That is honest: we do
/// not fabricate morphological features from fuzzy English labels.
/// </para>
/// </summary>
internal static class WiktMorphTagMap
{
    private static readonly Dictionary<string, (string Key, string Value)[]> Map =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["plural"] = new[] { ("Number", "Plur") },
        ["singular"] = new[] { ("Number", "Sing") },
        ["dual"] = new[] { ("Number", "Dual") },

        ["present"] = new[] { ("Tense", "Pres") },
        ["past"] = new[] { ("Tense", "Past") },
        ["future"] = new[] { ("Tense", "Fut") },
        ["imperfect"] = new[] { ("Tense", "Imp") },

        ["participle"] = new[] { ("VerbForm", "Part") },
        ["past-participle"] = new[] { ("VerbForm", "Part"), ("Tense", "Past") },
        ["present-participle"] = new[] { ("VerbForm", "Part"), ("Tense", "Pres") },
        ["gerund"] = new[] { ("VerbForm", "Ger") },
        ["infinitive"] = new[] { ("VerbForm", "Inf") },
        ["finite"] = new[] { ("VerbForm", "Fin") },
        ["supine"] = new[] { ("VerbForm", "Sup") },

        ["comparative"] = new[] { ("Degree", "Cmp") },
        ["superlative"] = new[] { ("Degree", "Sup") },
        ["positive"] = new[] { ("Degree", "Pos") },

        ["first-person"] = new[] { ("Person", "1") },
        ["second-person"] = new[] { ("Person", "2") },
        ["third-person"] = new[] { ("Person", "3") },

        ["masculine"] = new[] { ("Gender", "Masc") },
        ["feminine"] = new[] { ("Gender", "Fem") },
        ["neuter"] = new[] { ("Gender", "Neut") },
        ["common-gender"] = new[] { ("Gender", "Com") },

        ["nominative"] = new[] { ("Case", "Nom") },
        ["accusative"] = new[] { ("Case", "Acc") },
        ["genitive"] = new[] { ("Case", "Gen") },
        ["dative"] = new[] { ("Case", "Dat") },
        ["ablative"] = new[] { ("Case", "Abl") },
        ["vocative"] = new[] { ("Case", "Voc") },
        ["locative"] = new[] { ("Case", "Loc") },
        ["instrumental"] = new[] { ("Case", "Ins") },

        ["indicative"] = new[] { ("Mood", "Ind") },
        ["subjunctive"] = new[] { ("Mood", "Sub") },
        ["imperative"] = new[] { ("Mood", "Imp") },
        ["conditional"] = new[] { ("Mood", "Cnd") },
        ["optative"] = new[] { ("Mood", "Opt") },

        ["active"] = new[] { ("Voice", "Act") },
        ["passive"] = new[] { ("Voice", "Pass") },
        ["middle"] = new[] { ("Voice", "Mid") },

        ["definite"] = new[] { ("Definite", "Def") },
        ["indefinite"] = new[] { ("Definite", "Ind") },

        ["perfective"] = new[] { ("Aspect", "Perf") },
        ["imperfective"] = new[] { ("Aspect", "Imp") },
        ["progressive"] = new[] { ("Aspect", "Prog") },
    };

    public static IReadOnlyList<(string Key, string Value)> Translate(IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
        {
            return Array.Empty<(string, string)>();
        }

        List<(string, string)> features = new(tags.Count);
        foreach (string tag in tags)
        {
            if (Map.TryGetValue(tag, out (string Key, string Value)[]? expansion))
            {
                features.AddRange(expansion);
            }
        }
        return features;
    }
}
