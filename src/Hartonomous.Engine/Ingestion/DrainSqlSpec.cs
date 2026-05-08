namespace Hartonomous.Engine.Ingestion;

internal readonly record struct DrainSqlSpec(
    string TempCreate,
    string Copy,
    string Truncate,
    string Drain);