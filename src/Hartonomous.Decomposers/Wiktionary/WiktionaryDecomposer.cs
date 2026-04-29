using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Data;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Hartonomous.Core.Text.Segmentation;
using Hartonomous.Decomposers.Text;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Wiktionary;

/// <summary>
/// Streams the wiktextract JSONL into the substrate with the full semantic surface:
/// <list type="bullet">
///   <item>lemma entities + wikt_sense entities (content-hashed by gloss text)</item>
///   <item>has_sense, has_gloss, has_example edges</item>
///   <item>has_etymology, has_pronunciation, has_hyphenation, has_wikidata edges
///     (wikt_sense → document; text routed through TextDecomposer)</item>
///   <item>has_form edges (lemma → inflected_form), inflection_of edges
///     (inflected_form → lemma)</item>
///   <item>translation_of edges (wikt_sense → foreign-language lemma) — produces
///     foreign lemma entities by Merkle identity, with entity_language junction
///     attached per WiktTranslation.LangCode</item>
///   <item>Eight semantic-relation edge types: synonym, antonym, hypernym, hyponym,
///     meronym, coordinate_term, derived, related (lemma → lemma, with shared
///     hypernym/hyponym/meronym/antonym codes converging with WordNet)</item>
///   <item>Eight etymology-template edge types: etym_inherited_from /
///     etym_derived_from / etym_borrowed_from / etym_cognate_with /
///     etym_calque_of / etym_mention / etym_link / etym_etymon</item>
///   <item>entity_pos / entity_language junctions on every lemma, source_authority
///     significance on every emitted entity</item>
/// </list>
/// Hash-FK shape end-to-end: no Channel.CreateBounded, no Parallel.ForEachAsync,
/// no decomposer-owned ResolveEntityIdsAsync. Streaming line-by-line; pipeline
/// owns batching/transaction/parallelism.
///
/// Cross-lingual translations and etymon links emit non-English lemma entities
/// even under the T0 English LanguageFilter — the filter applies to the SOURCE
/// entry's language, not to the TARGETS of the cross-lingual edges. (Foreign
/// lemmas pointed-at by translations carry their own entity_language junction
/// tagging the target language code.)
/// </summary>
public sealed partial class WiktionaryDecomposer : BaseDecomposer
{
    public override string ProvenanceCode => "wiktextract";
    public override string DisplayName => "Wiktionary (wiktextract JSONL)";
    public override IReadOnlyList<Phase> Phases => [Phase.Wiktionary];

    private const double TrustPriorMu = 68000.0;

    private readonly string _jsonlPath;
    private readonly ICodepointProperties _codepointProperties;
    private readonly IReferenceDataReader? _referenceDataReader;
    private readonly IJunctionWriter? _junctionWriter;
    private readonly IReferenceDataWriter? _referenceDataWriter;

    public WiktionaryDecomposer(
        DecomposerConfig config,
        ILogger<WiktionaryDecomposer> logger,
        ICodepointProperties codepointProperties,
        IReferenceDataReader? referenceDataReader = null,
        IJunctionWriter? junctionWriter = null,
        IReferenceDataWriter? referenceDataWriter = null)
        : base(config, logger)
    {
        _jsonlPath = ResolveJsonlPath(config.SourceDirectory);
        _codepointProperties = codepointProperties;
        _referenceDataReader = referenceDataReader;
        _junctionWriter = junctionWriter;
        _referenceDataWriter = referenceDataWriter;
    }

    protected override IReadOnlyList<string> GetSourcePaths() => [_jsonlPath];

    private static string ResolveJsonlPath(string configured)
    {
        if (File.Exists(configured))
        {
            return configured;
        }
        return Path.Combine(configured, "raw-wiktextract-data.jsonl");
    }

    protected override async Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        WiktionaryReferenceTableWriter refWriter =
            new(_referenceDataReader!, _junctionWriter!, _referenceDataWriter!);
        try
        {
            Dictionary<string, int> posIdMap = await refWriter.LoadPosMapAsync(ct);
            Dictionary<string, int> langIdMap = await refWriter.LoadLanguageCodeMapAsync(ct);

            int? engLangId =
                langIdMap.TryGetValue("eng", out int e3) ? e3
                : langIdMap.TryGetValue("en",  out int e1) ? e1
                : (int?)null;

            long entryCount = 0;
            long entityCount = 0;
            long edgeCount = 0;
            int batchNum = 0;
            Dictionary<string, long> edgeCountByType = new(StringComparer.Ordinal);

            IIngestionBatch batch = pipeline.CreateBatch();

            async Task FlushBatchAsync()
            {
                if (batch.EntityCount == 0 && batch.EdgeCount == 0)
                {
                    return;
                }
                batchNum++;
                await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount,
                    batchNum, "wiktionary", ct);
                batch = pipeline.CreateBatch();
            }

            void BumpEdge(string code)
            {
                edgeCountByType.TryGetValue(code, out long current);
                edgeCountByType[code] = current + 1;
                edgeCount++;
            }

            // Resolve a non-English language by code → id, returning null if the language
            // is not in substrate.language master. (Wiktionary uses ISO 639-1 / 639-3 mix.)
            int? ResolveLangId(string? langCode)
            {
                if (string.IsNullOrEmpty(langCode))
                {
                    return null;
                }
                return langIdMap.TryGetValue(langCode, out int id) ? id : (int?)null;
            }

            foreach (WiktEntry entry in WiktionaryJsonlParser.Parse(_jsonlPath))
            {
                ct.ThrowIfCancellationRequested();
                entryCount++;

                if (!LanguageAllowed(entry.LangCode))
                {
                    continue;
                }
                if (string.IsNullOrEmpty(entry.Word) || entry.Senses.Count == 0)
                {
                    continue;
                }

                (EntityHandle lemmaHandle, _, _) =
                    EmitLemmaMaybeCompound(batch, entry.Word, ProvenanceCode);
                batch.AddSignificance(lemmaHandle, "source_authority", TrustPriorMu);
                entityCount++;

                string? upos = WiktPosMap.ToUpos(entry.Pos);
                if (upos is not null && posIdMap.TryGetValue(upos, out int posId))
                {
                    batch.AddJunction("entity_pos", lemmaHandle, posId, TrustPriorMu);
                }
                if (engLangId is int langId)
                {
                    batch.AddJunction("entity_language", lemmaHandle, langId);
                }

                // ── Forms (lemma → word_form, plus inflection_of back-edge). ──
                // Inflected forms ARE word_forms — content-addressed by their
                // UTF-8 bytes. The inflection relationship lives on the
                // inflection_of edge, not on a separate entity type.
                foreach (WiktForm form in entry.Forms)
                {
                    if (string.IsNullOrEmpty(form.Form) || form.Form == entry.Word)
                    {
                        continue;
                    }
                    (EntityHandle infHandle, _, _) =
                        EmitWordFormMerkle(batch, form.Form, "word_form");
                    batch.AddSignificance(infHandle, "source_authority", TrustPriorMu);
                    entityCount++;

                    if (engLangId is int infLang)
                    {
                        batch.AddJunction("entity_language", infHandle, infLang);
                    }

                    batch.AddEdge("has_form", ProvenanceCode,
                    [
                        new EdgeMemberSpec(lemmaHandle, "source", 0),
                        new EdgeMemberSpec(infHandle,   "target", 1),
                    ]);
                    BumpEdge("has_form");

                    batch.AddEdge("inflection_of", ProvenanceCode,
                    [
                        new EdgeMemberSpec(infHandle,   "source", 0),
                        new EdgeMemberSpec(lemmaHandle, "target", 1),
                    ]);
                    BumpEdge("inflection_of");
                }

                // ── Hyphenations (lemma scope). Each hyphenation pattern is text
                // content; routed through TextDecomposer for canonical Merkle dedup. ──
                foreach (WiktHyphenation hyph in entry.Hyphenations)
                {
                    string repr = HyphenationRepresentation(hyph);
                    if (string.IsNullOrEmpty(repr))
                    {
                        continue;
                    }
                    EntityHandle hyphDoc = IngestText(batch, repr);
                    // The has_hyphenation edge is declared wikt_sense → text_composition
                    // in seed; for entry-level hyphenations we use lemma as the source.
                    // The substrate.edge_member only requires a valid (entity_type_id,
                    // entity_hash) pair on each end, so this is structurally fine.
                    batch.AddEdge("has_hyphenation", ProvenanceCode,
                    [
                        new EdgeMemberSpec(lemmaHandle, "source", 0),
                        new EdgeMemberSpec(hyphDoc,     "target", 1),
                    ]);
                    BumpEdge("has_hyphenation");
                }

                // ── Pronunciations (lemma scope). One has_pronunciation edge per
                // distinct phonetic / audio attribute; ipa, enpr, audio file refs all
                // route through TextDecomposer so the same IPA string from any source
                // collapses to one document entity. ──
                foreach (WiktSound sound in entry.Sounds)
                {
                    foreach (string attr in EnumerateSoundAttrs(sound))
                    {
                        EntityHandle pDoc = IngestText(batch, attr);
                        batch.AddEdge("has_pronunciation", ProvenanceCode,
                        [
                            new EdgeMemberSpec(lemmaHandle, "source", 0),
                            new EdgeMemberSpec(pDoc,        "target", 1),
                        ]);
                        BumpEdge("has_pronunciation");
                    }
                }

                // ── Entry-level etymology text → has_etymology edge. ──
                EntityHandle? etymologyDoc = null;
                if (!string.IsNullOrEmpty(entry.EtymologyText))
                {
                    etymologyDoc = IngestText(batch, entry.EtymologyText);
                    batch.AddEdge("has_etymology", ProvenanceCode,
                    [
                        new EdgeMemberSpec(lemmaHandle,       "source", 0),
                        new EdgeMemberSpec(etymologyDoc.Value, "target", 1),
                    ]);
                    BumpEdge("has_etymology");
                }

                // ── Etymology templates: 8 distinct edge types based on Name. ──
                // Templates carry args[1] = source-language code, args[2] = source-word,
                // args[3] = gloss. We emit a foreign lemma entity for the source word
                // and an etym_<kind> edge from the entry-level lemma. (When the wikt_sense
                // for this entry isn't yet decided — etymology in wiktextract is entry-level
                // not sense-level — we anchor at the lemma. Once Wiktionary moves to per-
                // sense etymology in T2, anchor at wikt_sense instead.)
                foreach (WiktEtymologyTemplate t in entry.EtymologyTemplates)
                {
                    string? edgeCode = EtymologyTemplateNameToEdgeCode(t.Name);
                    if (edgeCode is null)
                    {
                        continue;
                    }

                    // Source word lives in args["2"] (or "1" for some template variants).
                    string? srcWord = ArgOrNull(t.Args, "2") ?? ArgOrNull(t.Args, "1");
                    string? srcLang = ArgOrNull(t.Args, "1");
                    if (string.IsNullOrEmpty(srcWord))
                    {
                        // Template carries no concrete word — emit edge to a document
                        // composed of the expansion text (still semantic content).
                        if (!string.IsNullOrEmpty(t.Expansion))
                        {
                            EntityHandle expansionDoc = IngestText(batch, t.Expansion);
                            batch.AddEdge(edgeCode, ProvenanceCode,
                            [
                                new EdgeMemberSpec(lemmaHandle,   "source", 0),
                                new EdgeMemberSpec(expansionDoc,  "target", 1),
                            ]);
                            BumpEdge(edgeCode);
                        }
                        continue;
                    }

                    (EntityHandle srcLemma, _, _) =
                        EmitLemmaMaybeCompound(batch, srcWord, ProvenanceCode);
                    batch.AddSignificance(srcLemma, "source_authority", TrustPriorMu);
                    entityCount++;
                    int? srcLangId = ResolveLangId(srcLang);
                    if (srcLangId is int sl)
                    {
                        batch.AddJunction("entity_language", srcLemma, sl);
                    }

                    batch.AddEdge(edgeCode, ProvenanceCode,
                    [
                        new EdgeMemberSpec(lemmaHandle, "source", 0),
                        new EdgeMemberSpec(srcLemma,    "target", 1),
                    ]);
                    BumpEdge(edgeCode);
                }

                // ── Translations (wikt_sense → foreign lemma; cross_lingual). ──
                foreach (WiktTranslation tr in entry.Translations)
                {
                    if (string.IsNullOrEmpty(tr.Word))
                    {
                        continue;
                    }
                    (EntityHandle foreignLemma, _, _) =
                        EmitLemmaMaybeCompound(batch, tr.Word, ProvenanceCode);
                    batch.AddSignificance(foreignLemma, "source_authority", TrustPriorMu);
                    entityCount++;
                    int? trLangId = ResolveLangId(tr.LangCode);
                    if (trLangId is int tl)
                    {
                        batch.AddJunction("entity_language", foreignLemma, tl);
                    }

                    // translation_of: wikt_sense → lemma. We anchor at the source-language
                    // lemma since per-sense translation lift is out of T0 scope. T1 will
                    // anchor at the matched wikt_sense via WiktTranslation.Sense.
                    batch.AddEdge("translation_of", ProvenanceCode,
                    [
                        new EdgeMemberSpec(lemmaHandle,   "source", 0),
                        new EdgeMemberSpec(foreignLemma,  "target", 1),
                    ]);
                    BumpEdge("translation_of");
                }

                // ── Entry-level semantic relations (lemma → lemma). ──
                EmitRelations(batch, lemmaHandle, entry.Synonyms,        "synonym",         BumpEdge, ResolveLangId, ref entityCount);
                EmitRelations(batch, lemmaHandle, entry.Antonyms,        "antonym",         BumpEdge, ResolveLangId, ref entityCount);
                EmitRelations(batch, lemmaHandle, entry.Hypernyms,       "hypernym",        BumpEdge, ResolveLangId, ref entityCount);
                EmitRelations(batch, lemmaHandle, entry.Hyponyms,        "hyponym",         BumpEdge, ResolveLangId, ref entityCount);
                EmitRelations(batch, lemmaHandle, entry.Meronyms,        "member_meronym",  BumpEdge, ResolveLangId, ref entityCount);
                EmitRelations(batch, lemmaHandle, entry.CoordinateTerms, "coordinate_term", BumpEdge, ResolveLangId, ref entityCount);
                EmitRelations(batch, lemmaHandle, entry.Derived,         "derived",         BumpEdge, ResolveLangId, ref entityCount);
                EmitRelations(batch, lemmaHandle, entry.Related,         "related",         BumpEdge, ResolveLangId, ref entityCount);

                // ── wikt_sense entities + has_sense + has_gloss + has_example. ──
                foreach (WiktSense sense in entry.Senses)
                {
                    if (sense.Glosses.Count == 0)
                    {
                        continue;
                    }

                    string joinedGloss = string.Join("\n", sense.Glosses);
                    if (joinedGloss.Length == 0)
                    {
                        continue;
                    }

                    // wikt_sense entity removed — sense is the lemma's
                    // gloss/example/wikidata metadata, attached directly to
                    // the lemma. Multi-sense lemmas get N has_gloss edges
                    // (one per Wiktionary sense), each pointing to a different
                    // text_composition. Provenance=wiktextract on every edge
                    // distinguishes Wiktionary's sense data from any other
                    // dictionary's.
                    EntityHandle glossDoc = IngestText(batch, joinedGloss);
                    batch.AddEdge("has_gloss", ProvenanceCode,
                    [
                        new EdgeMemberSpec(lemmaHandle, "source", 0),
                        new EdgeMemberSpec(glossDoc,    "target", 1),
                    ]);
                    BumpEdge("has_gloss");

                    foreach (WiktExample ex in sense.Examples)
                    {
                        if (string.IsNullOrEmpty(ex.Text))
                        {
                            continue;
                        }
                        EntityHandle exDoc = IngestText(batch, ex.Text);
                        batch.AddEdge("has_example", ProvenanceCode,
                        [
                            new EdgeMemberSpec(lemmaHandle, "source", 0),
                            new EdgeMemberSpec(exDoc,       "target", 1),
                        ]);
                        BumpEdge("has_example");
                    }

                    foreach (string wd in sense.Wikidata)
                    {
                        if (string.IsNullOrEmpty(wd))
                        {
                            continue;
                        }
                        EntityHandle wdDoc = IngestText(batch, wd);
                        batch.AddEdge("has_wikidata", ProvenanceCode,
                        [
                            new EdgeMemberSpec(lemmaHandle, "source", 0),
                            new EdgeMemberSpec(wdDoc,       "target", 1),
                        ]);
                        BumpEdge("has_wikidata");
                    }
                }

                if (batch.EntityCount >= BatchSize || batch.EdgeCount >= BatchSize)
                {
                    await FlushBatchAsync();
                }

                if (entryCount % 250_000 == 0)
                {
                    Log.EntriesProcessed(Logger, entryCount, entityCount, edgeCount);
                }
            }

            await FlushBatchAsync();

            foreach (KeyValuePair<string, long> kv in edgeCountByType)
            {
                Log.EdgesByType(Logger, kv.Key, kv.Value);
            }
            Log.DecompositionComplete(Logger, entryCount, entityCount, edgeCount);
        }
        finally
        {
            await refWriter.DisposeAsync();
        }
    }

    private void EmitRelations(
        IIngestionBatch batch,
        EntityHandle source,
        IReadOnlyList<WiktRelation> relations,
        string edgeCode,
        Action<string> bump,
        Func<string?, int?> resolveLang,
        ref long entityCount)
    {
        foreach (WiktRelation rel in relations)
        {
            if (string.IsNullOrEmpty(rel.Word) || rel.Word == "—")
            {
                continue;
            }
            (EntityHandle target, _, _) =
                EmitLemmaMaybeCompound(batch, rel.Word, ProvenanceCode);
            batch.AddSignificance(target, "source_authority", TrustPriorMu);
            entityCount++;

            // If WiktRelation carried a language code on a target headword,
            // attach an entity_language junction. wiktextract's relations are
            // implicitly same-language as the entry, so we don't currently have
            // a per-relation lang field — this is a no-op until the parser is
            // extended.
            _ = resolveLang;

            batch.AddEdge(edgeCode, ProvenanceCode,
            [
                new EdgeMemberSpec(source, "source", 0),
                new EdgeMemberSpec(target, "target", 1),
            ]);
            bump(edgeCode);
        }
    }

    /// <summary>
    /// Map a Wiktionary etymology template Name to the canonical substrate
    /// edge_type code. Returns null for templates we don't represent (the
    /// long tail of wiktextract templates is huge; we cover the eight
    /// load-bearing kinds).
    /// </summary>
    private static string? EtymologyTemplateNameToEdgeCode(string name) => name switch
    {
        "inh"          => "etym_inherited_from",
        "der"          => "etym_derived_from",
        "bor"          => "etym_borrowed_from",
        "cog"          => "etym_cognate_with",
        "cal"          => "etym_calque_of",
        "m" or "mention"  => "etym_mention",
        "l" or "link"     => "etym_link",
        "etymon"       => "etym_etymon",
        _ => null,
    };

    private static string? ArgOrNull(IReadOnlyDictionary<string, string> args, string key) =>
        args.TryGetValue(key, out string? v) ? v : null;

    private static IEnumerable<string> EnumerateSoundAttrs(WiktSound s)
    {
        if (!string.IsNullOrEmpty(s.Ipa)) { yield return s.Ipa; }
        if (!string.IsNullOrEmpty(s.Enpr)) { yield return s.Enpr; }
        if (!string.IsNullOrEmpty(s.Audio)) { yield return s.Audio; }
        if (!string.IsNullOrEmpty(s.OggUrl)) { yield return s.OggUrl; }
        if (!string.IsNullOrEmpty(s.Mp3Url)) { yield return s.Mp3Url; }
    }

    private static string HyphenationRepresentation(WiktHyphenation hyph)
    {
        // U+00B7 MIDDLE DOT is the canonical hyphenation separator used by
        // Wiktionary's display layer (e.g., "ex·am·ple"). Joining parts with
        // it produces a deterministic surface representation that converges
        // by Merkle identity with any other ingestion of the same hyphenation.
        if (hyph.Parts.Count == 0)
        {
            return string.Empty;
        }
        return string.Join('·', hyph.Parts);
    }

    private EntityHandle IngestText(IIngestionBatch batch, string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        TextDecomposer.TextIngestionResult r = TextDecomposer.IngestUtf8DocumentIntoBatch(
            batch, utf8, _codepointProperties, TrustPriorMu, Logger, CancellationToken.None);
        return r.DocumentHandle;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Wiktionary: {Entries} entries processed, {Entities} entities, {Edges} edges")]
        public static partial void EntriesProcessed(ILogger logger, long entries, long entities, long edges);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Edges by type: {Code}={Count}")]
        public static partial void EdgesByType(ILogger logger, string code, long count);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Wiktionary complete: {Entries} entries scanned, {Entities} entities, {Edges} edges")]
        public static partial void DecompositionComplete(ILogger logger, long entries, long entities, long edges);
    }
}
