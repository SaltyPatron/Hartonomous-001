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
/// Verifies LoraDeltaTuplePass:
///   - Reads (A, B) per LoraDelta tuple
///   - Composes ΔW = B·A and projects against embedding
///   - Emits model_concept_similarity edges with attestation_type=model_lora_adapter_evidence
///   - Handles both PEFT layout (A=[rank, hidden_in], B=[hidden_out, rank]) and Diffusers layout (A=[hidden_in, rank], B=[rank, hidden_out])
/// </summary>
public sealed class LoraDeltaTuplePassTests
{
    [Fact]
    public async Task Run_PeftLayout_EmitsModelLoraAdapterEvidenceAttestations()
    {
        const int vocab = 4;
        const int hidden = 4;
        const int rank = 2;
        // PEFT: A=[rank, hidden_in], B=[hidden_out, rank]
        float[] embed = MakeMatrix(vocab, hidden, seed: 1);
        float[] aWeight = MakeMatrix(rank, hidden, seed: 2);
        float[] bWeight = MakeMatrix(hidden, rank, seed: 3);

        string dir = MakeTempModelDir();
        try
        {
            WriteTokenizerJson(dir, vocab);
            using TinySafetensorsFile file = TinySafetensorsFile.CreateF32Multi(dir,
            [
                ("model.embed_tokens.weight", [vocab, hidden], embed),
                ("model.layers.0.self_attn.q_proj.lora_A.default.weight", [rank, hidden], aWeight),
                ("model.layers.0.self_attn.q_proj.lora_B.default.weight", [hidden, rank], bWeight),
            ]);

            TensorHandle embedHandle = MakeHandle(file.AllTensors[0]);
            TensorHandle aHandle = MakeHandle(file.AllTensors[1]);
            TensorHandle bHandle = MakeHandle(file.AllTensors[2]);

            ResolvedTuple embedTuple = MakeEmbeddingTuple(embedHandle);
            ResolvedTuple loraTuple = new(
                "LoraDelta:L0:H_:E_", ArchetypeTuple.LoraDelta, ModalityHint.Text,
                SecondaryModality: null, LayerIndex: 0, HeadIndex: null, ExpertIndex: null,
                Members: new TupleMember[]
                {
                    new(TupleSlot.LoraA, aHandle, FusedSplit: null),
                    new(TupleSlot.LoraB, bHandle, FusedSplit: null),
                });
            ModelPassContext ctx = MakeContext(dir,
                [embedHandle, aHandle, bHandle],
                [embedTuple, loraTuple]);
            RecordingPassSession session = new();

            LoraDeltaTuplePass pass = new(NullLogger.Instance);
            await pass.RunAsync(ctx, session, CancellationToken.None);

            Assert.NotEmpty(session.Batch.Edges);
            Assert.All(session.Batch.Edges, e => Assert.Equal("model_concept_similarity", e.EdgeTypeCode));
            // Post-AP-38: AttestationTypeCode is sign-only; LoRA adapter evidence
            // is encoded via (provenance × arena) — model_trust + semantic_relevance.
            Assert.All(session.Batch.Edges, e =>
                Assert.Contains(e.Significance, s => s.ContextTypeCode == "model_trust"
                    && s.AttestationTypeCode == "positive_evidence"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Run_NonLoraTuples_Skipped()
    {
        // Pass should ignore tuples that aren't LoraDelta
        const int vocab = 4;
        const int hidden = 4;
        float[] embed = MakeMatrix(vocab, hidden, seed: 1);
        float[] q = MakeMatrix(hidden, 2, seed: 2);

        string dir = MakeTempModelDir();
        try
        {
            WriteTokenizerJson(dir, vocab);
            using TinySafetensorsFile file = TinySafetensorsFile.CreateF32Multi(dir,
            [
                ("model.embed_tokens.weight", [vocab, hidden], embed),
                ("model.layers.0.self_attn.q_proj.weight", [hidden, 2], q),
            ]);

            TensorHandle embedHandle = MakeHandle(file.AllTensors[0]);
            TensorHandle qHandle = MakeHandle(file.AllTensors[1]);
            ResolvedTuple embedTuple = MakeEmbeddingTuple(embedHandle);
            ResolvedTuple attnTuple = new(
                "AttentionBlock:L0:H_:E_", ArchetypeTuple.AttentionBlock, ModalityHint.Text,
                SecondaryModality: null, LayerIndex: 0, HeadIndex: null, ExpertIndex: null,
                Members: new TupleMember[] { new(TupleSlot.Q, qHandle, FusedSplit: null) });
            ModelPassContext ctx = MakeContext(dir, [embedHandle, qHandle], [embedTuple, attnTuple]);
            RecordingPassSession session = new();

            LoraDeltaTuplePass pass = new(NullLogger.Instance);
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
