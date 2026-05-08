using System;
using System.Collections.Generic;
using System.Linq;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text;

namespace Hartonomous.Core.Tests.Text;

public sealed class SubstrateTextDecomposerTests
{
    [Fact]
    public void EmitStatic_SingleWordTopEntity_EmitsRootPointAndCentroid()
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
            row.Type == "s3_position"
            && row.Entity.Hash.AsSpan().SequenceEqual(result.RootHash));
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
        public List<(byte[] Hash, string Type)> Entities { get; } = [];
        public List<(EntityHandle Entity, string Type, byte[] Wkb)> Physicalities { get; } = [];
        public int EntityCount => Entities.Count;
        public int EdgeCount => 0;

        public EntityHandle AddEntity(byte[] hash, string entityTypeCode)
        {
            Entities.Add((hash, entityTypeCode));
            return new EntityHandle(hash, entityTypeCode);
        }

        public void AddPhysicality(EntityHandle entity, string physicalityTypeCode, byte[] geomWkb)
            => Physicalities.Add((entity, physicalityTypeCode, geomWkb));

        public void AddSignificance(EntityHandle entity, string contextTypeCode, double initialMu, string attestationTypeCode = "provenance_authority_corroboration")
        {
        }

        public void AddSequence(EntityHandle parent, int ordinal, EntityHandle child, int rleCount = 1)
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
