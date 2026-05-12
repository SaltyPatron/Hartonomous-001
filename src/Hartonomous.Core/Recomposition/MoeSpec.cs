namespace Hartonomous.Core.Recomposition;

/// <summary>MoE configuration sub-record. Omit at the parent level for monolith.</summary>
public sealed record MoeSpec(
    int NumExperts,
    int TopK,
    int SharedExperts);
