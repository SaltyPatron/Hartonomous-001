using System;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// Discriminated-union root for the streaming ingestion pipeline. Decomposers
/// emit values of the eight concrete subtypes via <see cref="IRecordSink"/>;
/// the sink dispatches to per-kind channels and per-kind COPY drains.
///
/// There is no "batch" boundary in the producer's API surface — a record is
/// a record. The sink batches internally for COPY amortization (~4096 rows
/// or 250ms idle, whichever first) but the decomposer produces one at a time.
///
/// All record types are read-only structs/records to keep allocation pressure
/// low under tight emission loops (decomposers may emit millions per second
/// for tensor decomp). Hashes are byte[] because BLAKE3 output is 32 bytes
/// and we hand the array directly to NpgsqlBinaryImporter.WriteAsync without
/// copying.
/// </summary>
public abstract record IngestionRecord;
