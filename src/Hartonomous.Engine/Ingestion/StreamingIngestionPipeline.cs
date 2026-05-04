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
/// Streaming ingestion pipeline. Producer threads (decomposers) push records
/// into bounded channels; per-kind drain tasks COPY records directly into
/// substrate core tables via session-local temp staging.
///
///   producer → bounded Channel per record kind →
///   drain task (long-lived NpgsqlConnection):
///     for each chunk:
///       TRUNCATE pg_temp.X_inflight
///       COPY pg_temp.X_inflight FROM STDIN BINARY (≤ChunkRows)
///       INSERT INTO substrate.X SELECT … FROM pg_temp.X_inflight ON CONFLICT DO NOTHING
///
/// Architectural changes vs the old staging+drain+primer triad:
///   * Persistent <c>substrate.staging_*</c> tables and the
///     <c>substrate.drain_staging_*_chunk</c> SQL functions are GONE. Drain
///     happens within the same connection that COPY-loaded the temp table,
///     before the next chunk reads — no cross-session staging pile-up, no
///     post-producer "catch-up drain", no shutdown-drain segfault risk.
///   * <c>BackgroundSignificancePrimer</c> is GONE. Edge / entity significance
///     records are emitted INLINE by producers (one row per (record × arena)
///     using the producer's known provenance.initial_mu and the arena
///     snapshot in <c>SignificanceContextCache</c>). AP-1 compliance: cross-
///     product against ALL arenas at emission, no cherry-picking.
///   * Edge LINESTRINGZM geometry is built INLINE in C# from participant
///     centroids tracked in an in-process LRU. No
///     <c>populate_edge_trajectories</c> post-pass.
///   * Producer-side dedup via per-channel <c>HashSet&lt;Hash32&gt;</c> drops
///     within-session duplicates before COPY; cross-session duplicates are
///     handled by ON CONFLICT DO NOTHING in the INSERT-SELECT step.
///   * Backpressure via bounded channels — when consumer can't keep up,
///     <c>EmitAsync</c> awaits naturally.
///
/// The temp tables auto-drop when the connection closes (default temp
/// behavior). No GC, no orphans, no cross-process state.
///
/// Lifecycle: caller constructs once per phase (or process), passes the
/// <c>IRecordSink</c> to all decomposers, calls <c>FlushAsync</c> at end of
/// phase, then disposes. Disposal completes channels, waits for drain tasks
/// to finish their last chunks, closes connections (drops temp tables).
/// </summary>
public sealed partial class StreamingIngestionPipeline : IRecordSink, IIngestionPipeline, IAsyncDisposable
{
    /// <summary>
    /// Channel capacity per record kind. ~65K bounded → ~MB-scale per-channel
    /// memory ceiling regardless of record count. EmitAsync awaits when full.
    /// </summary>
    private const int ChannelCapacity = 65_536;

    /// <summary>
    /// COPY chunk threshold. Each drain task COPY-loads up to this many rows
    /// into its temp table, then drains via INSERT-SELECT into substrate.
    /// Larger chunks amortize COPY overhead better; smaller chunks reduce
    /// crash blast radius.
    /// </summary>
    private const int CopyChunkRows = 4096;

    /// <summary>
    /// Idle timeout per drain task. If the channel is empty for this long,
    /// drain the current partial chunk (even if under-full) so producers see
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
    private readonly Channel<EdgeSignificanceRecord> _edgeSignificances;
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
    private long _edgeSignificancesEmitted;
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
        _edgeSignificances     = Channel.CreateBounded<EdgeSignificanceRecord>(opts);
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
            Task.Run(() => DrainEdgeSignificancesAsync(_shutdown.Token)),
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
        EdgeSignificancesEmitted      = _edgeSignificancesEmitted,
        EntityModelSourcesEmitted     = _entityModelSourcesEmitted,
        CopyCommits                   = _copyCommits,
        CopyErrors                    = _copyErrors,
    };

    // ── IIngestionPipeline compatibility shim ───────────────────────────
    // Unfolds an IIngestionBatch (the old API) into a sequence of individual
    // EmitAsync calls. Decomposers that still build IngestionBatch get the
    // streaming benefits without rewriting.

    public IIngestionBatch CreateBatch(string provenanceCode) => new IngestionBatch(provenanceCode);

    public IIngestionBatch CreateBatch() => new IngestionBatch("system_computed");

    public async Task SubmitBatchAsync(IIngestionBatch batch, CancellationToken ct)
    {
        if (batch is not IngestionBatch b)
        {
            throw new ArgumentException("Batch must be created by this pipeline.", nameof(batch));
        }

        string batchProvenance = b.ProvenanceCode;

        // Entities first. EntityRecord fans into substrate.entity (hash only)
        // AND substrate.entity_classification (hash, type, provenance) via
        // EmitAsync's internal split.
        foreach (EntityEntry e in b.Entities)
        {
            await EmitAsync(new EntityRecord(e.EntityTypeCode, e.Hash, batchProvenance), ct).ConfigureAwait(false);
        }

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
        // No-op: edge trajectories are now emitted INLINE by producers via
        // PhysicalityRecord on edge emission (W2C). No post-pass population.
        // Hook retained for IIngestionPipeline compatibility.
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
            EdgeSignificanceRecord r    => _edgeSignificances.Writer.WriteAsync(r, ct),
            EntityModelSourceRecord r   => _entityModelSources.Writer.WriteAsync(r, ct),
            _ => throw new ArgumentException(
                $"Unknown IngestionRecord subtype: {record.GetType().Name}", nameof(record)),
        };
    }

    // EntityRecord fans into two channels: hash-only into substrate.entity AND
    // (hash, type, provenance) into substrate.entity_classification. Phase C
    // unification: content identity vs decomposer-asserted classification.
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
        _edgeSignificances.Writer.TryComplete();
        _entityModelSources.Writer.TryComplete();

        // Wait for all drain tasks to finish their final chunks. Each drain
        // task drains its in-flight temp table after the channel closes
        // before exiting — so when WhenAll returns, every emitted record is
        // already in substrate. There is no separate catch-up drain phase.
        await Task.WhenAll(_drainTasks).ConfigureAwait(false);
        Log.PipelineFlushed(_logger,
            _entitiesEmitted, _entityClassificationsEmitted,
            _edgesEmitted, _edgeMembersEmitted,
            _junctionsEmitted, _physicalitiesEmitted, _sequencesEmitted,
            _entitySignificancesEmitted, _edgeSignificancesEmitted, _entityModelSourcesEmitted,
            _copyCommits, _copyErrors);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await FlushAsync(default).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }

        _shutdown.Cancel();
        _shutdown.Dispose();
        await _dataSource.DisposeAsync().ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Drain task definitions — one per record kind.
    //
    // Pattern: each drain task gets its OWN temp staging table created once
    // at connection open. Each chunk: TRUNCATE temp → COPY temp → INSERT
    // INTO substrate ... ON CONFLICT DO NOTHING from temp. Temp tables
    // auto-drop when the connection closes (default temp table behavior).
    //
    // No persistent staging tables. No background drain worker. The drain
    // happens within the same chunk that the COPY filled, before the next
    // chunk reads from the channel.
    // ═══════════════════════════════════════════════════════════════════

    private async Task DrainEntitiesAsync(CancellationToken ct)
    {
        await DrainKindAsync(
            _entities.Reader,
            tempCreate: """
                CREATE TEMP TABLE IF NOT EXISTS entity_inflight (
                    hash BYTEA NOT NULL
                )
                """,
            copySql: "COPY pg_temp.entity_inflight (hash) FROM STDIN (FORMAT binary)",
            truncateSql: "TRUNCATE pg_temp.entity_inflight",
            drainSql: """
                INSERT INTO substrate.entity (hash)
                SELECT DISTINCT hash FROM pg_temp.entity_inflight
                ON CONFLICT (hash) DO NOTHING
                """,
            kindName: "entities",
            writeRow: async (writer, rec) =>
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
            tempCreate: """
                CREATE TEMP TABLE IF NOT EXISTS entity_classification_inflight (
                    entity_hash    BYTEA NOT NULL,
                    entity_type_id INT   NOT NULL,
                    provenance_id  INT   NOT NULL
                )
                """,
            copySql: "COPY pg_temp.entity_classification_inflight (entity_hash, entity_type_id, provenance_id) FROM STDIN (FORMAT binary)",
            truncateSql: "TRUNCATE pg_temp.entity_classification_inflight",
            drainSql: """
                INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id)
                SELECT DISTINCT entity_hash, entity_type_id, provenance_id
                  FROM pg_temp.entity_classification_inflight ec
                 WHERE EXISTS (SELECT 1 FROM substrate.entity e WHERE e.hash = ec.entity_hash)
                ON CONFLICT (entity_hash, entity_type_id, provenance_id) DO NOTHING
                """,
            kindName: "entity_classifications",
            writeRow: async (writer, rec) =>
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
            tempCreate: """
                CREATE TEMP TABLE IF NOT EXISTS edge_inflight (
                    edge_type_id  INT   NOT NULL,
                    hash          BYTEA NOT NULL,
                    provenance_id INT   NOT NULL
                )
                """,
            copySql: "COPY pg_temp.edge_inflight (edge_type_id, hash, provenance_id) FROM STDIN (FORMAT binary)",
            truncateSql: "TRUNCATE pg_temp.edge_inflight",
            drainSql: """
                INSERT INTO substrate.edge (edge_type_id, hash, provenance_id)
                SELECT DISTINCT ON (edge_type_id, hash) edge_type_id, hash, provenance_id
                  FROM pg_temp.edge_inflight
                ON CONFLICT (edge_type_id, hash) DO NOTHING
                """,
            kindName: "edges",
            writeRow: async (writer, rec) =>
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
            tempCreate: """
                CREATE TEMP TABLE IF NOT EXISTS edge_member_inflight (
                    edge_type_id  INT   NOT NULL,
                    edge_hash     BYTEA NOT NULL,
                    entity_hash   BYTEA NOT NULL,
                    edge_role_id  INT   NOT NULL,
                    role_position INT   NOT NULL
                )
                """,
            copySql: "COPY pg_temp.edge_member_inflight (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position) FROM STDIN (FORMAT binary)",
            truncateSql: "TRUNCATE pg_temp.edge_member_inflight",
            drainSql: """
                INSERT INTO substrate.edge_member (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position)
                SELECT DISTINCT edge_type_id, edge_hash, entity_hash, edge_role_id, role_position
                  FROM pg_temp.edge_member_inflight
                ON CONFLICT DO NOTHING
                """,
            kindName: "edge_members",
            writeRow: async (writer, rec) =>
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
            tempCreate: """
                CREATE TEMP TABLE IF NOT EXISTS junction_inflight (
                    table_name  TEXT  NOT NULL,
                    entity_hash BYTEA NOT NULL,
                    ref_id      INT   NOT NULL,
                    mu          FLOAT8
                )
                """,
            copySql: "COPY pg_temp.junction_inflight (table_name, entity_hash, ref_id, mu) FROM STDIN (FORMAT binary)",
            truncateSql: "TRUNCATE pg_temp.junction_inflight",
            // Junction routing: one INSERT per allowlisted target table. The
            // ELSE branch silently discards rows with unknown table_name —
            // EmitAsync's allowlist check should prevent this in practice.
            drainSql: """
                WITH src AS (SELECT * FROM pg_temp.junction_inflight)
                  , ins_pos AS (
                        INSERT INTO substrate.entity_pos (entity_hash, pos_id, mu)
                        SELECT DISTINCT entity_hash, ref_id, COALESCE(mu, 1500.0)
                          FROM src WHERE table_name = 'entity_pos'
                        ON CONFLICT DO NOTHING
                        RETURNING 1
                    )
                  , ins_lex AS (
                        INSERT INTO substrate.entity_lexname (entity_hash, lexname_id)
                        SELECT DISTINCT entity_hash, ref_id
                          FROM src WHERE table_name = 'entity_lexname'
                        ON CONFLICT DO NOTHING
                        RETURNING 1
                    )
                  , ins_lang AS (
                        INSERT INTO substrate.entity_language (entity_hash, language_id)
                        SELECT DISTINCT entity_hash, ref_id
                          FROM src WHERE table_name = 'entity_language'
                        ON CONFLICT DO NOTHING
                        RETURNING 1
                    )
                  , ins_morph AS (
                        INSERT INTO substrate.entity_morph_feature (entity_hash, morph_feature_id)
                        SELECT DISTINCT entity_hash, ref_id
                          FROM src WHERE table_name = 'entity_morph_feature'
                        ON CONFLICT DO NOTHING
                        RETURNING 1
                    )
                  , ins_arch AS (
                        INSERT INTO substrate.model_architecture_class (entity_hash, architecture_class_id)
                        SELECT DISTINCT entity_hash, ref_id
                          FROM src WHERE table_name = 'model_architecture_class'
                        ON CONFLICT DO NOTHING
                        RETURNING 1
                    )
                  , ins_trole AS (
                        INSERT INTO substrate.tensor_tensor_role (entity_hash, tensor_role_id)
                        SELECT DISTINCT entity_hash, ref_id
                          FROM src WHERE table_name = 'tensor_tensor_role'
                        ON CONFLICT DO NOTHING
                        RETURNING 1
                    )
                  , ins_pdep AS (
                        INSERT INTO substrate.pattern_deprel (entity_hash, deprel_id, mu)
                        SELECT DISTINCT entity_hash, ref_id, COALESCE(mu, 1500.0)
                          FROM src WHERE table_name = 'pattern_deprel'
                        ON CONFLICT DO NOTHING
                        RETURNING 1
                    )
                SELECT COUNT(*) FROM (
                    SELECT 1 FROM ins_pos UNION ALL
                    SELECT 1 FROM ins_lex UNION ALL
                    SELECT 1 FROM ins_lang UNION ALL
                    SELECT 1 FROM ins_morph UNION ALL
                    SELECT 1 FROM ins_arch UNION ALL
                    SELECT 1 FROM ins_trole UNION ALL
                    SELECT 1 FROM ins_pdep
                ) all_ins
                """,
            kindName: "junctions",
            writeRow: async (writer, rec) =>
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
            tempCreate: """
                CREATE TEMP TABLE IF NOT EXISTS physicality_inflight (
                    physicality_type_id INT   NOT NULL,
                    entity_hash         BYTEA NOT NULL,
                    content_hash        BYTEA NOT NULL,
                    wkb                 BYTEA NOT NULL
                )
                """,
            copySql: "COPY pg_temp.physicality_inflight (physicality_type_id, entity_hash, content_hash, wkb) FROM STDIN (FORMAT binary)",
            truncateSql: "TRUNCATE pg_temp.physicality_inflight",
            // WKB → geometry conversion happens in this INSERT-SELECT step,
            // exactly as the deleted drain_staging_physicality_chunk did.
            // Producer streams raw WKB bytes (cheap to encode in C#);
            // ST_GeomFromWKB runs server-side once per chunk.
            drainSql: """
                INSERT INTO substrate.physicality (physicality_type_id, entity_hash, content_hash, geom)
                SELECT DISTINCT ON (physicality_type_id, entity_hash, content_hash)
                       physicality_type_id, entity_hash, content_hash, ST_GeomFromWKB(wkb, 0)
                  FROM pg_temp.physicality_inflight
                ON CONFLICT (physicality_type_id, entity_hash, content_hash) DO NOTHING
                """,
            kindName: "physicalities",
            writeRow: async (writer, rec) =>
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
            tempCreate: """
                CREATE TEMP TABLE IF NOT EXISTS sequence_inflight (
                    parent_hash BYTEA NOT NULL,
                    ordinal     INT   NOT NULL,
                    child_hash  BYTEA NOT NULL,
                    rle_count   INT   NOT NULL
                )
                """,
            copySql: "COPY pg_temp.sequence_inflight (parent_hash, ordinal, child_hash, rle_count) FROM STDIN (FORMAT binary)",
            truncateSql: "TRUNCATE pg_temp.sequence_inflight",
            drainSql: """
                INSERT INTO substrate.sequence (parent_hash, ordinal, child_hash, rle_count)
                SELECT DISTINCT ON (parent_hash, ordinal) parent_hash, ordinal, child_hash, rle_count
                  FROM pg_temp.sequence_inflight
                ON CONFLICT (parent_hash, ordinal) DO NOTHING
                """,
            kindName: "sequences",
            writeRow: async (writer, rec) =>
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
            tempCreate: """
                CREATE TEMP TABLE IF NOT EXISTS entity_significance_inflight (
                    context_type_id INT   NOT NULL,
                    entity_hash     BYTEA NOT NULL,
                    mu              FLOAT8 NOT NULL
                )
                """,
            copySql: "COPY pg_temp.entity_significance_inflight (context_type_id, entity_hash, mu) FROM STDIN (FORMAT binary)",
            truncateSql: "TRUNCATE pg_temp.entity_significance_inflight",
            drainSql: """
                INSERT INTO substrate.entity_significance (context_type_id, entity_hash, mu)
                SELECT DISTINCT ON (context_type_id, entity_hash) context_type_id, entity_hash, mu
                  FROM pg_temp.entity_significance_inflight
                ON CONFLICT (context_type_id, entity_hash) DO NOTHING
                """,
            kindName: "entity_significances",
            writeRow: async (writer, rec) =>
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

    private async Task DrainEdgeSignificancesAsync(CancellationToken ct)
    {
        await DrainKindAsync(
            _edgeSignificances.Reader,
            tempCreate: """
                CREATE TEMP TABLE IF NOT EXISTS edge_significance_inflight (
                    context_type_id INT   NOT NULL,
                    edge_type_id    INT   NOT NULL,
                    edge_hash       BYTEA NOT NULL,
                    mu              FLOAT8 NOT NULL
                )
                """,
            copySql: "COPY pg_temp.edge_significance_inflight (context_type_id, edge_type_id, edge_hash, mu) FROM STDIN (FORMAT binary)",
            truncateSql: "TRUNCATE pg_temp.edge_significance_inflight",
            drainSql: """
                INSERT INTO substrate.edge_significance (context_type_id, edge_type_id, edge_hash, mu)
                SELECT DISTINCT ON (context_type_id, edge_type_id, edge_hash) context_type_id, edge_type_id, edge_hash, mu
                  FROM pg_temp.edge_significance_inflight
                ON CONFLICT (context_type_id, edge_type_id, edge_hash) DO NOTHING
                """,
            kindName: "edge_significances",
            writeRow: async (writer, rec) =>
            {
                int contextId = await _codeResolver.SignificanceContextIdAsync(rec.ContextTypeCode, ct).ConfigureAwait(false);
                int edgeTypeId = await _codeResolver.EdgeTypeIdAsync(rec.EdgeTypeCode, ct).ConfigureAwait(false);
                await writer.StartRowAsync(ct).ConfigureAwait(false);
                await writer.WriteAsync(contextId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                await writer.WriteAsync(edgeTypeId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.EdgeHash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.InitialMu, NpgsqlDbType.Double, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _edgeSignificancesEmitted);
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainEntityModelSourcesAsync(CancellationToken ct)
    {
        await DrainKindAsync(
            _entityModelSources.Reader,
            tempCreate: """
                CREATE TEMP TABLE IF NOT EXISTS entity_model_source_inflight (
                    entity_hash     BYTEA NOT NULL,
                    model_source_id INT   NOT NULL
                )
                """,
            copySql: "COPY pg_temp.entity_model_source_inflight (entity_hash, model_source_id) FROM STDIN (FORMAT binary)",
            truncateSql: "TRUNCATE pg_temp.entity_model_source_inflight",
            drainSql: """
                INSERT INTO substrate.entity_model_source (entity_hash, model_source_id)
                SELECT DISTINCT entity_hash, model_source_id
                  FROM pg_temp.entity_model_source_inflight ems
                 WHERE EXISTS (SELECT 1 FROM substrate.entity e WHERE e.hash = ems.entity_hash)
                ON CONFLICT (entity_hash, model_source_id) DO NOTHING
                """,
            kindName: "entity_model_sources",
            writeRow: async (writer, rec) =>
            {
                await writer.StartRowAsync(ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.EntityHash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                await writer.WriteAsync((int)rec.ModelSourceId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _entityModelSourcesEmitted);
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Generic drain loop. Each drain task gets its OWN session-local temp
    /// table created once at connection open. Per chunk: TRUNCATE temp,
    /// COPY rows into temp, then INSERT-SELECT into substrate with ON
    /// CONFLICT DO NOTHING. Temp tables auto-drop on connection close.
    /// </summary>
    private async Task DrainKindAsync<T>(
        ChannelReader<T> reader,
        string tempCreate,
        string copySql,
        string truncateSql,
        string drainSql,
        string kindName,
        Func<NpgsqlBinaryImporter, T, ValueTask> writeRow,
        CancellationToken ct)
    {
        try
        {
            await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

            // One-time temp table create. Persists for the connection's
            // lifetime; auto-drops on close. No ON COMMIT clause — we don't
            // wrap chunks in explicit transactions.
            await using (NpgsqlCommand createCmd = new(tempCreate, conn))
            {
                await createCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            while (!ct.IsCancellationRequested)
            {
                // Wait for at least one record (or channel close).
                if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    return; // channel closed and empty
                }

                Stopwatch chunkSw = Stopwatch.StartNew();

                // Reset the temp table for this chunk.
                await using (NpgsqlCommand truncCmd = new(truncateSql, conn))
                {
                    await truncCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                // Phase 1: COPY rows from the channel into the temp table
                // until ChunkRows or idle timeout.
                int rowsInChunk = 0;
                NpgsqlBinaryImporter importer = await conn.BeginBinaryImportAsync(copySql, ct).ConfigureAwait(false);
                try
                {
                    while (rowsInChunk < CopyChunkRows)
                    {
                        if (reader.TryRead(out T? rec) && rec is not null)
                        {
                            await writeRow(importer, rec).ConfigureAwait(false);
                            rowsInChunk++;
                        }
                        else
                        {
                            using CancellationTokenSource idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            idleCts.CancelAfter(IdleFlushAfter);
                            bool hasMore;
                            try
                            {
                                hasMore = await reader.WaitToReadAsync(idleCts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                // Idle timeout — drain whatever we have.
                                break;
                            }
                            if (!hasMore)
                            {
                                // Channel closed while we waited — drain final partial chunk and exit.
                                break;
                            }
                        }
                    }

                    await importer.CompleteAsync(ct).ConfigureAwait(false);
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

                if (rowsInChunk > 0)
                {
                    // Phase 2: drain temp into substrate with ON CONFLICT.
                    try
                    {
                        await using NpgsqlCommand drainCmd = new(drainSql, conn);
                        await drainCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        Interlocked.Increment(ref _copyCommits);
                        Log.ChunkCommitted(_logger, kindName, rowsInChunk, chunkSw.Elapsed);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _copyErrors);
                        Log.ChunkFailed(_logger, kindName, rowsInChunk, chunkSw.Elapsed, ex);
                        throw;
                    }
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

    // Junction allowlist mirrors the deleted NpgsqlIngestionPipeline; defended-
    // in-depth against decomposer typos. The drain SQL's WHERE-table_name
    // CTE branches further validate.
    private static readonly HashSet<string> AllowedJunctionTables = new(StringComparer.Ordinal)
    {
        "entity_pos", "entity_lexname", "entity_language", "entity_morph_feature",
        "model_architecture_class", "tensor_tensor_role", "pattern_deprel",
    };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Pipeline chunk drained: kind={Kind} rows={Rows} elapsed={Elapsed}")]
        public static partial void ChunkCommitted(ILogger logger, string kind, int rows, TimeSpan elapsed);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Pipeline chunk FAILED: kind={Kind} rows={Rows} elapsed={Elapsed}")]
        public static partial void ChunkFailed(ILogger logger, string kind, int rows, TimeSpan elapsed, Exception ex);

        [LoggerMessage(Level = LogLevel.Critical,
            Message = "Pipeline drain task CRASHED: kind={Kind}")]
        public static partial void DrainTaskCrashed(ILogger logger, string kind, Exception ex);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Pipeline flushed: entities={Entities} classifications={Classifications} edges={Edges} edge_members={EdgeMembers} junctions={Junctions} physicalities={Physicalities} sequences={Sequences} entity_sigs={EntitySigs} edge_sigs={EdgeSigs} model_sources={ModelSources} commits={Commits} errors={Errors}")]
        public static partial void PipelineFlushed(ILogger logger,
            long entities, long classifications, long edges, long edgeMembers, long junctions,
            long physicalities, long sequences, long entitySigs, long edgeSigs, long modelSources,
            long commits, long errors);
    }
}
