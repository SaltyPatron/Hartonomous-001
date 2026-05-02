using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Data;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// Hash-as-PK ingestion pipeline. Every COPY writes
/// (entity_type_id, entity_hash) or (edge_type_id, edge_hash) directly into
/// substrate tables. There is no staging table for entity ID resolution —
/// the hash the decomposer computed IS the foreign key. The pipeline never
/// JOINs back to substrate.entity to discover a surrogate id; the surrogate
/// id does not exist.
///
/// Single transaction per batch. Order: entities, edges + edge_members,
/// junctions, physicality, sequence, entity_significance, entity_model_source.
/// Edge trajectories and edge_significance priming happen in dedicated phase
/// runners after the per-batch transactions commit.
/// </summary>
public sealed partial class NpgsqlIngestionPipeline : IIngestionPipeline
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly CodeResolver _codeResolver;
    private readonly ILogger<NpgsqlIngestionPipeline> _logger;

    private long _entitiesSubmitted;
    private long _edgesSubmitted;
    private long _junctionsSubmitted;
    private long _physicalitiesSubmitted;
    private long _significanceInitialized;
    private long _entityModelSourcesLinked;
    private long _batchesCommitted;
    private long _batchesFailed;
    private long _batchSequence;
    private TimeSpan _totalCommitTime;

    public NpgsqlIngestionPipeline(
        string connectionString,
        IReferenceDataReader referenceDataReader,
        ILogger<NpgsqlIngestionPipeline> logger)
    {
        // IncludeErrorDetail surfaces the offending row in CHECK / FK / NOT NULL
        // violations. Hashes are not sensitive content.
        NpgsqlConnectionStringBuilder csb = new(connectionString) { IncludeErrorDetail = true };
        NpgsqlDataSourceBuilder builder = new(csb.ConnectionString);
        _dataSource = builder.Build();
        _codeResolver = new CodeResolver(referenceDataReader);
        _logger = logger;
    }

    public PipelineStats Stats => new()
    {
        EntitiesSubmitted        = _entitiesSubmitted,
        EdgesSubmitted           = _edgesSubmitted,
        JunctionsSubmitted       = _junctionsSubmitted,
        PhysicalitiesSubmitted   = _physicalitiesSubmitted,
        SignificanceInitialized  = _significanceInitialized,
        EntityModelSourcesLinked = _entityModelSourcesLinked,
        BatchesCommitted         = _batchesCommitted,
        BatchesFailed            = _batchesFailed,
        TotalCommitTime          = _totalCommitTime,
    };

    public IIngestionBatch CreateBatch(string provenanceCode) => new IngestionBatch(provenanceCode);

    public IIngestionBatch CreateBatch() => new IngestionBatch("system_computed");

    public async Task SubmitBatchAsync(IIngestionBatch batch, CancellationToken ct)
    {
        if (batch is not IngestionBatch b)
        {
            throw new ArgumentException("Batch must be created by this pipeline.", nameof(batch));
        }

        long batchId = Interlocked.Increment(ref _batchSequence);
        Stopwatch sw = Stopwatch.StartNew();

        Log.BatchStarting(_logger, batchId,
            b.EntityCount, b.EdgeCount, b.Junctions.Count,
            b.Physicalities.Count, b.Sequences.Count,
            b.Significances.Count, b.EntityModelSources.Count);

        // Phase tracking so the catch knows where we were when PG died.
        string lastPhase = "<none>";
        try
        {
            await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
            await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct);

            lastPhase = "UpsertEntities";
            await RunPhaseAsync(batchId, lastPhase, b.Entities.Count,
                () => UpsertEntitiesAsync(batchId, conn, b, ct));

            lastPhase = "CreateEdges";
            await RunPhaseAsync(batchId, lastPhase, b.Edges.Count,
                () => CreateEdgesAsync(batchId, conn, b, ct));

            lastPhase = "PopulateJunctions";
            await RunPhaseAsync(batchId, lastPhase, b.Junctions.Count,
                () => PopulateJunctionsAsync(batchId, conn, b, ct));

            lastPhase = "CreatePhysicalities";
            await RunPhaseAsync(batchId, lastPhase, b.Physicalities.Count,
                () => CreatePhysicalitiesAsync(batchId, conn, b, ct));

            lastPhase = "PopulateSequences";
            await RunPhaseAsync(batchId, lastPhase, b.Sequences.Count,
                () => PopulateSequencesAsync(batchId, conn, b, ct));

            lastPhase = "InitializeEntitySignificance";
            await RunPhaseAsync(batchId, lastPhase, b.Significances.Count,
                () => InitializeEntitySignificanceAsync(batchId, conn, b, ct));

            lastPhase = "LinkEntityModelSources";
            await RunPhaseAsync(batchId, lastPhase, b.EntityModelSources.Count,
                () => LinkEntityModelSourcesAsync(batchId, conn, b, ct));

            lastPhase = "Commit";
            Stopwatch commitSw = Stopwatch.StartNew();
            await tx.CommitAsync(ct);
            Log.PhaseCompleted(_logger, batchId, lastPhase, 0, commitSw.Elapsed);

            Interlocked.Add(ref _entitiesSubmitted,        b.EntityCount);
            Interlocked.Add(ref _edgesSubmitted,           b.EdgeCount);
            Interlocked.Add(ref _junctionsSubmitted,       b.Junctions.Count);
            Interlocked.Add(ref _physicalitiesSubmitted,   b.Physicalities.Count);
            Interlocked.Add(ref _significanceInitialized,  b.Significances.Count);
            Interlocked.Add(ref _entityModelSourcesLinked, b.EntityModelSources.Count);
            Interlocked.Increment(ref _batchesCommitted);

            Log.BatchCommitted(_logger, batchId, b.EntityCount, b.EdgeCount, sw.Elapsed);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _batchesFailed);
            Log.BatchFailed(_logger, batchId, lastPhase,
                b.EntityCount, b.EdgeCount, b.Junctions.Count,
                b.Physicalities.Count, b.Sequences.Count,
                sw.Elapsed, ex);
            throw;
        }
        finally
        {
            _totalCommitTime += sw.Elapsed;
        }
    }

    private async Task RunPhaseAsync(long batchId, string phase, int rowCount, Func<Task> body)
    {
        Log.PhaseStarting(_logger, batchId, phase, rowCount);
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            await body();
            Log.PhaseCompleted(_logger, batchId, phase, rowCount, sw.Elapsed);
        }
        catch (IngestionStepException)
        {
            // Already wrapped at sub-step level; let it propagate untouched
            // so the BatchFailed log preserves the inner step context.
            throw;
        }
        catch (Exception ex)
        {
            Log.PhaseFailed(_logger, batchId, phase, rowCount, sw.Elapsed, ex);
            throw;
        }
    }

    /// <summary>
    /// Runs a substrate function call (or any other inline SQL) inside the
    /// current batch's transaction with full diagnostic context. On failure,
    /// wraps the exception with batch id, phase, sub-step, row count, and
    /// SQL text — so the C# log line answers "what was running when PG
    /// died" without grep'ing docker logs.
    /// </summary>
    private async Task RunSubStepAsync(
        long batchId,
        string phase,
        string subStep,
        int rowCount,
        string sql,
        NpgsqlConnection conn,
        CancellationToken ct,
        int? commandTimeoutSeconds = null)
    {
        Log.SubStepStarting(_logger, batchId, phase, subStep, rowCount);
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            await using NpgsqlCommand cmd = new(sql, conn);
            if (commandTimeoutSeconds.HasValue)
            {
                cmd.CommandTimeout = commandTimeoutSeconds.Value;
            }
            await cmd.ExecuteNonQueryAsync(ct);
            Log.SubStepCompleted(_logger, batchId, phase, subStep, rowCount, sw.Elapsed);
        }
        catch (Exception ex)
        {
            Log.SubStepFailed(_logger, batchId, phase, subStep, rowCount, sw.Elapsed, ex);
            throw new IngestionStepException(batchId, phase, subStep, rowCount, sql, ex);
        }
    }

    /// <summary>
    /// Same wrapping as RunSubStepAsync but for binary COPY operations.
    /// Records the COPY statement in the wrapped exception so failures
    /// during the binary-import stream still carry sub-step context.
    /// </summary>
    private async Task RunCopyAsync(
        long batchId,
        string phase,
        string subStep,
        int rowCount,
        string copySql,
        NpgsqlConnection conn,
        Func<NpgsqlBinaryImporter, Task> body,
        CancellationToken ct)
    {
        Log.SubStepStarting(_logger, batchId, phase, subStep, rowCount);
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            await using NpgsqlBinaryImporter writer = await conn.BeginBinaryImportAsync(copySql, ct);
            await body(writer);
            await writer.CompleteAsync(ct);
            Log.SubStepCompleted(_logger, batchId, phase, subStep, rowCount, sw.Elapsed);
        }
        catch (Exception ex)
        {
            Log.SubStepFailed(_logger, batchId, phase, subStep, rowCount, sw.Elapsed, ex);
            throw new IngestionStepException(batchId, phase, subStep, rowCount, copySql, ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    // ── UpsertEntitiesAsync ────────────────────────────────────────────────
    // Direct COPY into substrate.entity. ON CONFLICT (entity_type_id, hash) DO NOTHING.
    // No staging table, no resolve, no remap — the hash IS the FK.
    private async Task UpsertEntitiesAsync(long batchId, NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Entities.Count == 0)
        {
            return;
        }

        const string Phase = "UpsertEntities";

        // Pre-resolve type codes once.
        int[] typeIds = new int[batch.Entities.Count];
        for (int i = 0; i < batch.Entities.Count; i++)
        {
            typeIds[i] = await _codeResolver.EntityTypeIdAsync(batch.Entities[i].EntityTypeCode, ct);
        }

        await RunSubStepAsync(batchId, Phase, "create_staging_entity", 0,
            "CREATE TEMP TABLE staging_entity (entity_type_id INT NOT NULL, hash BYTEA NOT NULL) ON COMMIT DROP",
            conn, ct);

        await RunCopyAsync(batchId, Phase, "copy_staging_entity", batch.Entities.Count,
            "COPY staging_entity (entity_type_id, hash) FROM STDIN (FORMAT binary)",
            conn,
            async writer =>
            {
                for (int i = 0; i < batch.Entities.Count; i++)
                {
                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(typeIds[i], NpgsqlDbType.Integer, ct);
                    await writer.WriteAsync(batch.Entities[i].Hash, NpgsqlDbType.Bytea, ct);
                }
            },
            ct);

        // Drain staging into substrate.entity via the named substrate function
        // (AP-2: no inline INSERT SQL). The function loops over distinct
        // entity_type_ids and INSERTs one partition at a time.
        await RunSubStepAsync(batchId, Phase, "flush_entities_from_staging", batch.Entities.Count,
            "SELECT substrate.flush_entities_from_staging()", conn, ct,
            commandTimeoutSeconds: 1800);
    }

    // ── CreateEdgesAsync ───────────────────────────────────────────────────
    // Compute edge hash from (edge_type_id, ordered participant hashes).
    // COPY edge rows + edge_member rows with composite hash FKs.
    private async Task CreateEdgesAsync(long batchId, NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Edges.Count == 0)
        {
            return;
        }

        const string Phase = "CreateEdges";

        // Pre-resolve type/role/provenance ids and build edge hashes.
        int[] edgeTypeIds = new int[batch.Edges.Count];
        int[] provenanceIds = new int[batch.Edges.Count];
        byte[][] edgeHashes = new byte[batch.Edges.Count][];
        int[][] memberRoleIds = new int[batch.Edges.Count][];

        for (int i = 0; i < batch.Edges.Count; i++)
        {
            EdgeEntry edge = batch.Edges[i];
            edgeTypeIds[i]   = await _codeResolver.EdgeTypeIdAsync(edge.EdgeTypeCode, ct);
            provenanceIds[i] = await _codeResolver.ProvenanceIdAsync(edge.ProvenanceCode, ct);

            // Sort members by Position so edge hash is deterministic regardless
            // of insertion order in the decomposer.
            EdgeMemberSpec[] sorted = (EdgeMemberSpec[])edge.Members.Clone();
            Array.Sort(sorted, (a, b) => a.Position.CompareTo(b.Position));

            byte[][] orderedHashes = new byte[sorted.Length][];
            int[] roleIds = new int[sorted.Length];
            for (int j = 0; j < sorted.Length; j++)
            {
                orderedHashes[j] = sorted[j].Entity.Hash;
                roleIds[j] = await _codeResolver.EdgeRoleIdAsync(sorted[j].RoleCode, ct);
            }
            edgeHashes[i] = ComputeEdgeHash(edgeTypeIds[i], orderedHashes);
            memberRoleIds[i] = roleIds;
        }

        // ── COPY substrate.edge ──────────────────────────────────────────
        await RunSubStepAsync(batchId, Phase, "create_staging_edge", 0,
            "CREATE TEMP TABLE staging_edge (edge_type_id INT NOT NULL, hash BYTEA NOT NULL, provenance_id INT NOT NULL) ON COMMIT DROP",
            conn, ct);

        await RunCopyAsync(batchId, Phase, "copy_staging_edge", batch.Edges.Count,
            "COPY staging_edge (edge_type_id, hash, provenance_id) FROM STDIN (FORMAT binary)",
            conn,
            async writer =>
            {
                for (int i = 0; i < batch.Edges.Count; i++)
                {
                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(edgeTypeIds[i],   NpgsqlDbType.Integer, ct);
                    await writer.WriteAsync(edgeHashes[i],    NpgsqlDbType.Bytea,   ct);
                    await writer.WriteAsync(provenanceIds[i], NpgsqlDbType.Integer, ct);
                }
            },
            ct);

        await RunSubStepAsync(batchId, Phase, "flush_edges_from_staging", batch.Edges.Count,
            "SELECT substrate.flush_edges_from_staging()", conn, ct,
            commandTimeoutSeconds: 1800);

        // ── COPY substrate.edge_member ───────────────────────────────────
        int totalMembers = 0;
        for (int i = 0; i < batch.Edges.Count; i++)
        {
            totalMembers += batch.Edges[i].Members.Length;
        }
        if (totalMembers == 0)
        {
            return;
        }

        await RunSubStepAsync(batchId, Phase, "create_staging_edge_member", 0,
            "CREATE TEMP TABLE staging_edge_member (edge_type_id INT NOT NULL, edge_hash BYTEA NOT NULL, entity_type_id INT NOT NULL, entity_hash BYTEA NOT NULL, edge_role_id INT NOT NULL) ON COMMIT DROP",
            conn, ct);

        await RunCopyAsync(batchId, Phase, "copy_staging_edge_member", totalMembers,
            "COPY staging_edge_member (edge_type_id, edge_hash, entity_type_id, entity_hash, edge_role_id) FROM STDIN (FORMAT binary)",
            conn,
            async writer =>
            {
                for (int i = 0; i < batch.Edges.Count; i++)
                {
                    EdgeEntry edge = batch.Edges[i];
                    EdgeMemberSpec[] sorted = (EdgeMemberSpec[])edge.Members.Clone();
                    Array.Sort(sorted, (a, b) => a.Position.CompareTo(b.Position));
                    int[] roleIds = memberRoleIds[i];

                    for (int j = 0; j < sorted.Length; j++)
                    {
                        int entityTypeId = await _codeResolver.EntityTypeIdAsync(sorted[j].Entity.EntityTypeCode, ct);
                        await writer.StartRowAsync(ct);
                        await writer.WriteAsync(edgeTypeIds[i],     NpgsqlDbType.Integer, ct);
                        await writer.WriteAsync(edgeHashes[i],      NpgsqlDbType.Bytea,   ct);
                        await writer.WriteAsync(entityTypeId,       NpgsqlDbType.Integer, ct);
                        await writer.WriteAsync(sorted[j].Entity.Hash, NpgsqlDbType.Bytea, ct);
                        await writer.WriteAsync(roleIds[j],         NpgsqlDbType.Integer, ct);
                    }
                }
            },
            ct);

        await RunSubStepAsync(batchId, Phase, "flush_edge_members_from_staging", totalMembers,
            "SELECT substrate.flush_edge_members_from_staging()", conn, ct,
            commandTimeoutSeconds: 1800);

        // Prime substrate.edge_significance per arena (migration 0018 — the
        // pre-loop CROSS JOIN form crashed PG backends with SIGABRT/SIGSEGV).
        await RunSubStepAsync(batchId, Phase, "prime_edge_significance_for_staging", batch.Edges.Count,
            "SELECT substrate.prime_edge_significance_for_staging()", conn, ct,
            commandTimeoutSeconds: 1800);
    }

    // ── PopulateJunctionsAsync ─────────────────────────────────────────────
    // Group by table, COPY composite hash FK + reference id.
    private async Task PopulateJunctionsAsync(long batchId, NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Junctions.Count == 0)
        {
            return;
        }

        const string Phase = "PopulateJunctions";

        Dictionary<string, List<JunctionEntry>> grouped = new(StringComparer.Ordinal);
        foreach (JunctionEntry j in batch.Junctions)
        {
            if (!grouped.TryGetValue(j.JunctionTable, out List<JunctionEntry>? list))
            {
                list = [];
                grouped[j.JunctionTable] = list;
            }
            list.Add(j);
        }

        foreach ((string table, List<JunctionEntry> entries) in grouped)
        {
            if (!AllowedJunctionTables.Contains(table))
            {
                throw new ArgumentException($"Junction table not in allowlist: '{table}'", nameof(batch));
            }

            string refCol = GetJunctionRefColumn(table);
            bool hasMu = false;
            foreach (JunctionEntry e in entries)
            {
                if (e.Mu.HasValue) { hasMu = true; break; }
            }

            string stagingCols = hasMu
                ? "entity_type_id INT NOT NULL, entity_hash BYTEA NOT NULL, ref_id INT NOT NULL, mu FLOAT8"
                : "entity_type_id INT NOT NULL, entity_hash BYTEA NOT NULL, ref_id INT NOT NULL";

            string stagingTable = $"staging_{table}";

            await RunSubStepAsync(batchId, Phase, $"create_{stagingTable}", 0,
                $"CREATE TEMP TABLE {stagingTable} ({stagingCols}) ON COMMIT DROP",
                conn, ct);

            string copyCols = hasMu
                ? "entity_type_id, entity_hash, ref_id, mu"
                : "entity_type_id, entity_hash, ref_id";

            await RunCopyAsync(batchId, Phase, $"copy_{stagingTable}", entries.Count,
                $"COPY {stagingTable} ({copyCols}) FROM STDIN (FORMAT binary)",
                conn,
                async writer =>
                {
                    foreach (JunctionEntry e in entries)
                    {
                        int entityTypeId = await _codeResolver.EntityTypeIdAsync(e.Entity.EntityTypeCode, ct);
                        await writer.StartRowAsync(ct);
                        await writer.WriteAsync(entityTypeId,   NpgsqlDbType.Integer, ct);
                        await writer.WriteAsync(e.Entity.Hash,  NpgsqlDbType.Bytea,   ct);
                        await writer.WriteAsync(e.ReferenceId,  NpgsqlDbType.Integer, ct);
                        if (hasMu)
                        {
                            if (e.Mu.HasValue)
                            {
                                await writer.WriteAsync(e.Mu.Value, NpgsqlDbType.Double, ct);
                            }
                            else
                            {
                                await writer.WriteNullAsync(ct);
                            }
                        }
                    }
                },
                ct);

            string insertSql = hasMu
                ? $"INSERT INTO substrate.{table} (entity_type_id, entity_hash, {refCol}, mu) " +
                  $"SELECT DISTINCT entity_type_id, entity_hash, ref_id, COALESCE(mu, 1500.0) " +
                  $"FROM {stagingTable} ON CONFLICT DO NOTHING"
                : $"INSERT INTO substrate.{table} (entity_type_id, entity_hash, {refCol}) " +
                  $"SELECT DISTINCT entity_type_id, entity_hash, ref_id " +
                  $"FROM {stagingTable} ON CONFLICT DO NOTHING";

            await RunSubStepAsync(batchId, Phase, $"insert_{table}", entries.Count, insertSql, conn, ct,
                commandTimeoutSeconds: 1800);
        }
    }

    // ── CreatePhysicalitiesAsync ───────────────────────────────────────────
    // COPY (physicality_type_id, entity_type_id, entity_hash, content_hash, geom)
    // content_hash = BLAKE3(WKB) deduplicates within (type, entity).
    private async Task CreatePhysicalitiesAsync(long batchId, NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Physicalities.Count == 0)
        {
            return;
        }

        const string Phase = "CreatePhysicalities";

        await RunSubStepAsync(batchId, Phase, "create_staging_physicality", 0,
            "CREATE TEMP TABLE staging_physicality (physicality_type_id INT NOT NULL, entity_type_id INT NOT NULL, entity_hash BYTEA NOT NULL, content_hash BYTEA NOT NULL, wkb BYTEA NOT NULL) ON COMMIT DROP",
            conn, ct);

        await RunCopyAsync(batchId, Phase, "copy_staging_physicality", batch.Physicalities.Count,
            "COPY staging_physicality (physicality_type_id, entity_type_id, entity_hash, content_hash, wkb) FROM STDIN (FORMAT binary)",
            conn,
            async writer =>
            {
                foreach (PhysicalityEntry phys in batch.Physicalities)
                {
                    int physTypeId    = await _codeResolver.PhysicalityTypeIdAsync(phys.PhysicalityTypeCode, ct);
                    int entityTypeId  = await _codeResolver.EntityTypeIdAsync(phys.Entity.EntityTypeCode, ct);
                    byte[] contentHash = Blake3.Hash(phys.Wkb);

                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(physTypeId,        NpgsqlDbType.Integer, ct);
                    await writer.WriteAsync(entityTypeId,      NpgsqlDbType.Integer, ct);
                    await writer.WriteAsync(phys.Entity.Hash,  NpgsqlDbType.Bytea,   ct);
                    await writer.WriteAsync(contentHash,       NpgsqlDbType.Bytea,   ct);
                    await writer.WriteAsync(phys.Wkb,          NpgsqlDbType.Bytea,   ct);
                }
            },
            ct);

        await RunSubStepAsync(batchId, Phase, "flush_physicality_from_staging", batch.Physicalities.Count,
            "SELECT substrate.flush_physicality_from_staging()", conn, ct,
            commandTimeoutSeconds: 1800);
    }

    // ── PopulateSequencesAsync ─────────────────────────────────────────────
    // Drains batch.Sequences into substrate.sequence via the per-batch
    // staging_sequence TEMP table + named substrate function (AP-2).
    // (parent_type_id, parent_hash, ordinal) is unique by construction —
    // ON CONFLICT DO NOTHING keeps re-ingestion idempotent. RLE compression
    // is preserved verbatim from the producer; the staging row says
    // "this child fills positions ordinal..ordinal+rle_count-1 of this parent."
    private async Task PopulateSequencesAsync(long batchId, NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Sequences.Count == 0)
        {
            return;
        }

        const string Phase = "PopulateSequences";

        await RunSubStepAsync(batchId, Phase, "create_staging_sequence", 0,
            "CREATE TEMP TABLE staging_sequence (parent_entity_type_id INT NOT NULL, parent_entity_hash BYTEA NOT NULL, ordinal INT NOT NULL, child_entity_type_id INT NOT NULL, child_entity_hash BYTEA NOT NULL, rle_count INT NOT NULL DEFAULT 1) ON COMMIT DROP",
            conn, ct);

        await RunCopyAsync(batchId, Phase, "copy_staging_sequence", batch.Sequences.Count,
            "COPY staging_sequence (parent_entity_type_id, parent_entity_hash, ordinal, child_entity_type_id, child_entity_hash, rle_count) FROM STDIN (FORMAT binary)",
            conn,
            async writer =>
            {
                foreach (SequenceEntry s in batch.Sequences)
                {
                    int parentTypeId = await _codeResolver.EntityTypeIdAsync(s.Parent.EntityTypeCode, ct);
                    int childTypeId  = await _codeResolver.EntityTypeIdAsync(s.Child.EntityTypeCode, ct);

                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(parentTypeId,    NpgsqlDbType.Integer, ct);
                    await writer.WriteAsync(s.Parent.Hash,   NpgsqlDbType.Bytea,   ct);
                    await writer.WriteAsync(s.Ordinal,       NpgsqlDbType.Integer, ct);
                    await writer.WriteAsync(childTypeId,     NpgsqlDbType.Integer, ct);
                    await writer.WriteAsync(s.Child.Hash,    NpgsqlDbType.Bytea,   ct);
                    await writer.WriteAsync(s.RleCount,      NpgsqlDbType.Integer, ct);
                }
            },
            ct);

        await RunSubStepAsync(batchId, Phase, "flush_sequence_from_staging", batch.Sequences.Count,
            "SELECT substrate.flush_sequence_from_staging()", conn, ct,
            commandTimeoutSeconds: 1800);
    }

    // ── InitializeEntitySignificanceAsync ──────────────────────────────────
    // Writes only entity_significance rows. Edge significance is primed in
    // bulk by SignificanceFieldRunner against substrate.edge_significance —
    // not per-batch.
    private async Task InitializeEntitySignificanceAsync(long batchId, NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Significances.Count == 0)
        {
            return;
        }

        const string Phase = "InitializeEntitySignificance";

        await RunSubStepAsync(batchId, Phase, "create_staging_entity_significance", 0,
            "CREATE TEMP TABLE staging_entity_significance (context_type_id INT NOT NULL, entity_type_id INT NOT NULL, entity_hash BYTEA NOT NULL, mu FLOAT8 NOT NULL) ON COMMIT DROP",
            conn, ct);

        await RunCopyAsync(batchId, Phase, "copy_staging_entity_significance", batch.Significances.Count,
            "COPY staging_entity_significance (context_type_id, entity_type_id, entity_hash, mu) FROM STDIN (FORMAT binary)",
            conn,
            async writer =>
            {
                foreach (SignificanceEntry sig in batch.Significances)
                {
                    int contextId    = await _codeResolver.SignificanceContextIdAsync(sig.ContextTypeCode, ct);
                    int entityTypeId = await _codeResolver.EntityTypeIdAsync(sig.Entity.EntityTypeCode, ct);

                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(contextId,        NpgsqlDbType.Integer, ct);
                    await writer.WriteAsync(entityTypeId,     NpgsqlDbType.Integer, ct);
                    await writer.WriteAsync(sig.Entity.Hash,  NpgsqlDbType.Bytea,   ct);
                    await writer.WriteAsync(sig.InitialMu,    NpgsqlDbType.Double,  ct);
                }
            },
            ct);

        await RunSubStepAsync(batchId, Phase, "flush_entity_significance_from_staging", batch.Significances.Count,
            "SELECT substrate.flush_entity_significance_from_staging()", conn, ct,
            commandTimeoutSeconds: 1800);
    }

    // ── LinkEntityModelSourcesAsync ────────────────────────────────────────
    private async Task LinkEntityModelSourcesAsync(long batchId, NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.EntityModelSources.Count == 0)
        {
            return;
        }

        const string Phase = "LinkEntityModelSources";

        await RunSubStepAsync(batchId, Phase, "create_staging_entity_model_source", 0,
            "CREATE TEMP TABLE staging_entity_model_source (entity_type_id INT NOT NULL, entity_hash BYTEA NOT NULL, model_source_id INT NOT NULL) ON COMMIT DROP",
            conn, ct);

        await RunCopyAsync(batchId, Phase, "copy_staging_entity_model_source", batch.EntityModelSources.Count,
            "COPY staging_entity_model_source (entity_type_id, entity_hash, model_source_id) FROM STDIN (FORMAT binary)",
            conn,
            async writer =>
            {
                foreach (EntityModelSourceEntry e in batch.EntityModelSources)
                {
                    int entityTypeId = await _codeResolver.EntityTypeIdAsync(e.Entity.EntityTypeCode, ct);
                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(entityTypeId,       NpgsqlDbType.Integer, ct);
                    await writer.WriteAsync(e.Entity.Hash,      NpgsqlDbType.Bytea,   ct);
                    await writer.WriteAsync((int)e.ModelSourceId, NpgsqlDbType.Integer, ct);
                }
            },
            ct);

        await RunSubStepAsync(batchId, Phase, "insert_entity_model_source", batch.EntityModelSources.Count,
            "INSERT INTO substrate.entity_model_source (entity_type_id, entity_hash, model_source_id) " +
            "SELECT DISTINCT entity_type_id, entity_hash, model_source_id " +
            "FROM staging_entity_model_source ON CONFLICT DO NOTHING",
            conn, ct, commandTimeoutSeconds: 1800);
    }

    // ── PopulateEdgeTrajectoriesAsync ──────────────────────────────────────
    // Calls a substrate function (defined alongside the schema in a later
    // migration) that processes WHERE geom IS NULL in LIMIT-bounded chunks
    // until none remain. No BIGINT id-range arithmetic — there is no id.
    public async Task PopulateEdgeTrajectoriesAsync(CancellationToken ct)
    {
        const int ChunkSize = 5_000;

        long totalUpdated = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
            await using (NpgsqlCommand setCmd = new(
                "SET max_parallel_workers_per_gather = 0; " +
                "SET max_parallel_maintenance_workers = 0; " +
                "SET work_mem = '256MB';", conn))
            {
                await setCmd.ExecuteNonQueryAsync(ct);
            }

            await using NpgsqlCommand updateCmd = new(
                "SELECT substrate.populate_edge_trajectories($1)", conn);
            updateCmd.Parameters.Add(new NpgsqlParameter { Value = ChunkSize });
            updateCmd.CommandTimeout = 600;

            object? raw = await updateCmd.ExecuteScalarAsync(ct);
            long updated = raw is long u ? u : (raw is int i ? i : 0L);
            if (updated <= 0)
            {
                break;
            }
            totalUpdated += updated;
        }

        if (totalUpdated > 0)
        {
            Log.EdgeTrajectoriesPopulated(_logger, totalUpdated);
        }
    }

    // ── ComputeEdgeHash ────────────────────────────────────────────────────
    // BLAKE3 over [edge_type_id (4 LE bytes) | hash1 (32 bytes) | hash2 ...].
    // Stable identity for a typed n-ary edge: same participants in the same
    // role-ordered sequence under the same edge type produce the same hash.
    private static byte[] ComputeEdgeHash(int edgeTypeId, byte[][] orderedMemberHashes)
    {
        int len = 4;
        for (int i = 0; i < orderedMemberHashes.Length; i++)
        {
            len += orderedMemberHashes[i].Length;
        }
        byte[] buffer = new byte[len];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), edgeTypeId);
        int offset = 4;
        for (int i = 0; i < orderedMemberHashes.Length; i++)
        {
            orderedMemberHashes[i].CopyTo(buffer.AsSpan(offset));
            offset += orderedMemberHashes[i].Length;
        }
        return Blake3.Hash(buffer);
    }

    // ── Junction allowlist + ref column mapping ────────────────────────────
    // Junction allowlist. entity_sense was REMOVED — sense is captured by
    // has_sense edges (lemma → synset) and rated via substrate.edge_significance,
    // not via a parallel junction. entity_lexname ADDED — bounded-vocabulary
    // lexname classification (45 lexicographer files), polymorphic on entity_type.
    private static readonly HashSet<string> AllowedJunctionTables = new(StringComparer.Ordinal)
    {
        "entity_pos", "entity_lexname", "entity_language", "entity_morph_feature",
        "model_architecture_class", "tensor_tensor_role", "pattern_deprel",
    };

    private static string GetJunctionRefColumn(string table) => table switch
    {
        "entity_pos"               => "pos_id",
        "entity_lexname"           => "lexname_id",
        "entity_language"          => "language_id",
        "entity_morph_feature"     => "morph_feature_id",
        "model_architecture_class" => "architecture_class_id",
        "tensor_tensor_role"       => "tensor_role_id",
        "pattern_deprel"           => "deprel_id",
        _ => throw new ArgumentException($"Unknown junction table: '{table}'", nameof(table)),
    };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Pipeline batch #{BatchId} starting: entities={EntityCount} edges={EdgeCount} junctions={JunctionCount} physicalities={PhysicalityCount} sequences={SequenceCount} significances={SignificanceCount} model_sources={ModelSourceCount}")]
        public static partial void BatchStarting(ILogger logger,
            long batchId, int entityCount, int edgeCount, int junctionCount,
            int physicalityCount, int sequenceCount, int significanceCount, int modelSourceCount);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Pipeline batch #{BatchId} committed: entities={EntityCount} edges={EdgeCount} elapsed={Elapsed}")]
        public static partial void BatchCommitted(ILogger logger, long batchId, int entityCount, int edgeCount, TimeSpan elapsed);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Pipeline batch #{BatchId} FAILED at phase={LastPhase}: entities={EntityCount} edges={EdgeCount} junctions={JunctionCount} physicalities={PhysicalityCount} sequences={SequenceCount} elapsed={Elapsed}")]
        public static partial void BatchFailed(ILogger logger,
            long batchId, string lastPhase,
            int entityCount, int edgeCount, int junctionCount,
            int physicalityCount, int sequenceCount, TimeSpan elapsed, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Pipeline batch #{BatchId} phase={Phase} starting (rows={RowCount})")]
        public static partial void PhaseStarting(ILogger logger, long batchId, string phase, int rowCount);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Pipeline batch #{BatchId} phase={Phase} completed: rows={RowCount} elapsed={Elapsed}")]
        public static partial void PhaseCompleted(ILogger logger, long batchId, string phase, int rowCount, TimeSpan elapsed);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Pipeline batch #{BatchId} phase={Phase} FAILED: rows={RowCount} elapsed={Elapsed}")]
        public static partial void PhaseFailed(ILogger logger, long batchId, string phase, int rowCount, TimeSpan elapsed, Exception ex);

        [LoggerMessage(Level = LogLevel.Trace,
            Message = "Pipeline batch #{BatchId} {Phase}.{SubStep} starting (rows={RowCount})")]
        public static partial void SubStepStarting(ILogger logger, long batchId, string phase, string subStep, int rowCount);

        [LoggerMessage(Level = LogLevel.Trace,
            Message = "Pipeline batch #{BatchId} {Phase}.{SubStep} completed: rows={RowCount} elapsed={Elapsed}")]
        public static partial void SubStepCompleted(ILogger logger, long batchId, string phase, string subStep, int rowCount, TimeSpan elapsed);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Pipeline batch #{BatchId} {Phase}.{SubStep} FAILED: rows={RowCount} elapsed={Elapsed}")]
        public static partial void SubStepFailed(ILogger logger, long batchId, string phase, string subStep, int rowCount, TimeSpan elapsed, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Edge trajectories populated: {Count} edges updated")]
        public static partial void EdgeTrajectoriesPopulated(ILogger logger, long count);
    }
}
