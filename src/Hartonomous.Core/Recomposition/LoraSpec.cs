namespace Hartonomous.Core.Recomposition;

/// <summary>LoRA adapter spec. One per target tensor role to adapt.</summary>
public sealed record LoraSpec(
    string TargetRoleCode,
    int Rank);
