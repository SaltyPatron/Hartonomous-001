using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// Streaming ingestion pipeline. Replaces the per-batch
/// <see cref="NpgsqlIngestionPipeline"/> with a continuous-flow design:
///
///   producer threads (decomposers) → bounded Channel per record kind →
///   per-kind drain task with long-lived NpgsqlBinaryImporter →
///   substrate.staging_* (persistent) → background flush worker drains
///   staging → substrate via substrate.drain_staging_* functions
///
/// Key differences from the old pipeline:
///   * No per-batch transactions. Each drain task commits its own COPY chunk
///     when it hits ~4096 rows or ~250ms idle.
///   * No per-batch TEMP staging tables. Staging is persistent
///     (substrate.staging_*) — survives reconnect, decouples producer from
///     consumer, queueable.
///   * No synchronous prime_edge_significance call inside producer
///     transactions. Significance priming runs as a separate background
///     hosted task on its own connection.
///   * Backpressure via bounded channels — when consumer can't keep up,
///     <see cref="EmitAsync"/> awaits naturally.
///   * One pipeline shared across all decomposers in a phase. No per-decomposer
///     pipelines competing for partition routing.
///
/// Lifecycle: caller constructs once per phase (or process), passes the
/// <see cref="IRecordSink"/> to all decomposers, calls <see cref="FlushAsync"/>
/// at end of phase, then disposes. Disposal completes channels, waits for
/// drain tasks to finish their last chunks.
/// </summary>
public sealed partial class StreamingIngestionPipeline : IRecordSink, IIngestionPipeline, IAsyncDisposable
{
    /// <summary>
    /// Channel capacity per record kind. ~65K bounded → ~MB-scale per-channel
    /// memory ceiling regardless of record count. EmitAsync awaits when full.
    /// </summary>
    private const int ChannelCapacity = 65_536;

    /// <summary>
    /// COPY chunk threshold. Each drain task commits its current binary
    /// import after this many rows OR after the idle timer fires, whichever
    /// first. Larger chunks amortize COPY overhead better; smaller chunks
    /// reduce crash blast radius.
    /// </summary>
    private const int CopyChunkRows = 4096;

    /// <summary>
    /// Idle timeout per drain task. If the channel is empty for this long,
    /// commit the current COPY chunk (even if under-full) so producers see
    /// their records persisted in bounded latency.
    /// </summary>
    private static readonly TimeSpan IdleFlushAfter = TimeSpan.FromMilliseconds(250);

    private readonly NpgsqlDataSource _dataSource;
    private readonly CodeResolver _codeResolver;
    private readonly ILogger<StreamingIngestionPipeline> _logger;
    private readonly CancellationTokenSource _shutdown = new();

    // One channel per record kind so each drain task can commit independently
    // without coordinating with other kinds. SingleReader=true means the
    // drain side is lock-free.
    private readonly Channel<EntityRecord> _entities;
    private readonly Channel<EntityClassificationRecord> _entityClassifications;
    private readonly Channel<EdgeRecord> _edges;
    private readonly Channel<EdgeMemberRecord> _edgeMembers;
    private readonly Channel<JunctionRecord> _junctions;
    private readonly Channel<PhysicalityRecord> _physicalities;
    private readonly Channel<SequenceRecord> _sequences;
    private readonly Channel<EntitySignificanceRecord> _entitySignificances;
    private readonly Channel<EntityModelSourceRecord> _entityModelSources;

    // Drain tasks — one per kind. Started in constructor, awaited in dispose.
    private readonly Task[] _drainTasks;

    // Per-kind row counters, updated atomically by drain tasks. Surfaces via
    // PipelineStats for observability and end-of-phase summary.
    private long _entitiesEmitted;
    private long _entityClassificationsEmitted;
    private long _edgesEmitted;
    private long _edgeMembersEmitted;
    private long _junctionsEmitted;
    private long _physicalitiesEmitted;
    private long _sequencesEmitted;
    private long _entitySignificancesEmitted;
    private long _entityModelSourcesEmitted;
    private long _copyCommits;
    private long _copyErrors;

    public StreamingIngestionPipeline(
        string connectionString,
        IReferenceDataReader referenceDataReader,
        ILogger<StreamingIngestionPipeline> logger)
    {
        NpgsqlConnectionStringBuilder csb = new(connectionString) { IncludeErrorDetail = true };
        NpgsqlDataSourceBuilder builder = new(csb.ConnectionString);
        _dataSource = builder.Build();
        _codeResolver = new CodeResolver(referenceDataReader);
        _logger = logger;

        BoundedChannelOptions opts = new(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        };

        _entities              = Channel.CreateBounded<EntityRecord>(opts);
        _entityClassifications = Channel.CreateBounded<EntityClassificationRecord>(opts);
        _edges                 = Channel.CreateBounded<EdgeRecord>(opts);
        _edgeMembers           = Channel.CreateBounded<EdgeMemberRecord>(opts);
        _junctions             = Channel.CreateBounded<JunctionRecord>(opts);
        _physicalities         = Channel.CreateBounded<PhysicalityRecord>(opts);
        _sequences             = Channel.CreateBounded<SequenceRecord>(opts);
        _entitySignificances   = Channel.CreateBounded<EntitySignificanceRecord>(opts);
        _entityModelSources    = Channel.CreateBounded<EntityModelSourceRecord>(opts);

        _drainTasks = new[]
        {
            Task.Run(() => DrainEntitiesAsync(_shutdown.Token)),
            Task.Run(() => DrainEntityClassificationsAsync(_shutdown.Token)),
            Task.Run(() => DrainEdgesAsync(_shutdown.Token)),
            Task.Run(() => DrainEdgeMembersAsync(_shutdown.Token)),
            Task.Run(() => DrainJunctionsAsync(_shutdown.Token)),
            Task.Run(() => DrainPhysicalitiesAsync(_shutdown.Token)),
            Task.Run(() => DrainSequencesAsync(_shutdown.Token)),
            Task.Run(() => DrainEntitySignificancesAsync(_shutdown.Token)),
            Task.Run(() => DrainEntityModelSourcesAsync(_shutdown.Token)),
        };
    }

    public StreamingPipelineStats Stats => new()
    {
        EntitiesEmitted               = _entitiesEmitted,
        EntityClassificationsEmitted  = _entityClassificationsEmitted,
        EdgesEmitted                  = _edgesEmitted,
        EdgeMembersEmitted            = _edgeMembersEmitted,
        JunctionsEmitted              = _junctionsEmitted,
        PhysicalitiesEmitted          = _physicalitiesEmitted,
        SequencesEmitted              = _sequencesEmitted,
        EntitySignificancesEmitted    = _entitySignificancesEmitted,
        EntityModelSourcesEmitted     = _entityModelSourcesEmitted,
        CopyCommits                   = _copyCommits,
        CopyErrors                    = _copyErrors,
    };

    // ── IIngestionPipeline compatibility shim ───────────────────────────
    // Unfolds an IIngestionBatch (the old API) into a sequence of individual
    // EmitAsync calls (the new API). Existing decomposers that build batches
    // get the streaming benefits immediately without rewriting — they just
    // see SubmitBatchAsync return faster (channel-bounded backpressure
    // instead of synchronous staging-flush dance).
    //
    // This shim is the migration ramp: every decomposer continues working,
    // E1..E9 just remove the IngestionBatch accumulation in favor of direct
    // EmitAsync calls. Until then, the shim does the unfolding for them.

    public IIngestionBatch CreateBatch(string provenanceCode) => new IngestionBatch(provenanceCode);

    public IIngestionBatch CreateBatch() => new IngestionBatch("system_computed");

    public async Task SubmitBatchAsync(IIngestionBatch batch, CancellationToken ct)
    {
        if (batch is not IngestionBatch b)
        {
            throw new ArgumentException("Batch must be created by this pipeline.", nameof(batch));
        }

        // Batch-level provenance is the decomposer's assertion stamp. Every
        // entity classification and every edge in this batch attributes to it
        // unless explicitly overridden. Falling back to "system_computed" via
        // the no-arg CreateBatch() is honest ("no decomposer is asserting
        // this") but should be rare — decomposers should pass their own
        // ProvenanceCode at batch creation.
        string batchProvenance = b.ProvenanceCode;

        // Entities first. Each EntityRecord fans into staging_entity (hash only)
        // AND staging_entity_classification (hash, type, provenance) via the
        // pipeline's EmitAsync.
        foreach (EntityEntry e in b.Entities)
        {
            await EmitAsync(new EntityRecord(e.EntityTypeCode, e.Hash, batchProvenance), ct).ConfigureAwait(false);
        }

        // Edges + their members. EdgeHash is computed from
        // (edge_type_id, role-ordered participant hashes).
        foreach (EdgeEntry edge in b.Edges)
        {
            int edgeTypeId = await _codeResolver.EdgeTypeIdAsync(edge.EdgeTypeCode, ct).ConfigureAwait(false);

            EdgeMemberSpec[] sorted = (EdgeMemberSpec[])edge.Members.Clone();
            Array.Sort(sorted, (a, c) => a.Position.CompareTo(c.Position));

            byte[][] orderedHashes = new byte[sorted.Length][];
            for (int j = 0; j < sorted.Length; j++)
            {
                orderedHashes[j] = sorted[j].Entity.Hash;
            }
            byte[] edgeHash = ComputeEdgeHash(edgeTypeId, orderedHashes);

            await EmitAsync(new EdgeRecord(edge.EdgeTypeCode, edgeHash, edge.ProvenanceCode), ct).ConfigureAwait(false);
            for (int j = 0; j < sorted.Length; j++)
            {
                await EmitAsync(new EdgeMemberRecord(
                    edge.EdgeTypeCode, edgeHash,
                    sorted[j].Entity.Hash,
                    sorted[j].RoleCode,
                    sorted[j].Position), ct).ConfigureAwait(false);
            }
        }

        // Junctions, physicality, sequences, significance, model sources.
        foreach (JunctionEntry j in b.Junctions)
        {
            await EmitAsync(new JunctionRecord(
                j.JunctionTable, j.Entity.Hash,
                j.ReferenceId, j.Mu), ct).ConfigureAwait(false);
        }

        foreach (PhysicalityEntry p in b.Physicalities)
        {
            byte[] contentHash = Hartonomous.Core.Compute.Common.Blake3.Hash(p.Wkb.AsSpan());
            await EmitAsync(new PhysicalityRecord(
                p.PhysicalityTypeCode,
                p.Entity.Hash,
                contentHash, p.Wkb), ct).ConfigureAwait(false);
        }

        foreach (SequenceEntry s in b.Sequences)
        {
            await EmitAsync(new SequenceRecord(
                s.Parent.Hash,
                s.Ordinal,
                s.Child.Hash,
                s.RleCount), ct).ConfigureAwait(false);
        }

        foreach (SignificanceEntry sig in b.Significances)
        {
            await EmitAsync(new EntitySignificanceRecord(
                sig.ContextTypeCode,
                sig.Entity.Hash,
                sig.InitialMu), ct).ConfigureAwait(false);
        }

        foreach (EntityModelSourceEntry e in b.EntityModelSources)
        {
            await EmitAsync(new EntityModelSourceRecord(
                e.Entity.Hash,
                e.ModelSourceId), ct).ConfigureAwait(false);
        }
    }

    public Task PopulateEdgeTrajectoriesAsync(CancellationToken ct)
    {
        // Edge trajectories are computed by the substrate's
        // populate_edge_trajectories function. The streaming pipeline keeps
        // this hook for compatibility — call from end-of-phase paths.
        // No-op for now; the centroid/trajectory writes happen via
        // PhysicalityRecord emissions per Track 1 / Track 2 design.
        return Task.CompletedTask;
    }

    PipelineStats IIngestionPipeline.Stats => new()
    {
        EntitiesSubmitted        = _entitiesEmitted,
        EdgesSubmitted           = _edgesEmitted,
        JunctionsSubmitted       = _junctionsEmitted,
        PhysicalitiesSubmitted   = _physicalitiesEmitted,
        SignificanceInitialized  = _entitySignificancesEmitted,
        EntityModelSourcesLinked = _entityModelSourcesEmitted,
        BatchesCommitted         = _copyCommits,
        BatchesFailed            = _copyErrors,
        TotalCommitTime          = TimeSpan.Zero,
    };

    private static byte[] ComputeEdgeHash(int edgeTypeId, byte[][] orderedMemberHashes)
    {
        int len = 4;
        for (int i = 0; i < orderedMemberHashes.Length; i++)
        {
            len += orderedMemberHashes[i].Length;
        }
        byte[] buffer = new byte[len];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), edgeTypeId);
        int offset = 4;
        for (int i = 0; i < orderedMemberHashes.Length; i++)
        {
            orderedMemberHashes[i].CopyTo(buffer.AsSpan(offset));
            offset += orderedMemberHashes[i].Length;
        }
        return Hartonomous.Core.Compute.Common.Blake3.Hash(buffer);
    }

    public ValueTask EmitAsync(IngestionRecord record, CancellationToken ct)
    {
        return record switch
        {
            EntityRecord r              => EmitEntityWithClassificationAsync(r, ct),
            EntityClassificationRecord r => _entityClassifications.Writer.WriteAsync(r, ct),
            EdgeRecord r                => _edges.Writer.WriteAsync(r, ct),
            EdgeMemberRecord r          => _edgeMembers.Writer.WriteAsync(r, ct),
            JunctionRecord r            => _junctions.Writer.WriteAsync(r, ct),
            PhysicalityRecord r         => _physicalities.Writer.WriteAsync(r, ct),
            SequenceRecord r            => _sequences.Writer.WriteAsync(r, ct),
            EntitySignificanceRecord r  => _entitySignificances.Writer.WriteAsync(r, ct),
            EntityModelSourceRecord r   => _entityModelSources.Writer.WriteAsync(r, ct),
            _ => throw new ArgumentException(
                $"Unknown IngestionRecord subtype: {record.GetType().Name}", nameof(record)),
        };
    }

    // EntityRecord fans into two channels: hash-only into staging_entity AND
    // (hash, type, provenance) into staging_entity_classification. This is
    // the Phase C unification — content identity vs decomposer-asserted
    // classification metadata.
    private async ValueTask EmitEntityWithClassificationAsync(EntityRecord r, CancellationToken ct)
    {
        await _entities.Writer.WriteAsync(r, ct).ConfigureAwait(false);
        await _entityClassifications.Writer.WriteAsync(
            new EntityClassificationRecord(r.Hash, r.EntityTypeCode, r.ProvenanceCode), ct)
            .ConfigureAwait(false);
    }

    public async ValueTask FlushAsync(CancellationToken ct)
    {
        // Mark all channels complete so drain loops exit their reader loops
        // after consuming everything currently buffered.
        _entities.Writer.TryComplete();
        _entityClassifications.Writer.TryComplete();
        _edges.Writer.TryComplete();
        _edgeMembers.Writer.TryComplete();
        _junctions.Writer.TryComplete();
        _physicalities.Writer.TryComplete();
        _sequences.Writer.TryComplete();
        _entitySignificances.Writer.TryComplete();
        _entityModelSources.Writer.TryComplete();

        // Wait for all drain tasks to finish their final chunks.
        await Task.WhenAll(_drainTasks).ConfigureAwait(false);
        Log.PipelineFlushed(_logger,
            _entitiesEmitted, _entityClassificationsEmitted,
            _edgesEmitted, _edgeMembersEmitted,
            _junctionsEmitted, _physicalitiesEmitted, _sequencesEmitted,
            _entitySignificancesEmitted, _entityModelSourcesEmitted,
            _copyCommits, _copyErrors);
    }

    public async ValueTask DisposeAsync()
    {
        // FlushAsync may have already run; idempotent re-completion is safe.
        try
        {
            await FlushAsync(default).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }

        _shutdown.Cancel();
        _shutdown.Dispose();
        await _dataSource.DisposeAsync().ConfigureAwait(false);
    }

    // ── Drain task pattern ───────────────────────────────────────────────
    // Each drain task:
    //   1. Opens a long-lived NpgsqlConnection.
    //   2. Loops: read records from the channel until ChunkRows or idle.
    //   3. Opens an NpgsqlBinaryImporter into the staging table for the kind.
    //   4. Pumps records into the importer.
    //   5. Calls CompleteAsync to commit the COPY chunk.
    //   6. Returns to the read loop for the next chunk.
    //   7. On channel close, finalizes its current chunk and exits.
    //
    // No transactions — COPY itself is atomic; CompleteAsync commits the
    // COPY's implicit transaction. Crashing mid-chunk means up to ChunkRows
    // un-staged records that the next run will re-emit (decomposer source
    // is deterministic; ON CONFLICT DO NOTHING dedupes at the substrate side).

    private async Task DrainEntitiesAsync(CancellationToken ct)
    {
        await DrainKindAsync(
            _entities.Reader,
            "COPY substrate.staging_entity (hash) FROM STDIN (FORMAT binary)",
            "entities",
            async (writer, rec) =>
            {
                await writer.StartRowAsync(ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.Hash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _entitiesEmitted);
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainEntityClassificationsAsync(CancellationToken ct)
    {
        await DrainKindAsync(
            _entityClassifications.Reader,
            "COPY substrate.staging_entity_classification (entity_hash, entity_type_id, provenance_id) FROM STDIN (FORMAT binary)",
            "entity_classifications",
            async (writer, rec) =>
            {
                int typeId = await _codeResolver.EntityTypeIdAsync(rec.EntityTypeCode, ct).ConfigureAwait(false);
                int provenanceId = await _codeResolver.ProvenanceIdAsync(rec.ProvenanceCode, ct).ConfigureAwait(false);
                await writer.StartRowAsync(ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.EntityHash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                await writer.WriteAsync(typeId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                await writer.WriteAsync(provenanceId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _entityClassificationsEmitted);
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainEdgesAsync(CancellationToken ct)
    {
        await DrainKindAsync(
            _edges.Reader,
            "COPY substrate.staging_edge (edge_type_id, hash, provenance_id) FROM STDIN (FORMAT binary)",
            "edges",
            async (writer, rec) =>
            {
                int edgeTypeId = await _codeResolver.EdgeTypeIdAsync(rec.EdgeTypeCode, ct).ConfigureAwait(false);
                int provenanceId = await _codeResolver.ProvenanceIdAsync(rec.ProvenanceCode, ct).ConfigureAwait(false);
                await writer.StartRowAsync(ct).ConfigureAwait(false);
                await writer.WriteAsync(edgeTypeId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.EdgeHash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                await writer.WriteAsync(provenanceId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _edgesEmitted);
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainEdgeMembersAsync(CancellationToken ct)
    {
        await DrainKindAsync(
            _edgeMembers.Reader,
            "COPY substrate.staging_edge_member (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position) FROM STDIN (FORMAT binary)",
            "edge_members",
            async (writer, rec) =>
            {
                int edgeTypeId = await _codeResolver.EdgeTypeIdAsync(rec.EdgeTypeCode, ct).ConfigureAwait(false);
                int roleId = await _codeResolver.EdgeRoleIdAsync(rec.RoleCode, ct).ConfigureAwait(false);
                await writer.StartRowAsync(ct).ConfigureAwait(false);
                await writer.WriteAsync(edgeTypeId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.EdgeHash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.EntityHash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                await writer.WriteAsync(roleId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.RolePosition, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _edgeMembersEmitted);
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainJunctionsAsync(CancellationToken ct)
    {
        await DrainKindAsync(
            _junctions.Reader,
            "COPY substrate.staging_junction (table_name, entity_hash, ref_id, mu) FROM STDIN (FORMAT binary)",
            "junctions",
            async (writer, rec) =>
            {
                if (!AllowedJunctionTables.Contains(rec.JunctionTable))
                {
                    throw new ArgumentException(
                        $"JunctionRecord.JunctionTable not in allowlist: '{rec.JunctionTable}'");
                }
                await writer.StartRowAsync(ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.JunctionTable, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.EntityHash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.ReferenceId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                if (rec.Mu.HasValue)
                {
                    await writer.WriteAsync(rec.Mu.Value, NpgsqlDbType.Double, ct).ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteNullAsync(ct).ConfigureAwait(false);
                }
                Interlocked.Increment(ref _junctionsEmitted);
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainPhysicalitiesAsync(CancellationToken ct)
    {
        await DrainKindAsync(
            _physicalities.Reader,
            "COPY substrate.staging_physicality (physicality_type_id, entity_hash, content_hash, wkb) FROM STDIN (FORMAT binary)",
            "physicalities",
            async (writer, rec) =>
            {
                int physTypeId = await _codeResolver.PhysicalityTypeIdAsync(rec.PhysicalityTypeCode, ct).ConfigureAwait(false);
                await writer.StartRowAsync(ct).ConfigureAwait(false);
                await writer.WriteAsync(physTypeId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.EntityHash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.ContentHash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.Wkb, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _physicalitiesEmitted);
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainSequencesAsync(CancellationToken ct)
    {
        await DrainKindAsync(
            _sequences.Reader,
            "COPY substrate.staging_sequence (parent_hash, ordinal, child_hash, rle_count) FROM STDIN (FORMAT binary)",
            "sequences",
            async (writer, rec) =>
            {
                await writer.StartRowAsync(ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.ParentEntityHash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.Ordinal, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.ChildEntityHash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.RleCount, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _sequencesEmitted);
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainEntitySignificancesAsync(CancellationToken ct)
    {
        await DrainKindAsync(
            _entitySignificances.Reader,
            "COPY substrate.staging_entity_significance (context_type_id, entity_hash, mu) FROM STDIN (FORMAT binary)",
            "entity_significances",
            async (writer, rec) =>
            {
                int contextId = await _codeResolver.SignificanceContextIdAsync(rec.ContextTypeCode, ct).ConfigureAwait(false);
                await writer.StartRowAsync(ct).ConfigureAwait(false);
                await writer.WriteAsync(contextId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.EntityHash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.InitialMu, NpgsqlDbType.Double, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _entitySignificancesEmitted);
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainEntityModelSourcesAsync(CancellationToken ct)
    {
        await DrainKindAsync(
            _entityModelSources.Reader,
            "COPY substrate.staging_entity_model_source (entity_hash, model_source_id) FROM STDIN (FORMAT binary)",
            "entity_model_sources",
            async (writer, rec) =>
            {
                await writer.StartRowAsync(ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.EntityHash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                await writer.WriteAsync((int)rec.ModelSourceId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _entityModelSourcesEmitted);
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Generic drain loop. Reads from the per-kind channel, accumulates rows
    /// into a long-lived NpgsqlBinaryImporter, commits the chunk on size or
    /// idle threshold, opens a new importer, repeats. Exits on channel close
    /// after committing the final partial chunk.
    /// </summary>
    private async Task DrainKindAsync<T>(
        ChannelReader<T> reader,
        string copySql,
        string kindName,
        Func<NpgsqlBinaryImporter, T, ValueTask> writeRow,
        CancellationToken ct)
    {
        try
        {
            await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                // Wait for at least one record (or channel close).
                if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    return; // channel closed and empty
                }

                Stopwatch chunkSw = Stopwatch.StartNew();
                NpgsqlBinaryImporter importer = await conn.BeginBinaryImportAsync(copySql, ct).ConfigureAwait(false);
                int rowsInChunk = 0;
                try
                {
                    // Pump until chunk-size, idle timeout, or channel close.
                    while (rowsInChunk < CopyChunkRows)
                    {
                        bool hasMore;
                        // Block briefly for next record; on idle, commit what we have.
                        if (reader.TryRead(out T? rec) && rec is not null)
                        {
                            await writeRow(importer, rec).ConfigureAwait(false);
                            rowsInChunk++;
                        }
                        else
                        {
                            // Channel was empty in the moment — wait briefly with timeout.
                            using CancellationTokenSource idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            idleCts.CancelAfter(IdleFlushAfter);
                            try
                            {
                                hasMore = await reader.WaitToReadAsync(idleCts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                // Idle timeout — commit the current partial chunk.
                                break;
                            }
                            if (!hasMore)
                            {
                                // Channel closed while we waited — finalize and exit.
                                break;
                            }
                            // Otherwise loop and TryRead will succeed.
                        }
                    }

                    await importer.CompleteAsync(ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _copyCommits);
                    Log.ChunkCommitted(_logger, kindName, rowsInChunk, chunkSw.Elapsed);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _copyErrors);
                    Log.ChunkFailed(_logger, kindName, rowsInChunk, chunkSw.Elapsed, ex);
                    try { await importer.CloseAsync(ct).ConfigureAwait(false); }
                    catch { /* importer may already be in failed state */ }
                    throw;
                }
                finally
                {
                    await importer.DisposeAsync().ConfigureAwait(false);
                }

                if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    return; // channel closed; nothing more to drain
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — fine.
        }
        catch (Exception ex)
        {
            Log.DrainTaskCrashed(_logger, kindName, ex);
            throw;
        }
    }

    // Junction allowlist mirrors NpgsqlIngestionPipeline; defended-in-depth
    // against decomposer typos. The substrate.drain_staging_junction_chunk
    // function ALSO checks via its CASE-on-table_name routing.
    private static readonly HashSet<string> AllowedJunctionTables = new(StringComparer.Ordinal)
    {
        "entity_pos", "entity_lexname", "entity_language", "entity_morph_feature",
        "model_architecture_class", "tensor_tensor_role", "pattern_deprel",
    };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Pipeline COPY chunk committed: kind={Kind} rows={Rows} elapsed={Elapsed}")]
        public static partial void ChunkCommitted(ILogger logger, string kind, int rows, TimeSpan elapsed);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Pipeline COPY chunk FAILED: kind={Kind} rows={Rows} elapsed={Elapsed}")]
        public static partial void ChunkFailed(ILogger logger, string kind, int rows, TimeSpan elapsed, Exception ex);

        [LoggerMessage(Level = LogLevel.Critical,
            Message = "Pipeline drain task CRASHED: kind={Kind}")]
        public static partial void DrainTaskCrashed(ILogger logger, string kind, Exception ex);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Pipeline flushed: entities={Entities} classifications={Classifications} edges={Edges} edge_members={EdgeMembers} junctions={Junctions} physicalities={Physicalities} sequences={Sequences} entity_significances={EntitySigs} entity_model_sources={ModelSources} commits={Commits} errors={Errors}")]
        public static partial void PipelineFlushed(ILogger logger,
            long entities, long classifications, long edges, long edgeMembers, long junctions,
            long physicalities, long sequences, long entitySigs, long modelSources,
            long commits, long errors);
    }
}
