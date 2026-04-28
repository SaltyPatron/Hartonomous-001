using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// One row for substrate.entity_significance. The pipeline writes
/// (context_type_id, entity_type_id, entity_hash, mu, sigma, volatility, games)
/// directly — split from edge_significance, no XOR discriminator.
/// </summary>
internal readonly record struct SignificanceEntry(
    EntityHandle Entity,
    string ContextTypeCode,
    double InitialMu);
