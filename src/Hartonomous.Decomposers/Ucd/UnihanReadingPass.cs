using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Producer pass for CJK Unihan per-language readings:
///   unihan_reading  (codepoint → text_composition)
///
/// Parses ucd.unihan.flat.xml (extracted from ucd.unihan.flat.zip). For each
/// CJK codepoint with kMandarin / kCantonese / kJapanese / kVietnamese
/// attributes, emits one unihan_reading edge per (codepoint, language, reading)
/// triple. Each language's attestation fires under a per-language provenance
/// (unihan_kmandarin / unihan_kcantonese / unihan_kjapanese / unihan_kvietnamese)
/// so cross-language consensus accumulates on shared edge identities for chars
/// that have the same reading across languages (e.g. kanji ↔ hanja shared readings).
/// </summary>
internal sealed partial class UnihanReadingPass : IUnicodeSeedPass
{
    private const int BatchFlushSize = 25_000;
    private const string UcdNs = "http://www.unicode.org/ns/2003/ucd/1.0";

    public string PassId => "unicode.unihan_readings";
    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        string? zipPath = UnicodeConfusablePass.ResolveSource(context.SourceDirectory, "ucdxml", "ucd.unihan.flat.zip");
        if (zipPath is null)
        {
            Log.SourceMissing(context.Logger, "ucdxml/ucd.unihan.flat.zip");
            return;
        }

        // Per-language sub-passes — each fires under its own provenance so
        // cross-language consensus accumulates correctly on shared readings.
        long totalEdges = 0;
        long totalComps = 0;
        (string attr, string provenance)[] readings =
        [
            ("kMandarin",   "unihan_kmandarin"),
            ("kCantonese",  "unihan_kcantonese"),
            ("kJapaneseOn", "unihan_kjapanese"),
            ("kJapaneseKun","unihan_kjapanese"),
            ("kVietnamese", "unihan_kvietnamese"),
        ];

        using FileStream fs = File.OpenRead(zipPath);
        using ZipArchive zip = new(fs, ZipArchiveMode.Read);
        ZipArchiveEntry? entry = null;
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            if (e.FullName.EndsWith(".xml", System.StringComparison.OrdinalIgnoreCase))
            { entry = e; break; }
        }
        if (entry is null)
        {
            Log.SourceMissing(context.Logger, $"{zipPath} (no .xml entry)");
            return;
        }

        IIngestionBatch batch = context.Pipeline.CreateBatch(context.ProvenanceCode);
        using Stream xmlStream = entry.Open();
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true, IgnoreComments = true };
        using XmlReader xml = XmlReader.Create(xmlStream, settings);

        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element || xml.NamespaceURI != UcdNs) { continue; }
            if (xml.LocalName != "char") { continue; }

            string? cpAttr = xml.GetAttribute("cp");
            if (cpAttr is null) { continue; }
            int cp = int.Parse(cpAttr, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            Hash32 srcHash = Blake3.HashCodepoint(cp);
            EntityHandle srcHandle = new(srcHash, "codepoint");

            foreach ((string attrName, string provenance) in readings)
            {
                string? readingValue = xml.GetAttribute(attrName);
                if (string.IsNullOrWhiteSpace(readingValue)) { continue; }

                // Multiple readings per attribute are space-separated
                foreach (string reading in readingValue.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
                {
                    byte[] readingBytes = Encoding.UTF8.GetBytes(reading);
                    Hash32 readingHash = BaseDecomposer.ComputeHash(readingBytes);
                    EntityHandle readingHandle = batch.AddEntity(readingHash, "text_composition");
                    totalComps++;

                    EdgeMemberSpec[] members =
                    [
                        new EdgeMemberSpec(srcHandle, "source", 0),
                        new EdgeMemberSpec(readingHandle, "target", 1),
                    ];
                    batch.AddEdge("unihan_reading", provenance, members);
                    totalEdges++;
                }
            }

            if (batch.EntityCount + batch.EdgeCount >= BatchFlushSize)
            {
                await context.Pipeline.SubmitBatchAsync(batch, ct);
                batch = context.Pipeline.CreateBatch(context.ProvenanceCode);
            }
            ct.ThrowIfCancellationRequested();
        }

        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await context.Pipeline.SubmitBatchAsync(batch, ct);
        }

        Log.Materialized(context.Logger, totalEdges, totalComps);
        await context.ReportAsync(PassId, totalComps, totalEdges, ct);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Unihan reading edges: {Edges}; reading composition entities: {Comps}")]
        public static partial void Materialized(ILogger logger, long edges, long comps);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Source file not found, skipping: {File}")]
        public static partial void SourceMissing(ILogger logger, string file);
    }
}
