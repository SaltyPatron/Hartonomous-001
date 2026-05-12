namespace Hartonomous.Core.Decomposition;

/// <summary>
/// Options for content decomposition: provenance, top-level entity type code,
/// and trust prior.
/// </summary>
public sealed record ContentDecomposeOptions(
    string ProvenanceCode,
    string TopEntityType,
    double TrustMu);
