using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Ingestion;
using Hartonomous.Decomposers.Safetensors;
using Hartonomous.Decomposers.Safetensors.Passes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Verifies FfnTuplePass:
///   - SwiGluFfn tuple (gate/up/down) — emits model_ffn_factor edges with attestation_type=model_ffn_full_path.
///   - BertFfn tuple (intermediate/output) — emits same edge_type with same attestation_type.
///   - MoE expert tuple — emits with attestation_type=model_moe_expert_response.
///   - Skips when no embedding tuple.
///   - Skips when no FFN tuple.
/// </summary>
namespace Hartonomous.Decomposers.Tests.Safetensors;

public sealed class FfnTuplePassTests
{
    [Fact]
    public async Task Run_SwiGluFfn_EmitsModelFfnFullPathAttestation()
    {
        const int vocab = 4;
        const int hidden = 4;
        const int intermediate = 4;
        float[] embed = MakeMatrix(vocab, hidden, seed: 1);
        float[] gate = MakeMatrix(intermediate, hidden, seed: 2);
        float[] up = MakeMatrix(intermediate, hidden, seed: 3);
        float[] down = MakeMatrix(hidden, intermediate, seed: 4);

        string dir = MakeTempModelDir();
        try
        {
            WriteTokenizerJson(dir, vocab);
            using TinySafetensorsFile file = TinySafetensorsFile.CreateF32Multi(dir,
            [
                ("model.embed_tokens.weight", [vocab, hidden], embed),
                ("model.layers.0.mlp.gate_proj.weight", [intermediate, hidden], gate),
                ("model.layers.0.mlp.up_proj.weight", [intermediate, hidden], up),
                ("model.layers.0.mlp.down_proj.weight", [hidden, intermediate], down),
            ]);

            TensorHandle embedHandle = MakeHandle(file.AllTensors[0]);
            TensorHandle gateHandle = MakeHandle(file.AllTensors[1]);
            TensorHandle upHandle = MakeHandle(file.AllTensors[2]);
            TensorHandle downHandle = MakeHandle(file.AllTensors[3]);

            ResolvedTuple embedTuple = MakeEmbeddingTuple(embedHandle);
            ResolvedTuple ffnTuple = new(
                "SwiGluFfn:L0:H_:E_", ArchetypeTuple.SwiGluFfn, ModalityHint.Text,
                SecondaryModality: null, LayerIndex: 0, HeadIndex: null, ExpertIndex: null,
                Members: new TupleMember[]
                {
                    new(TupleSlot.Gate, gateHandle, FusedSplit: null),
                    new(TupleSlot.Up, upHandle, FusedSplit: null),
                    new(TupleSlot.Down, downHandle, FusedSplit: null),
                });
            ModelPassContext ctx = MakeContext(dir,
                [embedHandle, gateHandle, upHandle, downHandle],
                [embedTuple, ffnTuple]);
            RecordingPassSession session = new();

            FfnTuplePass pass = new(NullLogger.Instance);
            await pass.RunAsync(ctx, session, CancellationToken.None);

            Assert.NotEmpty(session.Batch.Edges);
            Assert.All(session.Batch.Edges, e => Assert.Equal("model_ffn_factor", e.EdgeTypeCode));
            Assert.All(session.Batch.Edges, e =>
                Assert.Contains(e.Significance, s => s.AttestationTypeCode == "model_ffn_full_path"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Run_BertFfn_EmitsModelFfnFullPathAttestation()
    {
        const int vocab = 4;
        const int hidden = 4;
        const int intermediate = 4;
        float[] embed = MakeMatrix(vocab, hidden, seed: 1);
        float[] inter = MakeMatrix(intermediate, hidden, seed: 2);
        float[] outp = MakeMatrix(hidden, intermediate, seed: 3);

        string dir = MakeTempModelDir();
        try
        {
            WriteTokenizerJson(dir, vocab);
            using TinySafetensorsFile file = TinySafetensorsFile.CreateF32Multi(dir,
            [
                ("model.embed_tokens.weight", [vocab, hidden], embed),
                ("encoder.layer.0.intermediate.dense.weight", [intermediate, hidden], inter),
                ("encoder.layer.0.output.dense.weight", [hidden, intermediate], outp),
            ]);

            TensorHandle embedHandle = MakeHandle(file.AllTensors[0]);
            TensorHandle interHandle = MakeHandle(file.AllTensors[1]);
            TensorHandle outpHandle = MakeHandle(file.AllTensors[2]);

            ResolvedTuple embedTuple = MakeEmbeddingTuple(embedHandle);
            ResolvedTuple ffnTuple = new(
                "BertFfn:L0:H_:E_", ArchetypeTuple.BertFfn, ModalityHint.Text,
                SecondaryModality: null, LayerIndex: 0, HeadIndex: null, ExpertIndex: null,
                Members: new TupleMember[]
                {
                    new(TupleSlot.Intermediate, interHandle, FusedSplit: null),
                    new(TupleSlot.Output, outpHandle, FusedSplit: null),
                });
            ModelPassContext ctx = MakeContext(dir,
                [embedHandle, interHandle, outpHandle],
                [embedTuple, ffnTuple]);
            RecordingPassSession session = new();

            FfnTuplePass pass = new(NullLogger.Instance);
            await pass.RunAsync(ctx, session, CancellationToken.None);

            Assert.NotEmpty(session.Batch.Edges);
            Assert.All(session.Batch.Edges, e =>
                Assert.Contains(e.Significance, s => s.AttestationTypeCode == "model_ffn_full_path"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Run_MoeExpert_EmitsModelMoeExpertResponseAttestation()
    {
        const int vocab = 4;
        const int hidden = 4;
        const int intermediate = 4;
        float[] embed = MakeMatrix(vocab, hidden, seed: 1);
        float[] expertGate = MakeMatrix(intermediate, hidden, seed: 5);
        float[] expertUp = MakeMatrix(intermediate, hidden, seed: 6);
        float[] expertDown = MakeMatrix(hidden, intermediate, seed: 7);

        string dir = MakeTempModelDir();
        try
        {
            WriteTokenizerJson(dir, vocab);
            using TinySafetensorsFile file = TinySafetensorsFile.CreateF32Multi(dir,
            [
                ("model.embed_tokens.weight", [vocab, hidden], embed),
                ("model.layers.0.mlp.experts.0.gate_proj.weight", [intermediate, hidden], expertGate),
                ("model.layers.0.mlp.experts.0.up_proj.weight", [intermediate, hidden], expertUp),
                ("model.layers.0.mlp.experts.0.down_proj.weight", [hidden, intermediate], expertDown),
            ]);

            TensorHandle embedHandle = MakeHandle(file.AllTensors[0]);
            TensorHandle gateHandle = MakeHandle(file.AllTensors[1]);
            TensorHandle upHandle = MakeHandle(file.AllTensors[2]);
            TensorHandle downHandle = MakeHandle(file.AllTensors[3]);

            ResolvedTuple embedTuple = MakeEmbeddingTuple(embedHandle);
            ResolvedTuple moeTuple = new(
                "MoeRouterBlock:L0:H_:E0", ArchetypeTuple.MoeRouterBlock, ModalityHint.Text,
                SecondaryModality: null, LayerIndex: 0, HeadIndex: null, ExpertIndex: 0,
                Members: new TupleMember[]
                {
                    new(TupleSlot.ExpertGate, gateHandle, FusedSplit: null),
                    new(TupleSlot.ExpertUp, upHandle, FusedSplit: null),
                    new(TupleSlot.ExpertDown, downHandle, FusedSplit: null),
                });
            ModelPassContext ctx = MakeContext(dir,
                [embedHandle, gateHandle, upHandle, downHandle],
                [embedTuple, moeTuple]);
            RecordingPassSession session = new();

            FfnTuplePass pass = new(NullLogger.Instance);
            await pass.RunAsync(ctx, session, CancellationToken.None);

            Assert.NotEmpty(session.Batch.Edges);
            Assert.All(session.Batch.Edges, e =>
                Assert.Contains(e.Significance, s => s.AttestationTypeCode == "model_moe_expert_response"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Run_NoEmbeddingTuple_SkipsAllFfnTuples()
    {
        const int vocab = 4;
        const int hidden = 4;
        float[] gate = MakeMatrix(hidden, hidden, seed: 1);
        float[] down = MakeMatrix(hidden, hidden, seed: 2);

        string dir = MakeTempModelDir();
        try
        {
            WriteTokenizerJson(dir, vocab);
            using TinySafetensorsFile file = TinySafetensorsFile.CreateF32Multi(dir,
            [
                ("model.layers.0.mlp.gate_proj.weight", [hidden, hidden], gate),
                ("model.layers.0.mlp.down_proj.weight", [hidden, hidden], down),
            ]);
            TensorHandle gateHandle = MakeHandle(file.AllTensors[0]);
            TensorHandle downHandle = MakeHandle(file.AllTensors[1]);
            ResolvedTuple ffnTuple = new(
                "SwiGluFfn:L0:H_:E_", ArchetypeTuple.SwiGluFfn, ModalityHint.Text,
                SecondaryModality: null, LayerIndex: 0, HeadIndex: null, ExpertIndex: null,
                Members: new TupleMember[]
                {
                    new(TupleSlot.Gate, gateHandle, FusedSplit: null),
                    new(TupleSlot.Down, downHandle, FusedSplit: null),
                });
            ModelPassContext ctx = MakeContext(dir, [gateHandle, downHandle], [ffnTuple]);
            RecordingPassSession session = new();

            FfnTuplePass pass = new(NullLogger.Instance);
            await pass.RunAsync(ctx, session, CancellationToken.None);

            Assert.Empty(session.Batch.Edges);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static TensorHandle MakeHandle(SafetensorsTensorInfo info)
    {
        byte[] hash = new byte[32];
        int seed = info.Name.GetHashCode(System.StringComparison.Ordinal);
        for (int i = 0; i < 32; i++) { hash[i] = (byte)((seed >> (i % 4 * 8)) ^ i); }
        return new TensorHandle(info,
            new TensorClassification(PrimitiveKind.Unknown, ArchetypeTuple.Unknown, TupleSlot.Unknown,
                null, null, null, ModalityHint.Unknown, null),
            hash, new EntityHandle(hash, "tensor"));
    }

    private static ResolvedTuple MakeEmbeddingTuple(TensorHandle embedHandle)
    {
        return new ResolvedTuple(
            "EmbeddingLookup:L_:H_:E_", ArchetypeTuple.EmbeddingLookup, ModalityHint.Text,
            SecondaryModality: null, LayerIndex: null, HeadIndex: null, ExpertIndex: null,
            Members: new TupleMember[]
            {
                new(TupleSlot.Table, embedHandle, FusedSplit: null),
            });
    }

    private static ModelPassContext MakeContext(string modelDir, TensorHandle[] tensors, ResolvedTuple[] tuples)
    {
        byte[] revision = new byte[32];
        byte[] archHash = new byte[32];
        for (int i = 0; i < 32; i++) { archHash[i] = (byte)(i ^ 0xAA); revision[i] = (byte)(i ^ 0x55); }
        EntityHandle archEntity = new(archHash, "model_architecture");
        ModelArchitecture arch = new(
            ModelId: "test/model", ArchitectureClass: "LlamaForCausalLM", ModelType: "test",
            HiddenSize: 4, NumLayers: 1, NumAttentionHeads: 1, VocabSize: 4,
            IntermediateSize: 4, MaxPositionEmbeddings: 4);
        ModelArchitectureHandle archHandle = new(arch, ArchitectureClassId: 1, ContentHash: archHash, Entity: archEntity);
        return new ModelPassContext(
            Source: new ModelSourceHandle(1, "test", "model", revision, "deadbeef", "test/model", modelDir),
            Architecture: archHandle, Tensors: tensors, Compute: ComputeFacade.Instance,
            TensorRoleMap: new Dictionary<string, int>(),
            CheckpointKey: "test_checkpoint", ProvenanceCode: "test_provenance",
            TensorClassifications: new Dictionary<TensorHandle, TensorClassification>(),
            ResolvedTuples: tuples);
    }

    private static string MakeTempModelDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"hartonomous-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteTokenizerJson(string dir, int vocabSize)
    {
        StringBuilder sb = new();
        sb.Append("{\"model\":{\"type\":\"BPE\",\"vocab\":{");
        for (int i = 0; i < vocabSize; i++)
        {
            if (i > 0) { sb.Append(','); }
            sb.Append('"').Append("token").Append(i).Append("\":").Append(i);
        }
        sb.Append("},\"merges\":[]}}");
        File.WriteAllText(Path.Combine(dir, "tokenizer.json"), sb.ToString());
    }

    private static float[] MakeMatrix(int rows, int cols, int seed)
    {
        float[] m = new float[rows * cols];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                m[r * cols + c] = (float)System.Math.Sin((seed + 1) * (r + 1) * (c + 1) * 0.137);
            }
        }
        return m;
    }
}
