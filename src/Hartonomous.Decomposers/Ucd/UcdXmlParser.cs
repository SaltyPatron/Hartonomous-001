using System.Xml;

namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Streaming parser for ucd.all.grouped.xml. Uses XmlReader to avoid loading 44MB+ into memory.
/// The grouped format uses &lt;group&gt; elements with shared attribute defaults; each &lt;char&gt;
/// inherits group attributes and overrides specific ones.
/// </summary>
internal static class UcdXmlParser
{
    public static IEnumerable<CodepointRecord> Parse(
        string xmlPath,
        ReferenceTableCollector refs,
        CancellationToken ct)
    {
        using XmlReader reader = XmlReader.Create(xmlPath, new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
        });

        Dictionary<string, string> groupDefaults = new();

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName == "group")
            {
                groupDefaults.Clear();
                if (reader.HasAttributes)
                {
                    for (int i = 0; i < reader.AttributeCount; i++)
                    {
                        reader.MoveToAttribute(i);
                        groupDefaults[reader.LocalName] = reader.Value;
                    }
                    reader.MoveToElement();
                }
                continue;
            }

            if (reader.LocalName is not ("char" or "reserved" or "noncharacter" or "surrogate"))
            {
                continue;
            }

            Dictionary<string, string> attrs = new(groupDefaults);
            if (reader.HasAttributes)
            {
                for (int i = 0; i < reader.AttributeCount; i++)
                {
                    reader.MoveToAttribute(i);
                    attrs[reader.LocalName] = reader.Value;
                }
                reader.MoveToElement();
            }

            if (attrs.TryGetValue("first-cp", out string? firstCp) &&
                attrs.TryGetValue("last-cp", out string? lastCp))
            {
                int first = Convert.ToInt32(firstCp, 16);
                int last = Convert.ToInt32(lastCp, 16);
                for (int cp = first; cp <= last; cp++)
                {
                    ct.ThrowIfCancellationRequested();
                    CodepointRecord? record = BuildRecord(cp, attrs, refs);
                    if (record != null)
                    {
                        yield return record;
                    }
                }
                continue;
            }

            if (!attrs.TryGetValue("cp", out string? cpHex))
            {
                continue;
            }

            int cpValue = Convert.ToInt32(cpHex, 16);
            CodepointRecord? single = BuildRecord(cpValue, attrs, refs);
            if (single != null)
            {
                yield return single;
            }
        }
    }

    private static CodepointRecord? BuildRecord(
        int cpValue,
        Dictionary<string, string> attrs,
        ReferenceTableCollector refs)
    {
        string gc = GetAttr(attrs, "gc", "Cn");
        string script = GetAttr(attrs, "sc", "Zzzz");
        string block = GetAttr(attrs, "blk", "NB");
        string age = GetAttr(attrs, "age", "NA");
        string name = GetAttr(attrs, "na", "");

        if (string.IsNullOrEmpty(name))
        {
            string na1 = GetAttr(attrs, "na1", "");
            if (!string.IsNullOrEmpty(na1))
            {
                name = na1;
            }
            else
            {
                name = $"U+{cpValue:X4}";
            }
        }

        refs.AddGeneralCategory(gc);
        refs.AddScript(script);
        if (block != "NB")
        {
            refs.AddBlock(block, 0, 0);
        }

        string? gcb = GetAttrOrNull(attrs, "GCB");
        string? wb = GetAttrOrNull(attrs, "WB");
        string? sb = GetAttrOrNull(attrs, "SB");
        string? lb = GetAttrOrNull(attrs, "lb");

        if (gcb != null)
        {
            refs.AddBreakProperty(gcb, "GCB");
        }

        if (wb != null)
        {
            refs.AddBreakProperty(wb, "WB");
        }

        if (sb != null)
        {
            refs.AddBreakProperty(sb, "SB");
        }

        if (lb != null)
        {
            refs.AddBreakProperty(lb, "LB");
        }

        return new CodepointRecord
        {
            Value = cpValue,
            Name = name,
            GeneralCategory = gc,
            Script = script,
            Block = block,
            Age = age,
            GraphemeClusterBreak = gcb,
            WordBreak = wb,
            SentenceBreak = sb,
            LineBreak = lb,
            BidiClass = GetAttrOrNull(attrs, "bc"),
            BidiMirrored = GetAttr(attrs, "Bidi_M", "N") == "Y",
            EastAsianWidth = GetAttrOrNull(attrs, "ea"),
            SimpleUppercase = ParseCpAttr(attrs, "suc"),
            SimpleLowercase = ParseCpAttr(attrs, "slc"),
            SimpleTitlecase = ParseCpAttr(attrs, "stc"),
            SimpleCaseFolding = ParseCpAttr(attrs, "scf"),
            DecompositionType = GetAttrOrNull(attrs, "dt"),
            DecompositionMapping = ParseCpListAttr(attrs, "dm"),
            CanonicalCombiningClass = int.TryParse(GetAttr(attrs, "ccc", "0"), out int ccc) ? ccc : 0,
            NumericType = GetAttrOrNull(attrs, "nt"),
            NumericValue = GetAttrOrNull(attrs, "nv"),
            JoiningType = GetAttrOrNull(attrs, "jt"),
            JoiningGroup = GetAttrOrNull(attrs, "jg"),
            HangulSyllableType = GetAttrOrNull(attrs, "hst"),
            IndicSyllabicCategory = GetAttrOrNull(attrs, "InSC"),
            IndicPositionalCategory = GetAttrOrNull(attrs, "InPC"),
            VerticalOrientation = GetAttrOrNull(attrs, "vo"),
            IsAlphabetic = GetAttr(attrs, "Alpha", "N") == "Y",
            IsCased = GetAttr(attrs, "Cased", "N") == "Y",
            IsUppercase = GetAttr(attrs, "Upper", "N") == "Y",
            IsLowercase = GetAttr(attrs, "Lower", "N") == "Y",
            IsMath = GetAttr(attrs, "Math", "N") == "Y",
            IsIdeographic = GetAttr(attrs, "Ideo", "N") == "Y",
            IsDash = GetAttr(attrs, "Dash", "N") == "Y",
            IsWhiteSpace = GetAttr(attrs, "WSpace", "N") == "Y",
            IsGraphemeBase = GetAttr(attrs, "Gr_Base", "N") == "Y",
            IsGraphemeExtend = GetAttr(attrs, "Gr_Ext", "N") == "Y",
            IsIdStart = GetAttr(attrs, "IDS", "N") == "Y",
            IsIdContinue = GetAttr(attrs, "IDC", "N") == "Y",
            IsEmoji = GetAttr(attrs, "Emoji", "N") == "Y",
            IsEmojiPresentation = GetAttr(attrs, "EPres", "N") == "Y",
            IsEmojiModifier = GetAttr(attrs, "EMod", "N") == "Y",
            IsEmojiModifierBase = GetAttr(attrs, "EBase", "N") == "Y",
            IsEmojiComponent = GetAttr(attrs, "EComp", "N") == "Y",
            IsExtendedPictographic = GetAttr(attrs, "ExtPict", "N") == "Y",
            IsDefaultIgnorable = GetAttr(attrs, "DI", "N") == "Y",
            IsDeprecated = GetAttr(attrs, "Dep", "N") == "Y",
            IsSoftDotted = GetAttr(attrs, "SD", "N") == "Y",
            IsSentenceTerminal = GetAttr(attrs, "STerm", "N") == "Y",
            IsTerminalPunctuation = GetAttr(attrs, "Term", "N") == "Y",
            IsQuotationMark = GetAttr(attrs, "QMark", "N") == "Y",
            IsRadical = GetAttr(attrs, "Radical", "N") == "Y",
            IsVariationSelector = GetAttr(attrs, "VS", "N") == "Y",
            IsPatternSyntax = GetAttr(attrs, "Pat_Syn", "N") == "Y",
            IsPatternWhiteSpace = GetAttr(attrs, "Pat_WS", "N") == "Y",
        };
    }

    private static string GetAttr(Dictionary<string, string> attrs, string key, string defaultValue)
    {
        return attrs.TryGetValue(key, out string? v) ? v : defaultValue;
    }

    private static string? GetAttrOrNull(Dictionary<string, string> attrs, string key)
    {
        return attrs.TryGetValue(key, out string? v) ? v : null;
    }

    private static int? ParseCpAttr(Dictionary<string, string> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out string? v) || string.IsNullOrEmpty(v) || v == "#")
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(v, 16);
        }
        catch
        {
            return null;
        }
    }

    private static int[]? ParseCpListAttr(Dictionary<string, string> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out string? v) || string.IsNullOrEmpty(v) || v == "#")
        {
            return null;
        }

        string[] parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            return parts.Select(p => Convert.ToInt32(p, 16)).ToArray();
        }
        catch
        {
            return null;
        }
    }
}
