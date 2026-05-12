namespace Hartonomous.Core.Decomposition;

/// <summary>
/// Context for metadata decomposition. Carries provenance and trust prior for
/// emitted text_composition entities and edges.
/// </summary>
public sealed record MetadataDecomposeContext(
    string ProvenanceCode,
    double TrustMu,
    string SourceFilePath);
