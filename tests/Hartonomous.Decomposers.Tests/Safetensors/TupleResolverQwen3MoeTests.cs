using System.Linq;
using Hartonomous.Decomposers.Safetensors;
using Hartonomous.Decomposers.Safetensors.Passes;
using Hartonomous.Decomposers.Safetensors.TupleResolution;
using Xunit;

namespace Hartonomous.Decomposers.Tests.Safetensors;

/// <summary>
/// Verifies the Qwen3MoeArchitectureProfile composes with the
/// LlamaArchitectureProfile: MoE-specific tensor names (router, experts)
/// resolve via the MoE profile; everything else (attention, embedding,
/// norms) falls through to the inherited Llama monolith resolution.
/// Names taken from actual Qwen3-Coder-30B-A3B-Instruct.
/// </summary>
public sealed class TupleResolverQwen3MoeTests
{
    private const string ArchClass = "Qwen3MoeForCausalLM";

    [Fact]
    public void RouterAndExperts_ClassifyAsMoeRouterBlockSlots()
    {
        TensorHandle router = TupleResolverTestHelpers.Tensor("model.layers.10.mlp.gate.weight", [128, 2048]);
        TensorHandle expertGate0 = TupleResolverTestHelpers.Tensor("model.layers.10.mlp.experts.0.gate_proj.weight", [768, 2048]);
        TensorHandle expertUp0 = TupleResolverTestHelpers.Tensor("model.layers.10.mlp.experts.0.up_proj.weight", [768, 2048]);
        TensorHandle expertDown0 = TupleResolverTestHelpers.Tensor("model.layers.10.mlp.experts.0.down_proj.weight", [2048, 768]);
        TensorHandle expertGate1 = TupleResolverTestHelpers.Tensor("model.layers.10.mlp.experts.1.gate_proj.weight", [768, 2048]);

        TupleResolver resolver = new();
        (var classifications, var tuples) = resolver.Resolve(ArchClass,
            [router, expertGate0, expertUp0, expertDown0, expertGate1]);

        Assert.Equal(ArchetypeTuple.MoeRouterBlock, classifications[router].Tuple);
        Assert.Equal(TupleSlot.Router, classifications[router].Slot);
        Assert.Equal(10, classifications[router].LayerIndex);

        Assert.Equal(TupleSlot.ExpertGate, classifications[expertGate0].Slot);
        Assert.Equal(0, classifications[expertGate0].ExpertIndex);
        Assert.Equal(10, classifications[expertGate0].LayerIndex);

        Assert.Equal(TupleSlot.ExpertGate, classifications[expertGate1].Slot);
        Assert.Equal(1, classifications[expertGate1].ExpertIndex);

        // Three distinct MoeRouterBlock tuples bucket from this input:
        //   (MoeRouterBlock, L=10, E=null) — the router
        //   (MoeRouterBlock, L=10, E=0)    — expert 0's three projections
        //   (MoeRouterBlock, L=10, E=1)    — expert 1's gate (only emitted in this test)
        Assert.Equal(3, tuples.Count(t => t.Tuple == ArchetypeTuple.MoeRouterBlock && t.LayerIndex == 10));
        Assert.Single(tuples, t => t.Tuple == ArchetypeTuple.MoeRouterBlock && t.LayerIndex == 10 && t.ExpertIndex == null);
        Assert.Single(tuples, t => t.Tuple == ArchetypeTuple.MoeRouterBlock && t.LayerIndex == 10 && t.ExpertIndex == 0);
    }

    [Fact]
    public void Qwen3Moe_PerExpertGateUpDown_BucketIntoOneTuplePerExpert()
    {
        TensorHandle expertGate0 = TupleResolverTestHelpers.Tensor("model.layers.10.mlp.experts.0.gate_proj.weight", [768, 2048]);
        TensorHandle expertUp0 = TupleResolverTestHelpers.Tensor("model.layers.10.mlp.experts.0.up_proj.weight", [768, 2048]);
        TensorHandle expertDown0 = TupleResolverTestHelpers.Tensor("model.layers.10.mlp.experts.0.down_proj.weight", [2048, 768]);

        TupleResolver resolver = new();
        (_, var tuples) = resolver.Resolve(ArchClass, [expertGate0, expertUp0, expertDown0]);

        ResolvedTuple expert0Tuple = tuples.Single(t =>
            t.Tuple == ArchetypeTuple.MoeRouterBlock && t.LayerIndex == 10 && t.ExpertIndex == 0);
        Assert.Equal(3, expert0Tuple.Members.Count);
    }

    [Fact]
    public void Qwen3Moe_AttentionTensors_FallThroughToLlamaProfile()
    {
        // Q/K/V/O still resolve via inherited Llama rules even under Qwen3-MoE arch
        TensorHandle q = TupleResolverTestHelpers.Tensor("model.layers.10.self_attn.q_proj.weight", [4096, 2048]);
        TensorHandle k = TupleResolverTestHelpers.Tensor("model.layers.10.self_attn.k_proj.weight", [512, 2048]);
        TupleResolver resolver = new();
        (var classifications, _) = resolver.Resolve(ArchClass, [q, k]);

        Assert.Equal(ArchetypeTuple.AttentionBlock, classifications[q].Tuple);
        Assert.Equal(TupleSlot.Q, classifications[q].Slot);
        Assert.Equal(TupleSlot.K, classifications[k].Slot);
    }

    [Fact]
    public void Qwen3Moe_EmbeddingFallsThrough()
    {
        TensorHandle e = TupleResolverTestHelpers.Tensor("model.embed_tokens.weight", [151936, 2048]);
        TupleResolver resolver = new();
        (var classifications, _) = resolver.Resolve(ArchClass, [e]);
        Assert.Equal(ArchetypeTuple.EmbeddingLookup, classifications[e].Tuple);
        Assert.Equal(TupleSlot.Table, classifications[e].Slot);
    }
}
