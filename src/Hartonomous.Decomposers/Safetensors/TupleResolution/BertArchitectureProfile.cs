using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Hartonomous.Decomposers.Safetensors.TupleResolution;

/// <summary>
/// Per docs/01-tensor-primitive-spec.md §III BERT family table. Covers BERT,
/// DistilBERT, MiniLM, RoBERTa, ELECTRA, and architectural descendants.
///
/// Patterns are written against name-after-prefix-strip; outer wrappers
/// (e.g. PEFT LoRA's <c>base_model.model.</c>) get peeled off before
/// matching by the TupleResolver.
/// </summary>
public sealed class BertArchitectureProfile : IArchitectureProfile
{
    public string ArchitectureClass => "BertModel";

    public string? PrefixToStrip => null;

    private static readonly Regex EmbedWord = new(@"^embeddings\.word_embeddings\.weight$", RegexOptions.Compiled);
    private static readonly Regex EmbedPos = new(@"^embeddings\.position_embeddings\.weight$", RegexOptions.Compiled);
    private static readonly Regex EmbedType = new(@"^embeddings\.token_type_embeddings\.weight$", RegexOptions.Compiled);
    private static readonly Regex EmbedLnScale = new(@"^embeddings\.LayerNorm\.weight$", RegexOptions.Compiled);
    private static readonly Regex EmbedLnBias = new(@"^embeddings\.LayerNorm\.bias$", RegexOptions.Compiled);

    private static readonly Regex AttnQ = new(@"^encoder\.layer\.(?<L>\d+)\.attention\.self\.query\.(weight|bias)$", RegexOptions.Compiled);
    private static readonly Regex AttnK = new(@"^encoder\.layer\.(?<L>\d+)\.attention\.self\.key\.(weight|bias)$", RegexOptions.Compiled);
    private static readonly Regex AttnV = new(@"^encoder\.layer\.(?<L>\d+)\.attention\.self\.value\.(weight|bias)$", RegexOptions.Compiled);
    private static readonly Regex AttnO = new(@"^encoder\.layer\.(?<L>\d+)\.attention\.output\.dense\.(weight|bias)$", RegexOptions.Compiled);
    private static readonly Regex AttnLnScale = new(@"^encoder\.layer\.(?<L>\d+)\.attention\.output\.LayerNorm\.weight$", RegexOptions.Compiled);
    private static readonly Regex AttnLnBias = new(@"^encoder\.layer\.(?<L>\d+)\.attention\.output\.LayerNorm\.bias$", RegexOptions.Compiled);

    private static readonly Regex FfnIntermediate = new(@"^encoder\.layer\.(?<L>\d+)\.intermediate\.dense\.(weight|bias)$", RegexOptions.Compiled);
    private static readonly Regex FfnOutput = new(@"^encoder\.layer\.(?<L>\d+)\.output\.dense\.(weight|bias)$", RegexOptions.Compiled);
    private static readonly Regex FfnLnScale = new(@"^encoder\.layer\.(?<L>\d+)\.output\.LayerNorm\.weight$", RegexOptions.Compiled);
    private static readonly Regex FfnLnBias = new(@"^encoder\.layer\.(?<L>\d+)\.output\.LayerNorm\.bias$", RegexOptions.Compiled);

    private static readonly Regex Pooler = new(@"^pooler\.dense\.(weight|bias)$", RegexOptions.Compiled);

    public IReadOnlyList<NamePatternRule> Rules { get; } = new List<NamePatternRule>
    {
        new(EmbedWord,         PrimitiveKind.Lookup,        ArchetypeTuple.EmbeddingLookup,  TupleSlot.Table,         ModalityHint.Text),
        new(EmbedPos,          PrimitiveKind.Lookup,        ArchetypeTuple.EmbeddingLookup,  TupleSlot.Table,         ModalityHint.Position),
        new(EmbedType,         PrimitiveKind.Lookup,        ArchetypeTuple.EmbeddingLookup,  TupleSlot.Table,         ModalityHint.Text),
        new(EmbedLnScale,      PrimitiveKind.Normalization, ArchetypeTuple.EmbeddingLookup,  TupleSlot.Scale,         ModalityHint.Text),
        new(EmbedLnBias,       PrimitiveKind.Normalization, ArchetypeTuple.EmbeddingLookup,  TupleSlot.Offset,        ModalityHint.Text),

        new(AttnQ,             PrimitiveKind.Linear,        ArchetypeTuple.AttentionBlock,   TupleSlot.Q,             ModalityHint.Text, LayerGroupName: "L"),
        new(AttnK,             PrimitiveKind.Linear,        ArchetypeTuple.AttentionBlock,   TupleSlot.K,             ModalityHint.Text, LayerGroupName: "L"),
        new(AttnV,             PrimitiveKind.Linear,        ArchetypeTuple.AttentionBlock,   TupleSlot.V,             ModalityHint.Text, LayerGroupName: "L"),
        new(AttnO,             PrimitiveKind.Linear,        ArchetypeTuple.AttentionBlock,   TupleSlot.O,             ModalityHint.Text, LayerGroupName: "L"),
        new(AttnLnScale,       PrimitiveKind.Normalization, ArchetypeTuple.AttentionBlock,   TupleSlot.Scale,         ModalityHint.Text, LayerGroupName: "L"),
        new(AttnLnBias,        PrimitiveKind.Normalization, ArchetypeTuple.AttentionBlock,   TupleSlot.Offset,        ModalityHint.Text, LayerGroupName: "L"),

        new(FfnIntermediate,   PrimitiveKind.Linear,        ArchetypeTuple.BertFfn,          TupleSlot.Intermediate,  ModalityHint.Text, LayerGroupName: "L"),
        new(FfnOutput,         PrimitiveKind.Linear,        ArchetypeTuple.BertFfn,          TupleSlot.Output,        ModalityHint.Text, LayerGroupName: "L"),
        new(FfnLnScale,        PrimitiveKind.Normalization, ArchetypeTuple.BertFfn,          TupleSlot.Scale,         ModalityHint.Text, LayerGroupName: "L"),
        new(FfnLnBias,         PrimitiveKind.Normalization, ArchetypeTuple.BertFfn,          TupleSlot.Offset,        ModalityHint.Text, LayerGroupName: "L"),

        new(Pooler,            PrimitiveKind.Linear,        ArchetypeTuple.EmbeddingLookup,  TupleSlot.LmHead,        ModalityHint.Text),
    };

    public bool Matches(string architectureClass)
    {
        if (string.IsNullOrEmpty(architectureClass)) { return false; }
        return architectureClass.Contains("Bert", System.StringComparison.OrdinalIgnoreCase)
            || architectureClass.Contains("MiniLM", System.StringComparison.OrdinalIgnoreCase)
            || architectureClass.Contains("DistilBert", System.StringComparison.OrdinalIgnoreCase)
            || architectureClass.Contains("Roberta", System.StringComparison.OrdinalIgnoreCase)
            || architectureClass.Contains("Electra", System.StringComparison.OrdinalIgnoreCase);
    }
}
