using System.Linq;
using Hartonomous.Decomposers.Safetensors;
using Hartonomous.Decomposers.Safetensors.Passes;
using Hartonomous.Decomposers.Safetensors.TupleResolution;
using Xunit;

namespace Hartonomous.Decomposers.Tests.Safetensors;

/// <summary>
/// Verifies the LlamaArchitectureProfile resolves real Llama-family tensor
/// names (Qwen2.5-Coder, Qwen3, DeepSeek-Coder, Mistral, Phi, Gemma share
/// this naming) to the correct (PrimitiveKind, ArchetypeTuple, TupleSlot,
/// LayerIdx) classifications.
/// </summary>
public sealed class TupleResolverLlamaTests
{
    private const string ArchClass = "LlamaForCausalLM";

    [Fact]
    public void Embedding_ClassifiesAsLookupTable()
    {
        TensorHandle e = TupleResolverTestHelpers.Tensor("model.embed_tokens.weight", [151936, 2048]);
        TupleResolver resolver = new();
        (var classifications, _) = resolver.Resolve(ArchClass, [e]);
        Assert.Equal(PrimitiveKind.Lookup, classifications[e].Primitive);
        Assert.Equal(ArchetypeTuple.EmbeddingLookup, classifications[e].Tuple);
        Assert.Equal(TupleSlot.Table, classifications[e].Slot);
    }

    [Fact]
    public void AttentionQkvO_ClassifiesAsAttentionBlockSlots()
    {
        TensorHandle q = TupleResolverTestHelpers.Tensor("model.layers.5.self_attn.q_proj.weight", [4096, 2048]);
        TensorHandle k = TupleResolverTestHelpers.Tensor("model.layers.5.self_attn.k_proj.weight", [512, 2048]);
        TensorHandle v = TupleResolverTestHelpers.Tensor("model.layers.5.self_attn.v_proj.weight", [512, 2048]);
        TensorHandle o = TupleResolverTestHelpers.Tensor("model.layers.5.self_attn.o_proj.weight", [2048, 4096]);
        TupleResolver resolver = new();
        (var classifications, var tuples) = resolver.Resolve(ArchClass, [q, k, v, o]);

        Assert.Equal(TupleSlot.Q, classifications[q].Slot);
        Assert.Equal(TupleSlot.K, classifications[k].Slot);
        Assert.Equal(TupleSlot.V, classifications[v].Slot);
        Assert.Equal(TupleSlot.O, classifications[o].Slot);
        Assert.All(new[] { q, k, v, o }, t => Assert.Equal(5, classifications[t].LayerIndex));

        ResolvedTuple attentionTuple = tuples.Single(t => t.Tuple == ArchetypeTuple.AttentionBlock && t.LayerIndex == 5);
        Assert.Equal(4, attentionTuple.Members.Count);
    }

    [Fact]
    public void Qwen3QkNorm_ClassifyAsNormalizationInsideAttentionBlock()
    {
        TensorHandle qNorm = TupleResolverTestHelpers.Tensor("model.layers.0.self_attn.q_norm.weight", [128]);
        TensorHandle kNorm = TupleResolverTestHelpers.Tensor("model.layers.0.self_attn.k_norm.weight", [128]);
        TupleResolver resolver = new();
        (var classifications, _) = resolver.Resolve(ArchClass, [qNorm, kNorm]);

        Assert.Equal(PrimitiveKind.Normalization, classifications[qNorm].Primitive);
        Assert.Equal(ArchetypeTuple.AttentionBlock, classifications[qNorm].Tuple);
        Assert.Equal(TupleSlot.QNorm, classifications[qNorm].Slot);
        Assert.Equal(TupleSlot.KNorm, classifications[kNorm].Slot);
    }

    [Fact]
    public void SwiGluFfn_GateUpDown_ClassifyAsSwiGluFfnSlots()
    {
        TensorHandle gate = TupleResolverTestHelpers.Tensor("model.layers.7.mlp.gate_proj.weight", [11008, 2048]);
        TensorHandle up = TupleResolverTestHelpers.Tensor("model.layers.7.mlp.up_proj.weight", [11008, 2048]);
        TensorHandle down = TupleResolverTestHelpers.Tensor("model.layers.7.mlp.down_proj.weight", [2048, 11008]);
        TupleResolver resolver = new();
        (var classifications, var tuples) = resolver.Resolve(ArchClass, [gate, up, down]);

        Assert.Equal(ArchetypeTuple.SwiGluFfn, classifications[gate].Tuple);
        Assert.Equal(TupleSlot.Gate, classifications[gate].Slot);
        Assert.Equal(TupleSlot.Up, classifications[up].Slot);
        Assert.Equal(TupleSlot.Down, classifications[down].Slot);

        ResolvedTuple ffnTuple = tuples.Single(t => t.Tuple == ArchetypeTuple.SwiGluFfn && t.LayerIndex == 7);
        Assert.Equal(3, ffnTuple.Members.Count);
    }

    [Fact]
    public void Norms_InputAndPostAttn_ClassifyToCorrectTuples()
    {
        TensorHandle inputLn = TupleResolverTestHelpers.Tensor("model.layers.0.input_layernorm.weight", [2048]);
        TensorHandle postAttnLn = TupleResolverTestHelpers.Tensor("model.layers.0.post_attention_layernorm.weight", [2048]);
        TensorHandle finalNorm = TupleResolverTestHelpers.Tensor("model.norm.weight", [2048]);
        TupleResolver resolver = new();
        (var classifications, _) = resolver.Resolve(ArchClass, [inputLn, postAttnLn, finalNorm]);

        // input_layernorm precedes attention block
        Assert.Equal(ArchetypeTuple.AttentionBlock, classifications[inputLn].Tuple);
        Assert.Equal(TupleSlot.Scale, classifications[inputLn].Slot);

        // post_attention_layernorm precedes FFN
        Assert.Equal(ArchetypeTuple.SwiGluFfn, classifications[postAttnLn].Tuple);
        Assert.Equal(TupleSlot.Scale, classifications[postAttnLn].Slot);

        Assert.Equal(PrimitiveKind.Normalization, classifications[finalNorm].Primitive);
    }

    [Fact]
    public void LmHead_ClassifiesAsLinearLmHeadSlot()
    {
        TensorHandle lmHead = TupleResolverTestHelpers.Tensor("lm_head.weight", [151936, 2048]);
        TupleResolver resolver = new();
        (var classifications, _) = resolver.Resolve(ArchClass, [lmHead]);

        Assert.Equal(PrimitiveKind.Linear, classifications[lmHead].Primitive);
        Assert.Equal(TupleSlot.LmHead, classifications[lmHead].Slot);
    }

    [Fact]
    public void TensorRoleCode_MapsResolvedSlotsToSeededRoles()
    {
        Assert.Equal("attention_query", ModelPassOrchestrator.TensorRoleCode(new TensorClassification(
            PrimitiveKind.Linear, ArchetypeTuple.AttentionBlock, TupleSlot.Q, 0, null, null, ModalityHint.Text, null)));
        Assert.Equal("ffn_gate", ModelPassOrchestrator.TensorRoleCode(new TensorClassification(
            PrimitiveKind.Linear, ArchetypeTuple.SwiGluFfn, TupleSlot.Gate, 0, null, null, ModalityHint.Text, null)));
        Assert.Equal("moe_expert_down", ModelPassOrchestrator.TensorRoleCode(new TensorClassification(
            PrimitiveKind.Linear, ArchetypeTuple.MoeRouterBlock, TupleSlot.ExpertDown, 0, null, 2, ModalityHint.Text, null)));
        Assert.Equal("vq_codebook", ModelPassOrchestrator.TensorRoleCode(new TensorClassification(
            PrimitiveKind.Lookup, ArchetypeTuple.EmbeddingLookup, TupleSlot.Table, null, null, null, ModalityHint.CodecCodeword, null)));
        Assert.Equal("logit_head", ModelPassOrchestrator.TensorRoleCode(new TensorClassification(
            PrimitiveKind.Linear, ArchetypeTuple.EmbeddingLookup, TupleSlot.LmHead, null, null, null, ModalityHint.Text, null)));
    }
}
