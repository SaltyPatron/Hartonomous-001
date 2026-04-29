namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One substrate.entity_significance row. Initial Mu seeded by the
/// decomposer's per-context trust prior; sigma/volatility/games default at
/// the substrate side (350.0 / 0.06 / 0). Decomposer-emitted significance
/// is the at-ingest seed; arena outcomes update via Glicko-2 later.
/// </summary>
public sealed record EntitySignificanceRecord(
    string ContextTypeCode,
    string EntityTypeCode,
    byte[] EntityHash,
    double InitialMu) : IngestionRecord;
