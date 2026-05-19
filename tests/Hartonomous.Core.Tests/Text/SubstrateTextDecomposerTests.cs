using System;
using System.Collections.Generic;
using System.Linq;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Native;
using Hartonomous.Core.Text;

namespace Hartonomous.Core.Tests.Text;

public sealed class SubstrateTextDecomposerTests
{
    [Fact]
    public void EmitStatic_SingleWordTopEntity_EmitsRootContourAndCentroid()
    {
        if (!TryUseNativeTextDecomposer())
        {
            return;
        }

        RecordingBatch batch = new();
        TextDecomposeOptions options = new("princeton_wordnet", "lemma", 1000.0);

        TextDecomposeResult result = SubstrateTextDecomposer.EmitStatic(batch, "dog"u8, options);

        Assert.Equal(8, result.PhysicalityRowsEmitted);
        Assert.NotEqual((0d, 0d, 0d, 0d), result.RootCentroid);
        // 3-role physicality_type vocabulary: entity / firefly / content. The
        // root carries either depending on whether top_kind is entity-tier
        // (codepoint / grapheme_cluster / word_form / morpheme / lemma /
        // synset / language_name) or content-tier (text_composition /
        // paragraph / document / audio_recording / audio_chunk /
        // pixel_region / video_frame). Lemma falls through to the native
        // text_composition kernel branch — content-tier — until a dedicated
        // KindLemma lands.
        Assert.Contains(batch.Physicalities, row =>
            (row.Type == "entity" || row.Type == "content")
            && row.Entity.Hash.Equals(result.RootHash));
    }

    [Fact]
    public void EmitStatic_WordFormHash_DeterministicAcrossContextVariants()
    {
        if (!TryUseNativeTextDecomposer())
        {
            return;
        }

        TextDecomposeOptions options = new("user_session", "word_form", 50000.0);

        Hash32 bare    = SubstrateTextDecomposer.EmitStatic(new RecordingBatch(), "he"u8, options).RootHash;
        Hash32 trail   = SubstrateTextDecomposer.EmitStatic(new RecordingBatch(), "he "u8, options).RootHash;
        Hash32 lead    = SubstrateTextDecomposer.EmitStatic(new RecordingBatch(), " he"u8, options).RootHash;
        Hash32 newline = SubstrateTextDecomposer.EmitStatic(new RecordingBatch(), "he\n"u8, options).RootHash;

        Assert.Equal(bare, trail);
        Assert.Equal(bare, lead);
        Assert.Equal(bare, newline);
    }

    [Fact]
    public void EmitStatic_TextCompositionHash_DiffersAcrossContextVariants()
    {
        if (!TryUseNativeTextDecomposer())
        {
            return;
        }

        TextDecomposeOptions options = new("user_session", "text_composition", 50000.0);

        Hash32 bare  = SubstrateTextDecomposer.EmitStatic(new RecordingBatch(), "he"u8, options).RootHash;
        Hash32 trail = SubstrateTextDecomposer.EmitStatic(new RecordingBatch(), "he "u8, options).RootHash;

        Assert.NotEqual(bare, trail);
    }

    [Fact]
    public void EmitStatic_WordForm_StillEmitsPerWordRecords()
    {
        // After the kernel fix that branches root-hash selection per top_kind,
        // make sure word_form requests STILL emit per-word entity records into
        // the batch (the kernel's per-word emission loop is independent of
        // root-hash selection). Decomposer passes that rely on EmitStatic
        // populating the batch with per-word records (e.g. AttentionBlockTuplePass
        // via the tokenizer-vocab loop) would silently fail if this regressed.
        if (!TryUseNativeTextDecomposer())
        {
            return;
        }

        RecordingBatch batch = new();
        TextDecomposeOptions options = new("user_session", "word_form", 50000.0);
        TextDecomposeResult result = SubstrateTextDecomposer.EmitStatic(batch, "token0"u8, options);

        Assert.NotEqual(default(Hash32), result.RootHash);
        Assert.NotEmpty(batch.Entities);  // codepoint + grapheme + word_form entities all emitted
        Assert.Contains(batch.Entities, e => e.Type == "word_form");
    }

    [Fact]
    public void UcdTablesReady_WhenNativeTextDecomposerUsable_ReturnsTrue()
    {
        if (!TryUseNativeTextDecomposer())
        {
            return;
        }

        Assert.Equal(1, TextDecomposeNative.UcdCatalogReady());
        Assert.Equal(1, TextDecomposeNative.UcdTablesReady());
    }

    /// <summary>
    /// Gate-1 #37 lock-in: raw text decomposition has no basis for POS / sense /
    /// language attestation. Those come exclusively from observer sources (UD,
    /// WordNet, OMW, Wiktionary). The "rake noun vs verb" disambiguation lives
    /// on edges those sources attest — not on edges raw text invents.
    ///
    /// If a future change makes <c>SubstrateTextDecomposer</c> (or the native
    /// <c>hartonomous_text_decompose</c>) emit any <c>has_pos</c> /
    /// <c>has_sense</c> / <c>has_language</c> / <c>has_morph_feature</c> /
    /// <c>has_lexname</c> / <c>has_deprel</c> edge — or any
    /// <c>entity_pos</c> / <c>entity_language</c> / <c>entity_morph_feature</c>
    /// / <c>entity_lexname</c> junction — this test fails with the exact
    /// emission recorded.
    /// </summary>
    [Fact]
    public void EmitStatic_RawText_EmitsNoPosSenseOrLanguageRecords()
    {
        if (!TryUseNativeTextDecomposer())
        {
            return;
        }

        ClassificationProbingBatch batch = new();
        TextDecomposeOptions options = new("user_session", "text_composition", 1500.0);

        SubstrateTextDecomposer.EmitStatic(batch, "rake the rakes"u8, options);

        Assert.Empty(batch.Edges);
        Assert.Empty(batch.Junctions);
    }

    private sealed class ClassificationProbingBatch : IIngestionBatch
    {
        public string ProvenanceCode { get; } = "user_session";
        public List<(Hash32 Hash, string Type)> Entities { get; } = [];
        public List<(EntityHandle Entity, string Type)> Physicalities { get; } = [];
        public List<string> Edges { get; } = [];
        public List<string> Junctions { get; } = [];
        public int EntityCount => Entities.Count;
        public int EdgeCount => Edges.Count;

        public EntityHandle AddEntity(Hash32 hash, string entityTypeCode)
        {
            Entities.Add((hash, entityTypeCode));
            return new EntityHandle(hash, entityTypeCode);
        }

        public void AddPhysicality(EntityHandle entity, string physicalityTypeCode, byte[] geomWkb)
            => Physicalities.Add((entity, physicalityTypeCode));

        public void AddSignificance(EntityHandle entity, string contextTypeCode, double initialMu, string attestationTypeCode = "provenance_authority_corroboration")
        {
        }

        public void AddCompositionChild(EntityHandle parent, int ordinal, EntityHandle child, int rleCount = 1)
        {
        }

        public void AddEdge(string edgeTypeCode, string provenanceCode, ReadOnlySpan<EdgeMemberSpec> members)
            => Edges.Add(edgeTypeCode);

        public void AddJunction(string junctionTable, EntityHandle entity, int referenceId, double? mu = null, string attestationTypeCode = "lexical_curated_relation")
            => Junctions.Add(junctionTable);

        public void AddPhysicalityPoint4d(EntityHandle entity, string physicalityTypeCode, double x1, double x2, double x3, double x4)
        {
        }

        public void AddPhysicalityLineString4d(EntityHandle entity, string physicalityTypeCode, ReadOnlySpan<(double X1, double X2, double X3, double X4)> vertices)
        {
        }

        public void AddEntityModelSource(EntityHandle entity, long modelSourceId)
        {
        }
    }

    [Fact]
    public void ComputeRootHash_WordForm_OneByteAscii_GuardsAndFailures()
    {
        if (!TryUseNativeTextDecomposer())
        {
            return;
        }

        List<(byte B, int Rc, bool DotnetWs)> failures = [];
        for (int b = 0; b < 128; b++)
        {
            byte[] one = [(byte)b];
            string s = System.Text.Encoding.UTF8.GetString(one);
            bool ws = string.IsNullOrWhiteSpace(s);
            try
            {
                Hash32 _ = SubstrateTextDecomposer.ComputeRootHash(one.AsSpan(), "word_form");
            }
            catch (InvalidOperationException ex)
            {
                int rc = ExtractReturnCode(ex.Message);
                failures.Add(((byte)b, rc, ws));
            }
        }

        // Every failing byte must already be caught by string.IsNullOrWhiteSpace
        // at the caller — otherwise the Wiktionary decomposer's filter is leaky
        // and -10 propagates past the try/catch barrier on some path we missed.
        List<(byte B, int Rc, bool DotnetWs)> leaks = failures.Where(f => !f.DotnetWs).ToList();
        Assert.True(
            leaks.Count == 0,
            "1-byte inputs that fail ComputeRootHash but pass IsNullOrWhiteSpace: " +
            string.Join(", ", leaks.Select(l => $"0x{l.B:X2}(rc={l.Rc})")));
    }

    private static int ExtractReturnCode(string message)
    {
        int idx = message.IndexOf("returned ", StringComparison.Ordinal);
        if (idx < 0) { return 0; }
        int start = idx + "returned ".Length;
        int end = start;
        if (end < message.Length && message[end] == '-') { end++; }
        while (end < message.Length && char.IsDigit(message[end])) { end++; }
        return int.TryParse(message.AsSpan(start, end - start), out int rc) ? rc : 0;
    }

    private static bool TryUseNativeTextDecomposer()
    {
        try
        {
            SubstrateTextDecomposer.EnsureUcdLoaded();
            return true;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("could not locate the UCD blob", StringComparison.Ordinal))
        {
            return false;
        }
    }

    private sealed class RecordingBatch : IIngestionBatch
    {
        public string ProvenanceCode { get; } = "test";
        public List<(Hash32 Hash, string Type)> Entities { get; } = [];
        public List<(EntityHandle Entity, string Type, byte[] Wkb)> Physicalities { get; } = [];
        public int EntityCount => Entities.Count;
        public int EdgeCount => 0;

        public EntityHandle AddEntity(Hash32 hash, string entityTypeCode)
        {
            Entities.Add((hash, entityTypeCode));
            return new EntityHandle(hash, entityTypeCode);
        }

        public void AddPhysicality(EntityHandle entity, string physicalityTypeCode, byte[] geomWkb)
            => Physicalities.Add((entity, physicalityTypeCode, geomWkb));

        public void AddSignificance(EntityHandle entity, string contextTypeCode, double initialMu, string attestationTypeCode = "provenance_authority_corroboration")
        {
        }

        public void AddCompositionChild(EntityHandle parent, int ordinal, EntityHandle child, int rleCount = 1)
        {
        }

        public void AddEdge(string edgeTypeCode, string provenanceCode, ReadOnlySpan<EdgeMemberSpec> members)
            => throw new NotSupportedException();

        public void AddJunction(string junctionTable, EntityHandle entity, int referenceId, double? mu = null, string attestationTypeCode = "lexical_curated_relation")
            => throw new NotSupportedException();

        public void AddPhysicalityPoint4d(EntityHandle entity, string physicalityTypeCode, double x1, double x2, double x3, double x4)
            => throw new NotSupportedException();

        public void AddPhysicalityLineString4d(EntityHandle entity, string physicalityTypeCode, ReadOnlySpan<(double X1, double X2, double X3, double X4)> vertices)
            => throw new NotSupportedException();

        public void AddEntityModelSource(EntityHandle entity, long modelSourceId)
            => throw new NotSupportedException();
    }
}
