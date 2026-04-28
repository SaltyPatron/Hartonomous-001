using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Engine;

/// <summary>
/// Metadata about an edge in a traversal path. Hash-as-PK: source and
/// target are composite handles, not long ids.
/// </summary>
public sealed record EdgeInfo
{
    /// <summary>Composite identity: edge type code + BLAKE3 hash.</summary>
    public required EdgeHandle Handle { get; init; }

    /// <summary>Source-role participant handle, when the edge has a single source.</summary>
    public EntityHandle? Source { get; init; }

    /// <summary>Target-role participant handle, when the edge has a single target.</summary>
    public EntityHandle? Target { get; init; }

    /// <summary>Convenience accessor for the edge type code.</summary>
    public string EdgeTypeCode => Handle.EdgeTypeCode;
}
