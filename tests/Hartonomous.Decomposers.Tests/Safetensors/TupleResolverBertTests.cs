using System.Linq;
using Hartonomous.Decomposers.Safetensors;
using Hartonomous.Decomposers.Safetensors.Passes;
using Hartonomous.Decomposers.Safetensors.TupleResolution;
using Xunit;

namespace Hartonomous.Decomposers.Tests.Safetensors;

/// <summary>
/// Verifies the BertArchitectureProfile resolves real BERT-family tensor
/// names (taken from the actual sentence-transformers/all-MiniLM-L6-v2
/// model in the farm) to the correct (PrimitiveKind, ArchetypeTuple,
/// TupleSlot, LayerIdx) classifications.
/// </summary>
public sealed class TupleResolverBertTests
{
    private const string ArchClass = "BertModel";

    [Fact]
    public void Embedding_TableTensor_ClassifiesAsLookup()
    {
        TensorHandle t = TupleResolverTestHelpers.Tensor("embeddings.word_embeddings.weight", [30522, 384]);
        TupleResolver resolver = new();
        (var classifications, _) = resolver.Resolve(ArchClass, [t]);
        TensorClassification cls = classifications[t];
        Assert.Equal(PrimitiveKind.Lookup, cls.Primitive);
        Assert.Equal(ArchetypeTuple.EmbeddingLookup, cls.Tuple);
        Assert.Equal(TupleSlot.Table, cls.Slot);
        Assert.Equal(ModalityHint.Text, cls.Modality);
    }

    [Fact]
    public void Attention_QKV_ClassifiesAsLinearAttentionBlockSlots()
    {
        TensorHandle q = TupleResolverTestHelpers.Tensor("encoder.layer.3.attention.self.query.weight", [384, 384]);
        TensorHandle k = TupleResolverTestHelpers.Tensor("encoder.layer.3.attention.self.key.weight", [384, 384]);
        TensorHandle v = TupleResolverTestHelpers.Tensor("encoder.layer.3.attention.self.value.weight", [384, 384]);
        TensorHandle o = TupleResolverTestHelpers.Tensor("encoder.layer.3.attention.output.dense.weight", [384, 384]);
        TupleResolver resolver = new();
        (var classifications, var tuples) = resolver.Resolve(ArchClass, [q, k, v, o]);

        Assert.Equal(PrimitiveKind.Linear, classifications[q].Primitive);
        Assert.Equal(ArchetypeTuple.AttentionBlock, classifications[q].Tuple);
        Assert.Equal(TupleSlot.Q, classifications[q].Slot);
        Assert.Equal(3, classifications[q].LayerIndex);

        Assert.Equal(TupleSlot.K, classifications[k].Slot);
        Assert.Equal(TupleSlot.V, classifications[v].Slot);
        Assert.Equal(TupleSlot.O, classifications[o].Slot);

        // All four bucket into ONE AttentionBlock tuple at layer 3
        ResolvedTuple attentionTuple = tuples.Single(t => t.Tuple == ArchetypeTuple.AttentionBlock && t.LayerIndex == 3);
        Assert.Equal(4, attentionTuple.Members.Count);
    }

    [Fact]
    public void Ffn_IntermediateAndOutput_ClassifiesAsBertFfnSlots()
    {
        TensorHandle inter = TupleResolverTestHelpers.Tensor("encoder.layer.0.intermediate.dense.weight", [1536, 384]);
        TensorHandle outp = TupleResolverTestHelpers.Tensor("encoder.layer.0.output.dense.weight", [384, 1536]);
        TupleResolver resolver = new();
        (var classifications, var tuples) = resolver.Resolve(ArchClass, [inter, outp]);

        Assert.Equal(ArchetypeTuple.BertFfn, classifications[inter].Tuple);
        Assert.Equal(TupleSlot.Intermediate, classifications[inter].Slot);
        Assert.Equal(0, classifications[inter].LayerIndex);

        Assert.Equal(ArchetypeTuple.BertFfn, classifications[outp].Tuple);
        Assert.Equal(TupleSlot.Output, classifications[outp].Slot);
        Assert.Equal(0, classifications[outp].LayerIndex);

        // Both bucket into ONE BertFfn tuple at layer 0
        ResolvedTuple ffnTuple = tuples.Single(t => t.Tuple == ArchetypeTuple.BertFfn && t.LayerIndex == 0);
        Assert.Equal(2, ffnTuple.Members.Count);
    }

    [Fact]
    public void Embedding_PositionAndType_ClassifyAsSeparateLookupTuples()
    {
        TensorHandle pos = TupleResolverTestHelpers.Tensor("embeddings.position_embeddings.weight", [512, 384]);
        TensorHandle typ = TupleResolverTestHelpers.Tensor("embeddings.token_type_embeddings.weight", [2, 384]);
        TupleResolver resolver = new();
        (var classifications, _) = resolver.Resolve(ArchClass, [pos, typ]);

        Assert.Equal(PrimitiveKind.Lookup, classifications[pos].Primitive);
        Assert.Equal(TupleSlot.Table, classifications[pos].Slot);
        Assert.Equal(ModalityHint.Position, classifications[pos].Modality);

        Assert.Equal(PrimitiveKind.Lookup, classifications[typ].Primitive);
        Assert.Equal(ModalityHint.Text, classifications[typ].Modality);
    }

    [Fact]
    public void LayerNorms_ClassifyAsNormalizationWithCorrectTupleAssociation()
    {
        // Embedding LN (γ + β)
        TensorHandle embedLnW = TupleResolverTestHelpers.Tensor("embeddings.LayerNorm.weight", [384]);
        TensorHandle embedLnB = TupleResolverTestHelpers.Tensor("embeddings.LayerNorm.bias", [384]);
        // Post-attention LN at layer 2
        TensorHandle attnLnW = TupleResolverTestHelpers.Tensor("encoder.layer.2.attention.output.LayerNorm.weight", [384]);
        TensorHandle attnLnB = TupleResolverTestHelpers.Tensor("encoder.layer.2.attention.output.LayerNorm.bias", [384]);
        // Post-FFN LN at layer 2
        TensorHandle ffnLnW = TupleResolverTestHelpers.Tensor("encoder.layer.2.output.LayerNorm.weight", [384]);
        TensorHandle ffnLnB = TupleResolverTestHelpers.Tensor("encoder.layer.2.output.LayerNorm.bias", [384]);

        TupleResolver resolver = new();
        (var classifications, _) = resolver.Resolve(ArchClass, [embedLnW, embedLnB, attnLnW, attnLnB, ffnLnW, ffnLnB]);

        Assert.All(new[] { embedLnW, embedLnB, attnLnW, attnLnB, ffnLnW, ffnLnB },
            t => Assert.Equal(PrimitiveKind.Normalization, classifications[t].Primitive));

        // γ vs β on slot
        Assert.Equal(TupleSlot.Scale, classifications[embedLnW].Slot);
        Assert.Equal(TupleSlot.Offset, classifications[embedLnB].Slot);

        // Post-attention LN belongs to AttentionBlock at layer 2
        Assert.Equal(ArchetypeTuple.AttentionBlock, classifications[attnLnW].Tuple);
        Assert.Equal(2, classifications[attnLnW].LayerIndex);
    }

    [Fact]
    public void UnknownTensorName_ProducesNoClassification()
    {
        TensorHandle weird = TupleResolverTestHelpers.Tensor("not.a.bert.thing", [42]);
        TupleResolver resolver = new();
        (var classifications, _) = resolver.Resolve(ArchClass, [weird]);
        Assert.False(classifications.ContainsKey(weird));
    }
}
