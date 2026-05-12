using System.Collections.Generic;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Data;

/// <summary>
/// One token-pair attestation edge from substrate, with consensus mu per
/// arena requested.
/// </summary>
public sealed record AttestationRow(
    string EdgeTypeCode,
    byte[] EdgeHash,
    IReadOnlyList<EntityHandle> Participants,
    IReadOnlyDictionary<string, double> ArenaMu,
    int GamesAggregate);
