using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Hartonomous.Decomposers.Safetensors.TupleResolution;

public sealed class FluxVaeArchitectureProfile : IArchitectureProfile
{
    public string ArchitectureClass => "FluxVae";

    public string? PrefixToStrip => null;

    private static readonly Regex AttnQ = new(@"^(?:encoder|decoder)\.mid\.attn_1\.q\.weight$", RegexOptions.Compiled);
    private static readonly Regex AttnK = new(@"^(?:encoder|decoder)\.mid\.attn_1\.k\.weight$", RegexOptions.Compiled);
    private static readonly Regex AttnV = new(@"^(?:encoder|decoder)\.mid\.attn_1\.v\.weight$", RegexOptions.Compiled);
    private static readonly Regex AttnO = new(@"^(?:encoder|decoder)\.mid\.attn_1\.proj_out\.weight$", RegexOptions.Compiled);

    public IReadOnlyList<NamePatternRule> Rules { get; } = new List<NamePatternRule>
    {
        new(AttnQ, PrimitiveKind.Linear, ArchetypeTuple.VaeAttnBlock, TupleSlot.Q, ModalityHint.ImagePatch),
        new(AttnK, PrimitiveKind.Linear, ArchetypeTuple.VaeAttnBlock, TupleSlot.K, ModalityHint.ImagePatch),
        new(AttnV, PrimitiveKind.Linear, ArchetypeTuple.VaeAttnBlock, TupleSlot.V, ModalityHint.ImagePatch),
        new(AttnO, PrimitiveKind.Linear, ArchetypeTuple.VaeAttnBlock, TupleSlot.O, ModalityHint.ImagePatch),
    };

    public bool Matches(string architectureClass)
    {
        if (string.IsNullOrEmpty(architectureClass)) { return false; }
        return architectureClass.Contains("VAE", System.StringComparison.OrdinalIgnoreCase)
            || architectureClass.Contains("Autoencoder", System.StringComparison.OrdinalIgnoreCase)
            || architectureClass.Contains("Flux", System.StringComparison.OrdinalIgnoreCase);
    }
}
