using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Ingestion;
using Hartonomous.Decomposers.Safetensors;
using Hartonomous.Decomposers.Safetensors.Passes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hartonomous.Decomposers.Tests.Safetensors;

public sealed class NormalizationPrimitivePassTests
{
    [Fact]
    public async Task Run_OneNormTensor_EmitsContourPlusSignificancePlusModelSource()
    {
        // 1-D tensor of length 8 (γ scale vector). Contour packs 4-at-a-time
        // → vertex_count = ceil(8/4) = 2 vertices.
        float[] gammaValues = [1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f, 7.0f, 8.0f];
        using TinySafetensorsFile file = TinySafetensorsFile.CreateF32("encoder.layer.0.attention.output.LayerNorm.weight", [8], gammaValues);

        TensorHandle handle = MakeTensorHandle(file.Tensor, PrimitiveKind.Normalization);
        ModelPassContext ctx = MakeContext(handle);
        RecordingPassSession session = new();

        NormalizationPrimitivePass pass = new(NullLogger.Instance);
        await pass.RunAsync(ctx, session, CancellationToken.None);

        // One contour LINESTRINGZM physicality emitted on the tensor entity
        Assert.Single(session.Batch.PhysLines);
        var line = session.Batch.PhysLines[0];
        Assert.Equal(handle.Entity, line.Entity);
        Assert.Equal("contour", line.PhysType);
        Assert.Equal(2, line.Vertices.Length);
        Assert.Equal((1.0, 2.0, 3.0, 4.0), line.Vertices[0]);
        Assert.Equal((5.0, 6.0, 7.0, 8.0), line.Vertices[1]);

        // One entity_significance — sign-bearing positive_evidence per AP-38.
        // Domain discrimination is via (provenance × arena), not attestation_type.
        Assert.Single(session.Batch.Significances);
        var sig = session.Batch.Significances[0];
        Assert.Equal(handle.Entity, sig.Entity);
        Assert.Equal("model_trust", sig.Arena);
        Assert.Equal("positive_evidence", sig.AttestationType);

        // One entity_model_source linkage
        Assert.Single(session.Batch.ModelSources);
        Assert.Equal(handle.Entity, session.Batch.ModelSources[0].Entity);

        // No edges emitted (norms don't fire token-pair attestations)
        Assert.Empty(session.Batch.Edges);
    }

    [Fact]
    public async Task Run_NormVectorWithUnaligned4Length_PadsLastVertexWithZeros()
    {
        // Length 5 → ceil(5/4) = 2 vertices, second vertex padded with zeros
        float[] values = [10.0f, 20.0f, 30.0f, 40.0f, 50.0f];
        using TinySafetensorsFile file = TinySafetensorsFile.CreateF32("model.layers.0.input_layernorm.weight", [5], values);
        TensorHandle handle = MakeTensorHandle(file.Tensor, PrimitiveKind.Normalization);
        ModelPassContext ctx = MakeContext(handle);
        RecordingPassSession session = new();

        NormalizationPrimitivePass pass = new(NullLogger.Instance);
        await pass.RunAsync(ctx, session, CancellationToken.None);

        Assert.Single(session.Batch.PhysLines);
        var line = session.Batch.PhysLines[0];
        Assert.Equal(2, line.Vertices.Length);
        Assert.Equal((10.0, 20.0, 30.0, 40.0), line.Vertices[0]);
        Assert.Equal((50.0, 0.0, 0.0, 0.0), line.Vertices[1]);
    }

    [Fact]
    public async Task Run_NonNormalizationPrimitive_Skipped()
    {
        // A Linear-classified tensor should NOT fire normalization emission
        float[] values = [1.0f, 2.0f, 3.0f, 4.0f];
        using TinySafetensorsFile file = TinySafetensorsFile.CreateF32("model.layers.0.self_attn.q_proj.weight", [2, 2], values);
        TensorHandle handle = MakeTensorHandle(file.Tensor, PrimitiveKind.Linear);
        ModelPassContext ctx = MakeContext(handle);
        RecordingPassSession session = new();

        NormalizationPrimitivePass pass = new(NullLogger.Instance);
        await pass.RunAsync(ctx, session, CancellationToken.None);

        Assert.Empty(session.Batch.PhysLines);
        Assert.Empty(session.Batch.Significances);
        Assert.Empty(session.Batch.ModelSources);
    }

    [Fact]
    public async Task Run_TensorWithoutClassification_Skipped()
    {
        float[] values = [1.0f, 2.0f, 3.0f];
        using TinySafetensorsFile file = TinySafetensorsFile.CreateF32("unrecognized.norm.weight", [3], values);
        TensorHandle handle = MakeTensorHandle(file.Tensor, classification: null);
        ModelPassContext ctx = MakeContext(handle);
        RecordingPassSession session = new();

        NormalizationPrimitivePass pass = new(NullLogger.Instance);
        await pass.RunAsync(ctx, session, CancellationToken.None);

        Assert.Empty(session.Batch.PhysLines);
    }

    [Fact]
    public async Task Run_TwoDTensorEvenIfClassifiedAsNormalization_Skipped()
    {
        // Defensive: norm tensors are 1-D by definition. Skip 2-D even if mis-classified.
        float[] values = [1.0f, 2.0f, 3.0f, 4.0f];
        using TinySafetensorsFile file = TinySafetensorsFile.CreateF32("weird.norm.weight", [2, 2], values);
        TensorHandle handle = MakeTensorHandle(file.Tensor, PrimitiveKind.Normalization);
        ModelPassContext ctx = MakeContext(handle);
        RecordingPassSession session = new();

        NormalizationPrimitivePass pass = new(NullLogger.Instance);
        await pass.RunAsync(ctx, session, CancellationToken.None);

        Assert.Empty(session.Batch.PhysLines);
    }

    private static TensorHandle MakeTensorHandle(SafetensorsTensorInfo info, PrimitiveKind? primitive = null, TensorClassification? classification = null)
    {
        byte[] hash = new byte[32];
        for (int i = 0; i < 32; i++) { hash[i] = (byte)(i ^ 0x42); }
        TensorClassification cls = classification ?? new TensorClassification(
            primitive ?? PrimitiveKind.Unknown,
            ArchetypeTuple.Unknown, TupleSlot.Unknown,
            null, null, null, ModalityHint.Unknown, null);
        return new TensorHandle(info, cls, hash, new EntityHandle(hash, "tensor"));
    }

    private static ModelPassContext MakeContext(params TensorHandle[] tensors)
    {
        byte[] revision = new byte[32];
        byte[] archHash = new byte[32];
        for (int i = 0; i < 32; i++) { archHash[i] = (byte)(i ^ 0xAA); revision[i] = (byte)(i ^ 0x55); }
        EntityHandle archEntity = new(archHash, "model_architecture");
        ModelArchitecture arch = new(
            ModelId: "test/model",
            ArchitectureClass: "BertModel",
            ModelType: "test",
            HiddenSize: 384,
            NumLayers: 1,
            NumAttentionHeads: 1,
            VocabSize: 8,
            IntermediateSize: 4,
            MaxPositionEmbeddings: 8);
        ModelArchitectureHandle archHandle = new(arch, ArchitectureClassId: 1, ContentHash: archHash, Entity: archEntity);

        Dictionary<TensorHandle, TensorClassification> cls = new();
        foreach (var t in tensors)
        {
            if (t.Classification.Primitive != PrimitiveKind.Unknown
                || t.Classification.Tuple != ArchetypeTuple.Unknown)
            {
                cls[t] = t.Classification;
            }
        }

        return new ModelPassContext(
            Source: new ModelSourceHandle(
                ModelSourceId: 1,
                PublisherSlug: "test",
                ModelSlug: "model",
                Revision: revision,
                RevisionHex: "deadbeef",
                ModelId: "test/model",
                ModelDirectory: Path.GetTempPath()),
            Architecture: archHandle,
            Tensors: tensors,
            Compute: ComputeFacade.Instance,
            TensorRoleMap: new Dictionary<string, int>(),
            CheckpointKey: "test_checkpoint",
            ProvenanceCode: "test_provenance",
            TensorClassifications: cls,
            ResolvedTuples: System.Array.Empty<ResolvedTuple>());
    }
}
