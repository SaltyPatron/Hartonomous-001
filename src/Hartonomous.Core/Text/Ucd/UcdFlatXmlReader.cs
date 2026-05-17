using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;

namespace Hartonomous.Core.Text.Ucd;

/// <summary>
/// Streaming reader for ucd.all.flat.xml (UAX #42 flat format). Mirrors the
/// Python pre-gen's parse_ucd_flat_xml shape: one record per codepoint
/// (expanding range elements) with all per-cp UAX #44 attributes.
///
/// Substrate extracts the semantic content (codepoint properties, case
/// mappings, sequence assemblies) and discards the XML structure — this
/// reader is the parsing surface; consumers emit through IIngestionBatch
/// like every other decomposer.
///
/// Handles both .zip (ucd.all.flat.zip — most common in /vault staging)
/// and raw .xml inputs. XmlReader handles the default namespace
/// (xmlns="http://www.unicode.org/ns/2003/ucd/1.0") natively via QName
/// comparison.
/// </summary>
public sealed class UcdFlatXmlReader : IDisposable
{
    private const string UcdNamespace = "http://www.unicode.org/ns/2003/ucd/1.0";

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly XmlReader _xml;
    private readonly ZipArchive? _zip;

    public UcdFlatXmlReader(string path)
    {
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            FileStream fs = File.OpenRead(path);
            _zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry? entry = null;
            foreach (ZipArchiveEntry e in _zip.Entries)
            {
                if (e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    entry = e;
                    break;
                }
            }
            if (entry is null)
            {
                throw new InvalidOperationException($"No .xml entry in {path}");
            }
            _stream = entry.Open();
            _ownsStream = true;
        }
        else
        {
            _stream = File.OpenRead(path);
            _ownsStream = true;
        }

        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = true,
        };
        _xml = XmlReader.Create(_stream, settings);
    }

    /// <summary>
    /// Stream all codepoint records. Range elements (first-cp/last-cp) expand
    /// to one record per codepoint in the range. reserved/noncharacter/surrogate
    /// elements also emit records (so the consumer sees the full 0..0x10FFFF
    /// plane, with `Assigned` flag distinguishing assigned vs unassigned).
    /// </summary>
    public IEnumerable<CodepointRecord> ReadAll()
    {
        while (_xml.Read())
        {
            if (_xml.NodeType != XmlNodeType.Element) { continue; }
            if (_xml.NamespaceURI != UcdNamespace) { continue; }

            string localName = _xml.LocalName;
            bool isChar = localName == "char";
            bool isReserved = localName == "reserved";
            bool isNoncharacter = localName == "noncharacter";
            bool isSurrogate = localName == "surrogate";
            if (!isChar && !isReserved && !isNoncharacter && !isSurrogate) { continue; }

            string? cpAttr = _xml.GetAttribute("cp");
            string? firstCp = _xml.GetAttribute("first-cp");
            string? lastCp = _xml.GetAttribute("last-cp");

            int loCp, hiCp;
            if (cpAttr is not null)
            {
                loCp = hiCp = int.Parse(cpAttr, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            else if (firstCp is not null && lastCp is not null)
            {
                loCp = int.Parse(firstCp, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                hiCp = int.Parse(lastCp, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            else
            {
                continue;
            }

            // Snapshot attributes from the element (we'll iterate sub-elements
            // for name-aliases after this).
            CodepointAttributes attrs = SnapshotAttributes(_xml);

            // Capture name-aliases (child elements within <char>).
            List<NameAlias>? aliases = null;
            if (isChar && !_xml.IsEmptyElement)
            {
                using XmlReader sub = _xml.ReadSubtree();
                sub.Read(); // move past the <char> open
                while (sub.Read())
                {
                    if (sub.NodeType == XmlNodeType.Element
                        && sub.NamespaceURI == UcdNamespace
                        && sub.LocalName == "name-alias")
                    {
                        string? alias = sub.GetAttribute("alias");
                        string? type = sub.GetAttribute("type");
                        if (!string.IsNullOrEmpty(alias))
                        {
                            aliases ??= new List<NameAlias>(2);
                            aliases.Add(new NameAlias(alias, type ?? ""));
                        }
                    }
                }
            }

            bool assigned = isChar;
            for (int cp = loCp; cp <= hiCp; cp++)
            {
                yield return new CodepointRecord(cp, assigned, attrs, aliases);
            }
        }
    }

    private static CodepointAttributes SnapshotAttributes(XmlReader xml)
    {
        // Read every attribute via direct GetAttribute calls (XmlReader is
        // positional; we capture the values we need before advancing).
        return new CodepointAttributes
        {
            Name = xml.GetAttribute("na") ?? "",
            Name1 = xml.GetAttribute("na1") ?? "",
            GeneralCategory = xml.GetAttribute("gc") ?? "Cn",
            CanonicalCombiningClass = ParseInt(xml.GetAttribute("ccc"), 0),
            BidiClass = xml.GetAttribute("bc") ?? "L",
            BidiMirrored = (xml.GetAttribute("Bidi_M") ?? "N") == "Y",
            BidiMirroringGlyph = ParseHexOrZero(xml.GetAttribute("bmg")),
            BracketType = xml.GetAttribute("bpt") ?? "n",
            BracketPair = ParseHexOrZero(xml.GetAttribute("bpb")),
            DecompositionType = xml.GetAttribute("dt") ?? "none",
            DecompositionMapping = xml.GetAttribute("dm") ?? "",
            CompositionExclusion = (xml.GetAttribute("Comp_Ex") ?? "N") == "Y",
            NumericType = xml.GetAttribute("nt") ?? "None",
            NumericValue = xml.GetAttribute("nv") ?? "",
            SimpleUppercase = ParseHashHexOrZero(xml.GetAttribute("suc")),
            SimpleLowercase = ParseHashHexOrZero(xml.GetAttribute("slc")),
            SimpleTitlecase = ParseHashHexOrZero(xml.GetAttribute("stc")),
            SimpleCaseFolding = ParseHashHexOrZero(xml.GetAttribute("scf")),
            FullUppercase = xml.GetAttribute("uc") ?? "",
            FullLowercase = xml.GetAttribute("lc") ?? "",
            FullTitlecase = xml.GetAttribute("tc") ?? "",
            FullCaseFolding = xml.GetAttribute("cf") ?? "",
            JoiningType = xml.GetAttribute("jt") ?? "U",
            JoiningGroup = xml.GetAttribute("jg") ?? "No_Joining_Group",
            EastAsianWidth = xml.GetAttribute("ea") ?? "N",
            LineBreak = xml.GetAttribute("lb") ?? "XX",
            GraphemeClusterBreak = xml.GetAttribute("GCB") ?? "Other",
            WordBreak = xml.GetAttribute("WB") ?? "Other",
            SentenceBreak = xml.GetAttribute("SB") ?? "Other",
            Script = xml.GetAttribute("sc") ?? "Unknown",
            ScriptExtensions = (xml.GetAttribute("scx") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
            Block = xml.GetAttribute("blk") ?? "No_Block",
            Age = xml.GetAttribute("age") ?? "unassigned",
            HangulSyllableType = xml.GetAttribute("hst") ?? "NA",
            IndicSyllabicCategory = xml.GetAttribute("InSC") ?? "Other",
            IndicPositionalCategory = xml.GetAttribute("InPC") ?? "NA",
            IndicConjunctBreak = xml.GetAttribute("InCB") ?? "None",
            VerticalOrientation = xml.GetAttribute("vo") ?? "R",
            ExtendedPictographic = (xml.GetAttribute("ExtPict") ?? "N") == "Y",
            Emoji = (xml.GetAttribute("Emoji") ?? "N") == "Y",
            EmojiPresentation = (xml.GetAttribute("EPres") ?? "N") == "Y",
            EmojiModifier = (xml.GetAttribute("EMod") ?? "N") == "Y",
            EmojiBase = (xml.GetAttribute("EBase") ?? "N") == "Y",
            EmojiComponent = (xml.GetAttribute("EComp") ?? "N") == "Y",
            NfcQc = xml.GetAttribute("NFC_QC") ?? "Y",
            NfdQc = xml.GetAttribute("NFD_QC") ?? "Y",
            NfkcQc = xml.GetAttribute("NFKC_QC") ?? "Y",
            NfkdQc = xml.GetAttribute("NFKD_QC") ?? "Y",
            Cased = (xml.GetAttribute("Cased") ?? "N") == "Y",
            CaseIgnorable = (xml.GetAttribute("CI") ?? "N") == "Y",
        };
    }

    private static int ParseInt(string? s, int fallback)
        => int.TryParse(s, out int n) ? n : fallback;

    private static int ParseHexOrZero(string? s)
    {
        if (string.IsNullOrEmpty(s) || s == "#") { return 0; }
        return int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static int ParseHashHexOrZero(string? s)
    {
        if (string.IsNullOrEmpty(s) || s == "#") { return 0; }
        // Simple case mappings are always one codepoint (UAX #42 §4.2.6).
        int spaceIdx = s.IndexOf(' ');
        string first = spaceIdx > 0 ? s.Substring(0, spaceIdx) : s;
        return int.Parse(first, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _xml.Dispose();
        if (_ownsStream) { _stream.Dispose(); }
        _zip?.Dispose();
    }
}
