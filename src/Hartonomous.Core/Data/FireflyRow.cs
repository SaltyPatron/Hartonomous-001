using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Data;

/// <summary>One per-(model, token) firefly POINTZM physicality.</summary>
public sealed record FireflyRow(
    EntityHandle TokenEntity,
    long ModelSourceId,
    double X,
    double Y,
    double Z,
    double M);
