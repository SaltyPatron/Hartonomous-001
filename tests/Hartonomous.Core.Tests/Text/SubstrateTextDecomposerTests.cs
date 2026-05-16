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
        Assert.Contains(batch.Physicalities, row =>
            row.Type == "contour"
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
