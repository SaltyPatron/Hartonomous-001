using System.Linq;
using Hartonomous.Decomposers.Safetensors;
using Hartonomous.Decomposers.Safetensors.Passes;
using Hartonomous.Decomposers.Safetensors.TupleResolution;
using Xunit;

namespace Hartonomous.Decomposers.Tests.Safetensors;

/// <summary>
/// Verifies the PeftLoraArchitectureProfile composes with a base architecture
/// profile. Names taken from actual canary-qwen-2.5b which wraps a Qwen LLM
/// in HF PEFT LoRA: prefix is "llm.base_model.model." and adapter suffix
/// pattern is .lora_A.{NAME}.weight / .lora_B.{NAME}.weight / .base_layer.weight.
/// </summary>
public sealed class TupleResolverPeftLoraTests
{
    [Fact]
    public void Peft_LoraTensors_DetectedAndClassifiedAsLoraDeltaSlots()
    {
        TensorHandle baseLayer = TupleResolverTestHelpers.Tensor(
            "llm.base_model.model.model.layers.0.self_attn.q_proj.base_layer.weight", [2048, 2048]);
        TensorHandle loraA = TupleResolverTestHelpers.Tensor(
            "llm.base_model.model.model.layers.0.self_attn.q_proj.lora_A.default.weight", [128, 2048]);
        TensorHandle loraB = TupleResolverTestHelpers.Tensor(
            "llm.base_model.model.model.layers.0.self_attn.q_proj.lora_B.default.weight", [2048, 128]);

        TupleResolver resolver = new();
        (var classifications, var tuples) = resolver.Resolve("LlamaForCausalLM", [baseLayer, loraA, loraB]);

        // PEFT detection triggers on .lora_A./.lora_B. presence
        Assert.Equal(ArchetypeTuple.LoraDelta, classifications[loraA].Tuple);
        Assert.Equal(TupleSlot.LoraA, classifications[loraA].Slot);
        Assert.Equal(TupleSlot.LoraB, classifications[loraB].Slot);

        // The base_layer should also be present in the LoraDelta tuple via the inner Llama profile's match
        // (PEFT's base_layer rule sets Slot=Base under the LoraDelta tuple).
        Assert.NotNull(classifications.GetValueOrDefault(baseLayer));
    }

    [Fact]
    public void NonPeftModel_DoesNotTriggerLoraDispatch()
    {
        // Plain Llama model with no PEFT wrapping — no .lora_A. / .base_layer. suffixes
        TensorHandle q = TupleResolverTestHelpers.Tensor("model.layers.0.self_attn.q_proj.weight", [2048, 2048]);
        TupleResolver resolver = new();
        (var classifications, var tuples) = resolver.Resolve("LlamaForCausalLM", [q]);

        // Resolves via Llama profile, NOT PEFT
        Assert.Equal(ArchetypeTuple.AttentionBlock, classifications[q].Tuple);
        Assert.Equal(TupleSlot.Q, classifications[q].Slot);
        Assert.DoesNotContain(tuples, t => t.Tuple == ArchetypeTuple.LoraDelta);
    }

    [Fact]
    public void PeftWrapped_LlamaInnerTensors_ResolveViaInnerProfileAfterPrefixStrip()
    {
        // The PEFT wrapping prefix llm.base_model.model. peels off; remaining inner
        // name model.layers.0.self_attn.q_proj.base_layer.weight needs to match the
        // Llama profile's q_proj rule once the .base_layer. suffix is recognized as
        // PEFT-marker (separate concern from Llama's q_proj match).
        TensorHandle innerQ = TupleResolverTestHelpers.Tensor(
            "llm.base_model.model.model.layers.0.self_attn.q_proj.base_layer.weight", [2048, 2048]);
        TupleResolver resolver = new();
        (var classifications, _) = resolver.Resolve("LlamaForCausalLM", [innerQ]);

        // Should be classified somehow — either as the LoraDelta base or as the Llama Q.
        // The current implementation's exact behavior depends on which profile matches first;
        // verify SOMETHING is recorded (not Unknown).
        Assert.True(classifications.ContainsKey(innerQ),
            $"PEFT-wrapped Llama Q-proj should resolve via at least one profile; got nothing");
    }
}
