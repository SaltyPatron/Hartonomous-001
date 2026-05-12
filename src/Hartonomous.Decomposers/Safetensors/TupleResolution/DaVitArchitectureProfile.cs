using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Hartonomous.Decomposers.Safetensors.TupleResolution;

public sealed class DaVitArchitectureProfile : IArchitectureProfile
{
    public string ArchitectureClass => "DaViT";

    public string? PrefixToStrip => null;

    private static readonly Regex ChannelQkv = new(
        @"^vision_tower\.(?<L>\d+)\.(?<B>\d+)\.channel_block\.channel_attn\.fn\.qkv\.weight$",
        RegexOptions.Compiled);

    private static readonly Regex ChannelProj = new(
        @"^vision_tower\.(?<L>\d+)\.(?<B>\d+)\.channel_block\.channel_attn\.fn\.proj\.weight$",
        RegexOptions.Compiled);

    private static readonly IReadOnlyList<FusedSplitSpec> QkvSplits =
    [
        new(TupleSlot.Q, Axis: 0, Ordinal: 0, Parts: 3),
        new(TupleSlot.K, Axis: 0, Ordinal: 1, Parts: 3),
        new(TupleSlot.V, Axis: 0, Ordinal: 2, Parts: 3),
    ];

    public IReadOnlyList<NamePatternRule> Rules { get; } = new List<NamePatternRule>
    {
        new(ChannelQkv,  PrimitiveKind.Linear, ArchetypeTuple.AttentionBlock, TupleSlot.Unknown,
            ModalityHint.ImagePatch, LayerGroupName: "L", FusedSplits: QkvSplits),
        new(ChannelProj, PrimitiveKind.Linear, ArchetypeTuple.AttentionBlock, TupleSlot.O,
            ModalityHint.ImagePatch, LayerGroupName: "L"),
    };

    public bool Matches(string architectureClass)
    {
        if (string.IsNullOrEmpty(architectureClass)) { return false; }
        return architectureClass.Contains("DaViT", System.StringComparison.OrdinalIgnoreCase)
            || architectureClass.Contains("Florence", System.StringComparison.OrdinalIgnoreCase);
    }
}
