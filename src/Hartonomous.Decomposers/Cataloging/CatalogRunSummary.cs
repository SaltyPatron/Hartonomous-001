namespace Hartonomous.Decomposers.Cataloging;

public sealed record CatalogRunSummary(
    string HubRoot,
    string OutputRoot,
    int Discovered,
    int Ingested,
    int UnsupportedV1,
    int Rejected,
    int DiscoveryFailed,
    int UnclassifiedTensors,
    int UniquePatterns);
