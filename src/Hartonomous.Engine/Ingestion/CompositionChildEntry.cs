using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// In-memory composition metadata captured before it is attached to the
/// parent's physicality row.
/// </summary>
internal readonly record struct CompositionChildEntry(
    EntityHandle Parent,
    int Ordinal,
    EntityHandle Child,
    int RleCount);
