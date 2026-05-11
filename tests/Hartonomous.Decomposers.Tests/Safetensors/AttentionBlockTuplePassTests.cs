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

namespace Hartonomous.Decomposers.Tests.Safetensors;

/// <summary>
/// Verifies AttentionBlockTuplePass:
///   - Reads embedding + Q + K (and optionally V + O) from a real safetensors file
///   - Builds vocab → word_form-hash bridge from a synthetic tokenizer.json
///   - Emits model_attention_pattern edges with attestation_type=model_attention_qk_pattern
///   - Emits model_attention_pattern edges with attestation_type=model_attention_vo_pattern when V+O present
///   - Skips tuples whose modality is not text
///   - Skips when no embedding tuple exists in context
/// </summary>
public sealed class AttentionBlockTuplePassTests
{
    [Fact]
    public async Task Run_QkOnly_EmitsModelAttentionPatternWithQkAttestation()
    {
        // Tiny synthetic model: vocab=4, hidden=4. Q and K are 4×2 (cols=2 = head_dim).
        const int vocab = 4;
        const int hidden = 4;
        float[] embed = MakeMatrix(vocab, hidden, seed: 1);
        float[] q = MakeMatrix(hidden, 2, seed: 2);
        float[] k = MakeMatrix(hidden, 2, seed: 3);

        string dir = MakeTempModelDir();
        try
        {
            WriteTokenizerJson(dir, vocab);
            using TinySafetensorsFile file = TinySafetensorsFile.CreateF32Multi(dir,
            [
                ("model.embed_tokens.weight", [vocab, hidden], embed),
                ("model.layers.0.self_attn.q_proj.weight", [hidden, 2], q),
                ("model.layers.0.self_attn.k_proj.weight", [hidden, 2], k),
            ]);

            (TensorHandle embedHandle, TensorHandle qHandle, TensorHandle kHandle) = MakeHandles(file.AllTensors);
            ResolvedTuple embedTuple = MakeEmbeddingTuple(embedHandle);
            ResolvedTuple attnTuple = MakeAttentionTupleQK(qHandle, kHandle, layer: 0);
            ModelPassContext ctx = MakeContext(dir, [embedHandle, qHandle, kHandle], [embedTuple, attnTuple]);
            RecordingPassSession session = new();

            AttentionBlockTuplePass pass = new(NullLogger.Instance);
            await pass.RunAsync(ctx, session, CancellationToken.None);

            // Some edges emitted (vocab=4 means at most 4×4 minus self = 12 pairs;
            // top-K-per-side filter further reduces; expect at least 1 edge).
            Assert.NotEmpty(session.Batch.Edges);
            Assert.All(session.Batch.Edges, e => Assert.Equal("model_attention_pattern", e.EdgeTypeCode));
            Assert.All(session.Batch.Edges, e =>
                Assert.Contains(e.RatingEvents, s => s.AttestationTypeCode == "model_attention_qk_pattern"));
            // No VO attestations (V/O not provided)
            Assert.DoesNotContain(session.Batch.Edges, e =>
                e.RatingEvents.Any(s => s.AttestationTypeCode == "model_attention_vo_pattern"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Run_QkvO_EmitsBothQkAndVoAttestations()
    {
        const int vocab = 4;
        const int hidden = 4;
        float[] embed = MakeMatrix(vocab, hidden, seed: 1);
        float[] q = MakeMatrix(hidden, 2, seed: 2);
        float[] k = MakeMatrix(hidden, 2, seed: 3);
        float[] v = MakeMatrix(hidden, 2, seed: 4);
        float[] o = MakeMatrix(2, hidden, seed: 5);  // O has rows=head_dim, cols=hidden

        string dir = MakeTempModelDir();
        try
        {
            WriteTokenizerJson(dir, vocab);
            using TinySafetensorsFile file = TinySafetensorsFile.CreateF32Multi(dir,
            [
                ("model.embed_tokens.weight", [vocab, hidden], embed),
                ("model.layers.0.self_attn.q_proj.weight", [hidden, 2], q),
                ("model.layers.0.self_attn.k_proj.weight", [hidden, 2], k),
                ("model.layers.0.self_attn.v_proj.weight", [hidden, 2], v),
                ("model.layers.0.self_attn.o_proj.weight", [2, hidden], o),
            ]);

            TensorHandle embedHandle = MakeHandle(file.AllTensors[0]);
            TensorHandle qHandle = MakeHandle(file.AllTensors[1]);
            TensorHandle kHandle = MakeHandle(file.AllTensors[2]);
            TensorHandle vHandle = MakeHandle(file.AllTensors[3]);
            TensorHandle oHandle = MakeHandle(file.AllTensors[4]);

            ResolvedTuple embedTuple = MakeEmbeddingTuple(embedHandle);
            ResolvedTuple attnTuple = new(
                "AttentionBlock:L0:H_:E_", ArchetypeTuple.AttentionBlock, ModalityHint.Text,
                SecondaryModality: null, LayerIndex: 0, HeadIndex: null, ExpertIndex: null,
                Members: new TupleMember[]
                {
                    new(TupleSlot.Q, qHandle, FusedSplit: null),
                    new(TupleSlot.K, kHandle, FusedSplit: null),
                    new(TupleSlot.V, vHandle, FusedSplit: null),
                    new(TupleSlot.O, oHandle, FusedSplit: null),
                });
            ModelPassContext ctx = MakeContext(dir,
                [embedHandle, qHandle, kHandle, vHandle, oHandle],
                [embedTuple, attnTuple]);
            RecordingPassSession session = new();

            AttentionBlockTuplePass pass = new(NullLogger.Instance);
            await pass.RunAsync(ctx, session, CancellationToken.None);

            Assert.Contains(session.Batch.Edges, e =>
                e.RatingEvents.Any(s => s.AttestationTypeCode == "model_attention_qk_pattern"));
            Assert.Contains(session.Batch.Edges, e =>
                e.RatingEvents.Any(s => s.AttestationTypeCode == "model_attention_vo_pattern"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Run_NoEmbeddingTuple_SkipsAllAttentionTuples()
    {
        const int vocab = 4;
        const int hidden = 4;
        float[] q = MakeMatrix(hidden, 2, seed: 1);
        float[] k = MakeMatrix(hidden, 2, seed: 2);

        string dir = MakeTempModelDir();
        try
        {
            WriteTokenizerJson(dir, vocab);
            using TinySafetensorsFile file = TinySafetensorsFile.CreateF32Multi(dir,
            [
                ("model.layers.0.self_attn.q_proj.weight", [hidden, 2], q),
                ("model.layers.0.self_attn.k_proj.weight", [hidden, 2], k),
            ]);
            TensorHandle qHandle = MakeHandle(file.AllTensors[0]);
            TensorHandle kHandle = MakeHandle(file.AllTensors[1]);
            ResolvedTuple attnTuple = MakeAttentionTupleQK(qHandle, kHandle, layer: 0);
            // No embedding tuple in context → pass must skip
            ModelPassContext ctx = MakeContext(dir, [qHandle, kHandle], [attnTuple]);
            RecordingPassSession session = new();

            AttentionBlockTuplePass pass = new(NullLogger.Instance);
            await pass.RunAsync(ctx, session, CancellationToken.None);

            Assert.Empty(session.Batch.Edges);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Run_NoTokenizerJson_SkipsCleanly()
    {
        const int vocab = 4;
        const int hidden = 4;
        float[] embed = MakeMatrix(vocab, hidden, seed: 1);
        float[] q = MakeMatrix(hidden, 2, seed: 2);
        float[] k = MakeMatrix(hidden, 2, seed: 3);

        string dir = MakeTempModelDir();
        try
        {
            // NOT writing tokenizer.json
            using TinySafetensorsFile file = TinySafetensorsFile.CreateF32Multi(dir,
            [
                ("model.embed_tokens.weight", [vocab, hidden], embed),
                ("model.layers.0.self_attn.q_proj.weight", [hidden, 2], q),
                ("model.layers.0.self_attn.k_proj.weight", [hidden, 2], k),
            ]);
            (TensorHandle embedHandle, TensorHandle qHandle, TensorHandle kHandle) = MakeHandles(file.AllTensors);
            ResolvedTuple embedTuple = MakeEmbeddingTuple(embedHandle);
            ResolvedTuple attnTuple = MakeAttentionTupleQK(qHandle, kHandle, layer: 0);
            ModelPassContext ctx = MakeContext(dir, [embedHandle, qHandle, kHandle], [embedTuple, attnTuple]);
            RecordingPassSession session = new();

            AttentionBlockTuplePass pass = new(NullLogger.Instance);
            await pass.RunAsync(ctx, session, CancellationToken.None);

            // No tokenizer → no vocab → can't emit edges
            Assert.Empty(session.Batch.Edges);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static (TensorHandle, TensorHandle, TensorHandle) MakeHandles(IReadOnlyList<SafetensorsTensorInfo> tensors)
    {
        return (MakeHandle(tensors[0]), MakeHandle(tensors[1]), MakeHandle(tensors[2]));
    }

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

    private static ResolvedTuple MakeAttentionTupleQK(TensorHandle q, TensorHandle k, int layer)
    {
        return new ResolvedTuple(
            $"AttentionBlock:L{layer}:H_:E_", ArchetypeTuple.AttentionBlock, ModalityHint.Text,
            SecondaryModality: null, LayerIndex: layer, HeadIndex: null, ExpertIndex: null,
            Members: new TupleMember[]
            {
                new(TupleSlot.Q, q, FusedSplit: null),
                new(TupleSlot.K, k, FusedSplit: null),
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
        // Minimal tokenizer.json with BPE model and a few vocab entries.
        // The HuggingFaceTokenizerParser handles BPE/WordPiece/SentencePiece;
        // for this test we use a tiny BPE config with dummy tokens t0..tN.
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
        // Deterministic synthetic data with non-trivial variation across rows
        // so the projection-against-embedding produces non-uniform norms.
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
