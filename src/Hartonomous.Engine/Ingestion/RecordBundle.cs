using System.Collections.Generic;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// One atomic source-unit's worth of substrate records (entity +
/// classification + physicality + edges + significance + …). The bundled-emit
/// pipeline drains a bundle's members in dependency order inside a single
/// transaction; they commit together or fail together.
///
/// <para>
/// A bundle is partitioned across worker threads by the BLAKE3 prefix of its
/// <see cref="LeaderHash"/>. The leader is the first entity-tier hash in the
/// bundle (or, if the bundle carries only edges, the first edge hash) and
/// determines which worker's <c>Channel&lt;RecordBundle&gt;</c> receives it.
/// Hash-prefix partitioning is deterministic (Law #6): same hash → same
/// worker → same temp-table sequence → same byte-identical substrate state
/// regardless of N, regardless of run.
/// </para>
/// </summary>
internal sealed record RecordBundle(
    Hash32 LeaderHash,
    IReadOnlyList<IngestionRecord> Records);
