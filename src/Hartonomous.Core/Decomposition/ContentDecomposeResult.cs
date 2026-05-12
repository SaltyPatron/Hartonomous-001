using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Decomposition;

/// <summary>Result of content decomposition, including the top-level root entity.</summary>
public sealed record ContentDecomposeResult(
    EntityHandle RootHandle,
    byte[] RootHash,
    long EntityCount,
    long EdgeCount);
