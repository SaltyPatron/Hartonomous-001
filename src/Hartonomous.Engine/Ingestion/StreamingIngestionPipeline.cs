using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Common;
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
///   * <c>BackgroundSignificancePrimer</c> is GONE. Entity significance records
///     are emitted inline by producers. Edge significance is primed by the
///     phase-owned post-pass, cross-producted against every arena present at
///     execution time.
///   * Edge LINESTRINGZM geometry is built inline in C# when participant
///     centroids are present in the producer batch. Edges whose participant
///     physicality is not available inline are populated by the phase-owned
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
    /// Channel capacity per record kind. ~256K bounded → ~MB-scale per-channel
    /// memory ceiling regardless of record count. EmitAsync awaits when full.
    /// Was 65_536 — bumped to reduce producer backpressure on multi-million-row
    /// seed phases (WordNet, Wiktionary, UD, Tatoeba) where producers were
    /// blocking on the bounded channel more often than necessary.
    /// </summary>
    private const int ChannelCapacity = 262_144;

    /// <summary>
    /// Number of parallel drain workers per channel. Each worker owns its own
    /// long-lived NpgsqlConnection + its own pg_temp.X_inflight table (pg_temp
    /// is connection-local, so identical temp-table names don't collide). All
    /// workers for the same kind read from the same Channel&lt;T&gt; — bounded
    /// MPMC dispatch is what makes a single-channel/multi-reader pipeline fan
    /// out to N PG backends per kind. With 10 kinds and 4 workers that's 40
    /// drain backends; max_connections=100 in docker-compose leaves headroom
    /// for the producer connections, the bulk-existence-check pool, and the
    /// post-pass workers. Was 1 (single-reader) — that capped throughput per
    /// kind to one PG backend's COPY+INSERT-SELECT, ~2k rows/s on physicality
    /// and ~2k rows/s on entity. With 4× workers the bulk seed phases become
    /// CPU-bound on the host instead of single-backend-bound on PG.
    /// </summary>
    private const int DrainWorkersPerKind = 1;

    /// <summary>
    /// COPY chunk threshold. Each drain task COPY-loads up to this many rows
    /// into its temp table, then drains via INSERT-SELECT into substrate.
    /// Larger chunks amortize COPY overhead better; smaller chunks reduce
    /// crash blast radius. Was 4096 — bumped to 32_768 to amortize the
    /// per-chunk TRUNCATE + INSERT-SELECT cost 8× across rows on bulk seed
    /// phases that emit tens of millions of records.
    /// </summary>
    private const int CopyChunkRows = 32_768;

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
    private long _producerDedupHits;

    // Per-kind producer-wait tick counters. Incremented when a producer's
    // WriteAsync awaits because a bounded channel is full (backpressure).
    // Indexed by the same KindIndex enum the drain logger uses so end-of-phase
    // can correlate "channel X drained N rows in T elapsed; producers blocked
    // for W on this channel" — which directly answers "where is the slowness".
    private readonly long[] _producerWaitTicks = new long[KindIndex.Count];

    // Per-kind drain elapsed ticks. Drain task adds chunk elapsed here on each
    // commit so end-of-phase has total drain time per kind, not just per chunk.
    private readonly long[] _drainElapsedTicks = new long[KindIndex.Count];

    // Per-kind drain row counters; mirror the per-emit counters but counted
    // post-COPY-commit so end-of-phase numbers are "what landed in substrate"
    // rather than "what was emitted (and possibly dedupped before COPY)".
    private readonly long[] _drainRowsCommitted = new long[KindIndex.Count];

    // Per-kind producer-side row counters. Incremented after a record is
    // accepted by its bounded channel so phase orchestration can wait for all
    // records submitted so far without closing channels between phases.
    private readonly long[] _producerRowsSubmitted = new long[KindIndex.Count];

    private static class KindIndex
    {
        public const int Entity = 0;
        public const int EntityClassification = 1;
        public const int Edge = 2;
        public const int EdgeMember = 3;
        public const int Junction = 4;
        public const int Physicality = 5;
        public const int Sequence = 6;
        public const int EntitySignificance = 7;
        public const int EdgeSignificance = 8;
        public const int EntityModelSource = 9;
        public const int Count = 10;

        public static string Name(int idx) => idx switch
        {
            Entity => "entity",
            EntityClassification => "entity_classification",
            Edge => "edge",
            EdgeMember => "edge_member",
            Junction => "junction",
            Physicality => "physicality",
            Sequence => "sequence",
            EntitySignificance => "entity_significance",
            EdgeSignificance => "edge_significance",
            EntityModelSource => "entity_model_source",
            _ => $"kind_{idx}",
        };
    }

    /// <summary>
    /// Awaits a channel write, tracking elapsed ticks if the write blocked
    /// (channel full). Fast path returns immediately on completed-synchronously
    /// writes (the common case when drain keeps up).
    /// </summary>
    private async ValueTask WriteTrackedAsync<T>(
        System.Threading.Channels.ChannelWriter<T> writer,
        T item, int kindIndex, CancellationToken ct)
    {
        ValueTask vt = writer.WriteAsync(item, ct);
        if (vt.IsCompletedSuccessfully)
        {
            await vt.ConfigureAwait(false);
            Interlocked.Increment(ref _producerRowsSubmitted[kindIndex]);
            return;
        }
        long start = Stopwatch.GetTimestamp();
        await vt.ConfigureAwait(false);
        Interlocked.Add(ref _producerWaitTicks[kindIndex], Stopwatch.GetTimestamp() - start);
        Interlocked.Increment(ref _producerRowsSubmitted[kindIndex]);
    }

    /// <summary>
    /// Per-channel within-session dedup state. Producer drops emissions whose
    /// dedup key is already present so the bounded channel + temp staging
    /// never sees a duplicate twice. Memory cost: 32-byte struct per unique
    /// key, no boxing, no byte[] allocation per lookup. Cross-session
    /// duplicates still flow through and are caught by ON CONFLICT DO NOTHING
    /// at the substrate-side INSERT — dedup here is bandwidth optimization,
    /// not correctness.
    /// </summary>
    private const int DedupCapacityPerChannel = 1_048_576; // ~32 MB per channel cap

    private readonly ConcurrentDictionary<Hash32, byte> _entityDedup = new();
    private readonly ConcurrentDictionary<Hash32, byte> _edgeDedup = new();
    private readonly ConcurrentDictionary<Hash32, byte> _physicalityDedup = new();
    private readonly ConcurrentDictionary<Hash32, byte> _sequenceDedup = new();
    private readonly ConcurrentDictionary<Hash32, byte> _entitySignificanceDedup = new();
    private readonly ConcurrentDictionary<Hash32, byte> _edgeSignificanceDedup = new();

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
            SingleReader = (DrainWorkersPerKind == 1),
            SingleWriter = false,
        };

        _entities = Channel.CreateBounded<EntityRecord>(opts);
        _entityClassifications = Channel.CreateBounded<EntityClassificationRecord>(opts);
        _edges = Channel.CreateBounded<EdgeRecord>(opts);
        _edgeMembers = Channel.CreateBounded<EdgeMemberRecord>(opts);
        _junctions = Channel.CreateBounded<JunctionRecord>(opts);
        _physicalities = Channel.CreateBounded<PhysicalityRecord>(opts);
        _sequences = Channel.CreateBounded<SequenceRecord>(opts);
        _entitySignificances = Channel.CreateBounded<EntitySignificanceRecord>(opts);
        _edgeSignificances = Channel.CreateBounded<EdgeSignificanceRecord>(opts);
        _entityModelSources = Channel.CreateBounded<EntityModelSourceRecord>(opts);

        // N workers per channel. Each kind gets DrainWorkersPerKind parallel
        // drain backends. SingleReader=false on the bounded channels above
        // makes Channel<T> behave as MPMC; every TryRead call atomically
        // claims one record so workers don't race on duplicates.
        List<Task> drainTasks = new(DrainWorkersPerKind * 10);
        for (int w = 0; w < DrainWorkersPerKind; w++)
        {
            drainTasks.Add(Task.Run(() => DrainEntitiesAsync(_shutdown.Token)));
            drainTasks.Add(Task.Run(() => DrainEntityClassificationsAsync(_shutdown.Token)));
            drainTasks.Add(Task.Run(() => DrainEdgesAsync(_shutdown.Token)));
            drainTasks.Add(Task.Run(() => DrainEdgeMembersAsync(_shutdown.Token)));
            drainTasks.Add(Task.Run(() => DrainJunctionsAsync(_shutdown.Token)));
            drainTasks.Add(Task.Run(() => DrainPhysicalitiesAsync(_shutdown.Token)));
            drainTasks.Add(Task.Run(() => DrainSequencesAsync(_shutdown.Token)));
            drainTasks.Add(Task.Run(() => DrainEntitySignificancesAsync(_shutdown.Token)));
            drainTasks.Add(Task.Run(() => DrainEdgeSignificancesAsync(_shutdown.Token)));
            drainTasks.Add(Task.Run(() => DrainEntityModelSourcesAsync(_shutdown.Token)));
        }
        _drainTasks = drainTasks.ToArray();

        // Periodic mid-phase progress snapshot. Fires every PeriodicSnapshotInterval
        // with one line per active kind: rows so far, drain elapsed, producer-wait
        // elapsed. Lets the operator watch progress live and tell whether the
        // pipeline is making forward motion or stuck. Fires under the same
        // CancellationToken; stops on dispose.
        _periodicSnapshotTask = Task.Run(() => PeriodicSnapshotAsync(_shutdown.Token));
    }

    private static readonly TimeSpan PeriodicSnapshotInterval = TimeSpan.FromSeconds(10);
    private readonly Task _periodicSnapshotTask;
    private readonly Stopwatch _phaseClock = Stopwatch.StartNew();

    /// <summary>
    /// Complete every channel writer with the supplied exception. Called once
    /// from the first drain task that crashes so producer EmitAsync calls fail
    /// fast instead of blocking on a dead consumer. Idempotent: TryComplete is
    /// a no-op once a writer is already completed.
    /// </summary>
    private void FailAllWriters(Exception ex)
    {
        _entities.Writer.TryComplete(ex);
        _entityClassifications.Writer.TryComplete(ex);
        _edges.Writer.TryComplete(ex);
        _edgeMembers.Writer.TryComplete(ex);
        _junctions.Writer.TryComplete(ex);
        _physicalities.Writer.TryComplete(ex);
        _sequences.Writer.TryComplete(ex);
        _entitySignificances.Writer.TryComplete(ex);
        _edgeSignificances.Writer.TryComplete(ex);
        _entityModelSources.Writer.TryComplete(ex);
    }

    private async Task PeriodicSnapshotAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(PeriodicSnapshotInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }

                if (!_logger.IsEnabled(LogLevel.Information))
                {
                    continue;
                }

#pragma warning disable CA1873 // IsEnabled checked above; analyzer can't see across the loop.
                TimeSpan phaseElapsed = _phaseClock.Elapsed;
                for (int i = 0; i < KindIndex.Count; i++)
                {
                    long rows = Interlocked.Read(ref _drainRowsCommitted[i]);
                    long drainTicks = Interlocked.Read(ref _drainElapsedTicks[i]);
                    long waitTicks = Interlocked.Read(ref _producerWaitTicks[i]);
                    if (rows == 0 && drainTicks == 0 && waitTicks == 0)
                    {
                        continue;
                    }
                    TimeSpan drainElapsed = TimeSpan.FromSeconds((double)drainTicks / Stopwatch.Frequency);
                    TimeSpan waitElapsed = TimeSpan.FromSeconds((double)waitTicks / Stopwatch.Frequency);
                    double rowsPerSec = drainElapsed.TotalSeconds > 0
                        ? rows / drainElapsed.TotalSeconds : 0.0;
                    Log.LiveSnapshot(_logger, phaseElapsed, KindIndex.Name(i), rows,
                        drainElapsed, waitElapsed, rowsPerSec);
                }
#pragma warning restore CA1873
            }
        }
        catch (Exception ex)
        {
            // Snapshot loop must NEVER take down the pipeline. Log and exit.
            Log.SnapshotLoopCrashed(_logger, ex);
        }
    }

    public StreamingPipelineStats Stats => new()
    {
        EntitiesEmitted = _entitiesEmitted,
        EntityClassificationsEmitted = _entityClassificationsEmitted,
        EdgesEmitted = _edgesEmitted,
        EdgeMembersEmitted = _edgeMembersEmitted,
        JunctionsEmitted = _junctionsEmitted,
        PhysicalitiesEmitted = _physicalitiesEmitted,
        SequencesEmitted = _sequencesEmitted,
        EntitySignificancesEmitted = _entitySignificancesEmitted,
        EdgeSignificancesEmitted = _edgeSignificancesEmitted,
        EntityModelSourcesEmitted = _entityModelSourcesEmitted,
        CopyCommits = _copyCommits,
        CopyErrors = _copyErrors,
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

        // Build a within-batch centroid map from any POINTZM physicalities
        // emitted by the decomposer. When an edge's participants are all
        // atoms with POINTZM physicality (the common case for codepoint /
        // word_form / lemma edges), we can attach the LINESTRINGZM EWKB
        // inline so the drain INSERT writes geom directly. Edges whose
        // participants don't have POINTZM here (compositions whose physicality
        // is LINESTRINGZM, or participants from prior batches) leave geom
        // NULL for PopulateEdgeTrajectoriesAsync.
        Dictionary<Hash32, (double X, double Y, double Z, double M)>? centroidMap = null;
        foreach (PhysicalityEntry p in b.Physicalities)
        {
            // POINTZM EWKB layout: byte_order(1) + type(4) + 4*float8(32) = 37 bytes.
            // Type word: 0xC0000001 (PostGIS EWKB POINT|Z|M) or 3001 (ISO).
            if (p.Wkb.Length != 37)
            {
                continue;
            }
            if (p.Wkb[0] != 0x01)
            {
                continue; // require little-endian
            }
            uint typeWord = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Wkb.AsSpan(1, 4));
            bool isPointZM = (typeWord == 0xC0000001u) || (typeWord == 3001u);
            if (!isPointZM)
            {
                continue;
            }
            double x = System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(p.Wkb.AsSpan(5, 8));
            double y = System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(p.Wkb.AsSpan(13, 8));
            double z = System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(p.Wkb.AsSpan(21, 8));
            double m = System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(p.Wkb.AsSpan(29, 8));
            centroidMap ??= new Dictionary<Hash32, (double, double, double, double)>();
            centroidMap[new Hash32(p.Entity.Hash)] = (x, y, z, m);
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

            // Try to build inline LINESTRINGZM EWKB if every participant has
            // a POINTZM centroid in the batch. Otherwise leave geom NULL for
            // the post-pass populate.
            byte[]? inlineGeomWkb = null;
            if (centroidMap is not null && sorted.Length >= 2)
            {
                (double X, double Y, double Z, double M)[] verts =
                    new (double, double, double, double)[sorted.Length];
                bool allPresent = true;
                for (int j = 0; j < sorted.Length; j++)
                {
                    if (!centroidMap.TryGetValue(new Hash32(sorted[j].Entity.Hash), out var c))
                    {
                        allPresent = false;
                        break;
                    }
                    verts[j] = c;
                }
                if (allPresent)
                {
                    inlineGeomWkb = PostGisWkbBuilder.LineStringZM(verts.AsSpan());
                }
            }

            await EmitAsync(new EdgeRecord(edge.EdgeTypeCode, edgeHash, edge.ProvenanceCode, inlineGeomWkb), ct).ConfigureAwait(false);
            for (int j = 0; j < sorted.Length; j++)
            {
                await EmitAsync(new EdgeMemberRecord(
                    edge.EdgeTypeCode, edgeHash,
                    sorted[j].Entity.Hash,
                    sorted[j].RoleCode,
                    sorted[j].Position), ct).ConfigureAwait(false);
            }

            // Inline edge significance: one row per (edge × arena). AP-1: cross-product
            // against every arena currently in significance_context. Uses the batch
            // provenance's initial_mu as the trust seed rather than the default 1500 that
            // PrimeAllSignificanceAsync would insert. This means hot paths (WordNet, UD,
            // Wiktionary) start with calibrated Glicko-2 priors rather than equal weights.
            //
            // Producer-supplied per-arena overrides (EdgeEntry.SignificanceOverrides)
            // win for the arenas they cover. FfnEdgeDecompositionPass uses this to
            // ship a per-edge mu derived from the signed weight scaled by the tensor's
            // mean magnitude in the model_trust arena — so Glicko-2-rated A* traversal
            // sees the model's learned function as cost gradients, not uniform-cost BFS.
            double provenanceMu = await _codeResolver.ProvenanceMuAsync(edge.ProvenanceCode, ct).ConfigureAwait(false);
            IReadOnlyList<string> arenas = await _codeResolver.AllSignificanceContextCodesAsync(ct).ConfigureAwait(false);
            EdgeSignificanceSpec[] overrides = edge.SignificanceOverrides;
            // Auto-prime lands as provenance_authority_corroboration
            // (the substrate's record that THIS provenance asserts this edge
            // with this initial mu). Other attestation kinds — corpus
            // co-occurrence, model attention, inference outcomes — are
            // emitted separately under their own attestation_type codes.
            const string AutoPrimeAttestation = "provenance_authority_corroboration";
            foreach (string arenaCode in arenas)
            {
                double mu = provenanceMu;
                string attestation = AutoPrimeAttestation;
                for (int k = 0; k < overrides.Length; k++)
                {
                    if (string.Equals(overrides[k].ContextTypeCode, arenaCode, StringComparison.Ordinal))
                    {
                        mu = overrides[k].InitialMu;
                        attestation = string.IsNullOrEmpty(overrides[k].AttestationTypeCode)
                            ? AutoPrimeAttestation
                            : overrides[k].AttestationTypeCode;
                        break;
                    }
                }
                await EmitAsync(new EdgeSignificanceRecord(
                    arenaCode, attestation, edge.EdgeTypeCode, edgeHash, mu), ct).ConfigureAwait(false);
            }
        }

        foreach (JunctionEntry j in b.Junctions)
        {
            await EmitAsync(new JunctionRecord(
                j.JunctionTable, j.Entity.Hash,
                j.ReferenceId,
                j.AttestationTypeCode ?? "lexical_curated_relation",
                j.Mu), ct).ConfigureAwait(false);
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
                sig.AttestationTypeCode ?? "provenance_authority_corroboration",
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

    public async Task DrainPendingAsync(CancellationToken ct)
    {
        long[] targetRows = new long[KindIndex.Count];
        for (int i = 0; i < KindIndex.Count; i++)
        {
            targetRows[i] = Interlocked.Read(ref _producerRowsSubmitted[i]);
        }

        while (true)
        {
            for (int i = 0; i < _drainTasks.Length; i++)
            {
                if (_drainTasks[i].IsFaulted)
                {
                    await Task.WhenAll(_drainTasks).ConfigureAwait(false);
                }
            }

            bool allDrained = true;
            for (int i = 0; i < KindIndex.Count; i++)
            {
                if (Interlocked.Read(ref _drainRowsCommitted[i]) < targetRows[i])
                {
                    allDrained = false;
                    break;
                }
            }

            if (allDrained)
            {
                return;
            }

            await Task.Delay(IdleFlushAfter, ct).ConfigureAwait(false);
        }
    }

    // ── Substrate-aware ingestion: bulk existence checks ──────────────────
    //
    // Decomposers compute candidate PKs locally (UCD/UCA/ISO blobs + BLAKE3,
    // zero DB calls), then call these methods ONCE per kind per chunk. The
    // substrate's content-addressed identity model means a btree probe over
    // bytea(32) hashes answers a million-element ANY-array in well under a
    // second on production hardware. The decomposer subtracts the returned
    // existing-PK set from candidates and emits ONLY the diff — eliminating
    // the 30:1+ redundant-emission ratios that the conversation surfaced.
    //
    // ON CONFLICT DO NOTHING in the drain INSERT-SELECT remains as
    // belt-and-suspenders for the cross-session race window (decomposer A
    // and decomposer B both compute the same candidate concurrently, both
    // ask the substrate, both get "missing", both emit) but should fire
    // near-zero times per phase under steady-state ingestion.

    public async Task<HashSet<HashKey>> GetExistingEntityHashesAsync(
        IReadOnlyCollection<byte[]> hashes, CancellationToken ct)
    {
        HashSet<HashKey> existing = new(hashes.Count);
        if (hashes.Count == 0)
        {
            return existing;
        }

        byte[][] arr = new byte[hashes.Count][];
        int i = 0;
        foreach (byte[] h in hashes)
        {
            arr[i++] = h;
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(IngestionSql.GetExistingEntityHashes, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = arr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            existing.Add(new HashKey((byte[])r[0]));
        }
        return existing;
    }

    public async Task<HashSet<EntityClassificationKey>> GetExistingEntityClassificationsAsync(
        IReadOnlyCollection<EntityClassificationKey> tuples, CancellationToken ct)
    {
        HashSet<EntityClassificationKey> existing = new(tuples.Count);
        if (tuples.Count == 0)
        {
            return existing;
        }

        int n = tuples.Count;
        byte[][] hashArr = new byte[n][];
        int[] etArr = new int[n];
        int[] pArr  = new int[n];
        int i = 0;
        foreach (EntityClassificationKey k in tuples)
        {
            hashArr[i] = k.EntityHash;
            etArr[i]   = await _codeResolver.EntityTypeIdAsync(k.EntityTypeCode, ct).ConfigureAwait(false);
            pArr[i]    = await _codeResolver.ProvenanceIdAsync(k.ProvenanceCode, ct).ConfigureAwait(false);
            i++;
        }

        // Reverse maps: id → original code so the returned set carries the
        // codes the decomposer originally passed in. The reference vocabularies
        // are bounded (54 entity types, 10 provenances) so HashSet membership
        // is O(1) and the build is negligible.
        Dictionary<int, string> etByIdInvolved = new();
        Dictionary<int, string> pByIdInvolved  = new();
        foreach (EntityClassificationKey k in tuples)
        {
            int etid = await _codeResolver.EntityTypeIdAsync(k.EntityTypeCode, ct).ConfigureAwait(false);
            int pid  = await _codeResolver.ProvenanceIdAsync(k.ProvenanceCode, ct).ConfigureAwait(false);
            etByIdInvolved[etid] = k.EntityTypeCode;
            pByIdInvolved[pid]   = k.ProvenanceCode;
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(IngestionSql.GetExistingEntityClassifications, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = hashArr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        cmd.Parameters.Add(new NpgsqlParameter { Value = etArr,   NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = pArr,    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            byte[] h = (byte[])r[0];
            string etCode = (string)r[1];
            string pCode  = (string)r[2];
            existing.Add(new EntityClassificationKey(h, etCode, pCode));
        }
        return existing;
    }

    public async Task<HashSet<EdgeKey>> GetExistingEdgesAsync(
        IReadOnlyCollection<EdgeKey> tuples, CancellationToken ct)
    {
        HashSet<EdgeKey> existing = new(tuples.Count);
        if (tuples.Count == 0)
        {
            return existing;
        }

        int n = tuples.Count;
        int[] etArr = new int[n];
        byte[][] hashArr = new byte[n][];
        int i = 0;
        foreach (EdgeKey k in tuples)
        {
            etArr[i]   = await _codeResolver.EdgeTypeIdAsync(k.EdgeTypeCode, ct).ConfigureAwait(false);
            hashArr[i] = k.EdgeHash;
            i++;
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(IngestionSql.GetExistingEdges, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = etArr,   NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = hashArr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string etCode = (string)r[0];
            byte[] h      = (byte[])r[1];
            existing.Add(new EdgeKey(etCode, h));
        }
        return existing;
    }

    public async Task<HashSet<PhysicalityKey>> GetExistingPhysicalitiesAsync(
        IReadOnlyCollection<PhysicalityKey> tuples, CancellationToken ct)
    {
        HashSet<PhysicalityKey> existing = new(tuples.Count);
        if (tuples.Count == 0)
        {
            return existing;
        }

        int n = tuples.Count;
        int[] ptArr = new int[n];
        byte[][] ehArr = new byte[n][];
        byte[][] chArr = new byte[n][];
        int i = 0;
        foreach (PhysicalityKey k in tuples)
        {
            ptArr[i] = await _codeResolver.PhysicalityTypeIdAsync(k.PhysicalityTypeCode, ct).ConfigureAwait(false);
            ehArr[i] = k.EntityHash;
            chArr[i] = k.ContentHash;
            i++;
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(IngestionSql.GetExistingPhysicalities, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = ptArr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = ehArr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        cmd.Parameters.Add(new NpgsqlParameter { Value = chArr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string ptCode = (string)r[0];
            byte[] eh     = (byte[])r[1];
            byte[] ch     = (byte[])r[2];
            existing.Add(new PhysicalityKey(ptCode, eh, ch));
        }
        return existing;
    }

    public async Task<HashSet<SequenceKey>> GetExistingSequenceRowsAsync(
        IReadOnlyCollection<SequenceKey> tuples, CancellationToken ct)
    {
        HashSet<SequenceKey> existing = new(tuples.Count);
        if (tuples.Count == 0)
        {
            return existing;
        }

        int n = tuples.Count;
        byte[][] phArr = new byte[n][];
        int[] ordArr = new int[n];
        int i = 0;
        foreach (SequenceKey k in tuples)
        {
            phArr[i]  = k.ParentHash;
            ordArr[i] = k.Ordinal;
            i++;
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(IngestionSql.GetExistingSequenceRows, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = phArr,  NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        cmd.Parameters.Add(new NpgsqlParameter { Value = ordArr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            byte[] ph = (byte[])r[0];
            int ord   = (int)r[1];
            existing.Add(new SequenceKey(ph, ord));
        }
        return existing;
    }

    public async Task PopulateEdgeTrajectoriesAsync(CancellationToken ct)
    {
        // Populate geom on edges where the producer didn't (or couldn't)
        // attach an inline LINESTRINGZM EWKB. Per-chunk connection so a PG
        // restart between chunks doesn't poison the whole post-pass; small
        // chunk + parallel-disabled session so the per-call working set fits
        // comfortably under work_mem and never spawns parallel workers each
        // grabbing their own work_mem (the historical OOM-kill path on a
        // 7M-edge trajectory population pass against the 64GB-host WSL2 PG container).
        const int chunkSize = 4_096;
        const int maxRetries = 5;
        long totalUpdated = 0;
        int chunksProcessed = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long updated = 0;
            Exception? lastEx = null;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    await using NpgsqlConnection conn =
                        await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
                    await using (NpgsqlCommand setCmd = new(IngestionSql.PostPassSessionSettings, conn))
                    {
                        await setCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                    await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
                        conn,
                        SubstrateFunctionNames.PopulateEdgeTrajectories,
                        [new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = chunkSize }]);
                    cmd.CommandTimeout = 0;
                    object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    updated = result is long l ? l : (long?)result ?? 0L;
                    lastEx = null;
                    break;
                }
                catch (Exception ex) when (
                    ex is NpgsqlException ||
                    ex is System.IO.IOException ||
                    ex is System.Net.Sockets.SocketException ||
                    (ex.InnerException is System.Net.Sockets.SocketException))
                {
                    lastEx = ex;
                    int delayMs = 1000 * (1 << attempt);
                    Log.PostPassRetry(_logger, "populate_edge_trajectories", attempt + 1, delayMs, ex);
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
            }
            if (lastEx is not null)
            {
                Log.PostPassGivingUp(_logger, "populate_edge_trajectories", maxRetries, totalUpdated, lastEx);
                throw new InvalidOperationException(
                    "populate_edge_trajectories failed after retry exhaustion; phase cannot complete with missing edge geometry.",
                    lastEx);
            }
            totalUpdated += updated;
            chunksProcessed++;
            if (chunksProcessed % 50 == 0 || updated == 0)
            {
                Log.PostPassProgress(_logger, "populate_edge_trajectories", chunksProcessed, totalUpdated);
            }
            if (updated == 0)
            {
                break;
            }
        }
        Log.EdgeTrajectoriesPopulated(_logger, totalUpdated);

        await using NpgsqlConnection verifyConn =
            await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand verifyCmd = NpgsqlSubstrateCommand.CreateFunction(
            verifyConn,
            SubstrateFunctionNames.CountMissingEdgeTrajectories,
            []);
        verifyCmd.CommandTimeout = 0;
        object? missingResult = await verifyCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        long missing = missingResult is long missingLong ? missingLong : (long?)missingResult ?? 0L;
        if (missing > 0)
        {
            throw new InvalidOperationException(
                $"populate_edge_trajectories left {missing} edges without geometry; phase cannot complete with missing edge trajectories.");
        }
    }

    PipelineStats IIngestionPipeline.Stats => new()
    {
        EntitiesSubmitted = _entitiesEmitted,
        EdgesSubmitted = _edgesEmitted,
        JunctionsSubmitted = _junctionsEmitted,
        PhysicalitiesSubmitted = _physicalitiesEmitted,
        SignificanceInitialized = _entitySignificancesEmitted,
        EntityModelSourcesLinked = _entityModelSourcesEmitted,
        BatchesCommitted = _copyCommits,
        BatchesFailed = _copyErrors,
        TotalCommitTime = TimeSpan.Zero,
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
            EntityRecord r => EmitEntityWithClassificationAsync(r, ct),
            EntityClassificationRecord r => WriteTrackedAsync(_entityClassifications.Writer, r, KindIndex.EntityClassification, ct),
            EdgeRecord r => EmitEdgeAsync(r, ct),
            EdgeMemberRecord r => WriteTrackedAsync(_edgeMembers.Writer, r, KindIndex.EdgeMember, ct),
            JunctionRecord r => WriteTrackedAsync(_junctions.Writer, r, KindIndex.Junction, ct),
            PhysicalityRecord r => EmitPhysicalityAsync(r, ct),
            SequenceRecord r => EmitSequenceAsync(r, ct),
            EntitySignificanceRecord r => EmitEntitySignificanceAsync(r, ct),
            EdgeSignificanceRecord r => EmitEdgeSignificanceAsync(r, ct),
            EntityModelSourceRecord r => WriteTrackedAsync(_entityModelSources.Writer, r, KindIndex.EntityModelSource, ct),
            _ => throw new ArgumentException(
                $"Unknown IngestionRecord subtype: {record.GetType().Name}", nameof(record)),
        };
    }

    // EntityRecord fans into two channels: hash-only into substrate.entity AND
    // (hash, type, provenance) into substrate.entity_classification. Phase C
    // unification: content identity vs decomposer-asserted classification.
    // Dedup: substrate.entity is content-only PK on hash, so two emissions of
    // the same content collapse — drop the second before COPY. Classification
    // PK is (entity_hash, entity_type_id, provenance_id) so a different
    // classification on the same content goes through.
    private async ValueTask EmitEntityWithClassificationAsync(EntityRecord r, CancellationToken ct)
    {
        Hash32 key = new(r.Hash);
        if (TryAddDedup(_entityDedup, key))
        {
            await WriteTrackedAsync(_entities.Writer, r, KindIndex.Entity, ct).ConfigureAwait(false);
        }
        // Classification always goes through — substrate.entity_classification
        // ON CONFLICT handles cross-session dupes; within-session a decomposer
        // emitting the same (entity, type, provenance) twice is harmless.
        await WriteTrackedAsync(_entityClassifications.Writer,
            new EntityClassificationRecord(r.Hash, r.EntityTypeCode, r.ProvenanceCode),
            KindIndex.EntityClassification, ct).ConfigureAwait(false);
    }

    private async ValueTask EmitEdgeAsync(EdgeRecord r, CancellationToken ct)
    {
        // Dedup key includes edge type because (edge_type_id, hash) is the PK.
        Hash32 key = ComposeKey(r.EdgeTypeCode, r.EdgeHash);
        if (TryAddDedup(_edgeDedup, key))
        {
            await WriteTrackedAsync(_edges.Writer, r, KindIndex.Edge, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask EmitPhysicalityAsync(PhysicalityRecord r, CancellationToken ct)
    {
        // (physicality_type_id, entity_hash, content_hash) is the PK.
        Hash32 key = ComposeKey(r.PhysicalityTypeCode, r.EntityHash, r.ContentHash);
        if (TryAddDedup(_physicalityDedup, key))
        {
            await WriteTrackedAsync(_physicalities.Writer, r, KindIndex.Physicality, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask EmitSequenceAsync(SequenceRecord r, CancellationToken ct)
    {
        // (parent_hash, ordinal) is the PK; child_hash and rle_count not in PK.
        Hash32 key = ComposeKey(r.ParentEntityHash, r.Ordinal);
        if (TryAddDedup(_sequenceDedup, key))
        {
            await WriteTrackedAsync(_sequences.Writer, r, KindIndex.Sequence, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask EmitEntitySignificanceAsync(EntitySignificanceRecord r, CancellationToken ct)
    {
        // Dedup key includes attestation_type — same (arena, entity) under
        // different attestation_types is intentionally distinct rating data,
        // not a duplicate. Collapsing them would conflate evidence kinds.
        Hash32 key = ComposeKey(r.ContextTypeCode, r.AttestationTypeCode, r.EntityHash);
        if (TryAddDedup(_entitySignificanceDedup, key))
        {
            await WriteTrackedAsync(_entitySignificances.Writer, r, KindIndex.EntitySignificance, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask EmitEdgeSignificanceAsync(EdgeSignificanceRecord r, CancellationToken ct)
    {
        // Dedup key includes attestation_type for the same reason as the
        // entity-side: corpus_co_occurrence_window vs lexical_curated_relation
        // vs model_attention_pattern attestations on the same edge are
        // distinct rating rows, not duplicates.
        Hash32 key = ComposeKey(r.ContextTypeCode, r.AttestationTypeCode, r.EdgeTypeCode, r.EdgeHash);
        if (TryAddDedup(_edgeSignificanceDedup, key))
        {
            await WriteTrackedAsync(_edgeSignificances.Writer, r, KindIndex.EdgeSignificance, ct).ConfigureAwait(false);
        }
    }

    private static Hash32 ComposeKey(string codeA, string codeB, string codeC, byte[] hash)
    {
        byte[] aBytes = System.Text.Encoding.UTF8.GetBytes(codeA);
        byte[] bBytes = System.Text.Encoding.UTF8.GetBytes(codeB);
        byte[] cBytes = System.Text.Encoding.UTF8.GetBytes(codeC);
        byte[] buf = new byte[aBytes.Length + 1 + bBytes.Length + 1 + cBytes.Length + 1 + hash.Length];
        int o = 0;
        Buffer.BlockCopy(aBytes, 0, buf, o, aBytes.Length); o += aBytes.Length;
        buf[o++] = 0x1F;
        Buffer.BlockCopy(bBytes, 0, buf, o, bBytes.Length); o += bBytes.Length;
        buf[o++] = 0x1F;
        Buffer.BlockCopy(cBytes, 0, buf, o, cBytes.Length); o += cBytes.Length;
        buf[o++] = 0x1F;
        Buffer.BlockCopy(hash, 0, buf, o, hash.Length);
        return new Hash32(Hartonomous.Core.Compute.Common.Blake3.Hash(buf));
    }

    /// <summary>
    /// Try to record a dedup key. Returns true if the key was added (i.e.,
    /// the caller should proceed to emit); false if already present (drop).
    /// When the dictionary exceeds <see cref="DedupCapacityPerChannel"/> the
    /// dedup state is cleared — false negatives become possible afterward
    /// (re-emission of an earlier hash) but ON CONFLICT DO NOTHING in the
    /// drain INSERT-SELECT catches them. Bounded memory by design.
    /// </summary>
    private bool TryAddDedup(ConcurrentDictionary<Hash32, byte> dedup, Hash32 key)
    {
        if (dedup.Count >= DedupCapacityPerChannel)
        {
            // Best-effort reset; another thread may add concurrently — fine.
            dedup.Clear();
        }
        if (dedup.TryAdd(key, 0))
        {
            return true;
        }
        Interlocked.Increment(ref _producerDedupHits);
        return false;
    }

    /// <summary>
    /// Mix a string code and a 32-byte hash into a Hash32 dedup key. Uses
    /// BLAKE3 over the concatenation. Used for composite PKs whose key
    /// includes a reference-table code that the producer hasn't yet
    /// resolved to an int (resolution happens in the drain task at write
    /// time). Stable across calls because BLAKE3 is deterministic.
    /// </summary>
    private static Hash32 ComposeKey(string code, byte[] hash)
    {
        byte[] codeBytes = System.Text.Encoding.UTF8.GetBytes(code);
        byte[] buf = new byte[codeBytes.Length + 1 + hash.Length];
        Buffer.BlockCopy(codeBytes, 0, buf, 0, codeBytes.Length);
        buf[codeBytes.Length] = 0x1F; // unit separator
        Buffer.BlockCopy(hash, 0, buf, codeBytes.Length + 1, hash.Length);
        return new Hash32(Hartonomous.Core.Compute.Common.Blake3.Hash(buf));
    }

    private static Hash32 ComposeKey(string codeA, byte[] hashA, byte[] hashB)
    {
        byte[] codeBytes = System.Text.Encoding.UTF8.GetBytes(codeA);
        byte[] buf = new byte[codeBytes.Length + 1 + hashA.Length + 1 + hashB.Length];
        int o = 0;
        Buffer.BlockCopy(codeBytes, 0, buf, o, codeBytes.Length); o += codeBytes.Length;
        buf[o++] = 0x1F;
        Buffer.BlockCopy(hashA, 0, buf, o, hashA.Length); o += hashA.Length;
        buf[o++] = 0x1F;
        Buffer.BlockCopy(hashB, 0, buf, o, hashB.Length);
        return new Hash32(Hartonomous.Core.Compute.Common.Blake3.Hash(buf));
    }

    private static Hash32 ComposeKey(string codeA, string codeB, byte[] hash)
    {
        byte[] aBytes = System.Text.Encoding.UTF8.GetBytes(codeA);
        byte[] bBytes = System.Text.Encoding.UTF8.GetBytes(codeB);
        byte[] buf = new byte[aBytes.Length + 1 + bBytes.Length + 1 + hash.Length];
        int o = 0;
        Buffer.BlockCopy(aBytes, 0, buf, o, aBytes.Length); o += aBytes.Length;
        buf[o++] = 0x1F;
        Buffer.BlockCopy(bBytes, 0, buf, o, bBytes.Length); o += bBytes.Length;
        buf[o++] = 0x1F;
        Buffer.BlockCopy(hash, 0, buf, o, hash.Length);
        return new Hash32(Hartonomous.Core.Compute.Common.Blake3.Hash(buf));
    }

    private static Hash32 ComposeKey(byte[] hash, int ordinal)
    {
        byte[] buf = new byte[hash.Length + 4];
        Buffer.BlockCopy(hash, 0, buf, 0, hash.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(hash.Length, 4), ordinal);
        return new Hash32(Hartonomous.Core.Compute.Common.Blake3.Hash(buf));
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

        // Per-kind drain + producer-wait summary. Surfaces channel-by-channel:
        //   rows         — count COPYed + INSERT-ON-CONFLICT-committed to substrate
        //   drain        — total wall time spent in TRUNCATE+COPY+INSERT for this kind
        //   producerWait — total wall time producers spent blocked because the
        //                  bounded channel was full (backpressure from this kind's drain
        //                  not keeping up). Zero = no backpressure on this channel.
        // Read together: high producerWait + high drain on the same kind = drain
        // is the bottleneck. High producerWait + low drain = producers are bursty
        // and the channel isn't sized for the burst. Low producerWait, high drain
        // = drain is slow but consumers haven't filled it (low producer rate).
        if (_logger.IsEnabled(LogLevel.Information))
        {
#pragma warning disable CA1873 // IsEnabled is checked above; analyzer can't see across the loop.
            for (int i = 0; i < KindIndex.Count; i++)
            {
                long rows = Interlocked.Read(ref _drainRowsCommitted[i]);
                long drainTicks = Interlocked.Read(ref _drainElapsedTicks[i]);
                long waitTicks = Interlocked.Read(ref _producerWaitTicks[i]);
                if (rows == 0 && drainTicks == 0 && waitTicks == 0)
                {
                    continue; // skip silent kinds for terseness
                }
                TimeSpan drainElapsed = TimeSpan.FromSeconds((double)drainTicks / Stopwatch.Frequency);
                TimeSpan waitElapsed = TimeSpan.FromSeconds((double)waitTicks / Stopwatch.Frequency);
                double rowsPerSec = drainElapsed.TotalSeconds > 0
                    ? rows / drainElapsed.TotalSeconds : 0.0;
                Log.KindSummary(_logger, KindIndex.Name(i), rows,
                    drainElapsed, waitElapsed, rowsPerSec);
            }
#pragma warning restore CA1873
        }

        // FlushAsync drains channels only. Post-phase enrichment (sequence
        // physicality, edge trajectory population, and significance priming) is the phase orchestrator's
        // responsibility and must be called explicitly via
        // PopulateSequencePhysicalityAsync, PopulateEdgeTrajectoriesAsync,
        // and PrimeAllSignificanceAsync.
        // Keeping them here would execute them twice — once per phase by the
        // orchestrator and again at dispose — wasting time on already-complete
        // work. Idempotency of the underlying functions makes the double-call
        // safe but not free; at Wiktionary scale each redundant pass is minutes.
    }

    public async Task PopulateSequencePhysicalityAsync(CancellationToken ct)
    {
        const int chunkSize = 16_384;
        const int maxRetries = 5;
        long totalInserted = 0;
        int chunksProcessed = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long inserted = 0;
            Exception? lastEx = null;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    await using NpgsqlConnection conn =
                        await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
                    await using (NpgsqlCommand setCmd = new(IngestionSql.PostPassSessionSettings, conn))
                    {
                        await setCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                    await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
                        conn,
                        SubstrateFunctionNames.PopulateSequencePhysicality,
                        [new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = chunkSize }]);
                    cmd.CommandTimeout = 0;
                    object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    inserted = result is long l ? l : (long?)result ?? 0L;
                    lastEx = null;
                    break;
                }
                catch (Exception ex) when (
                    ex is NpgsqlException ||
                    ex is System.IO.IOException ||
                    ex is System.Net.Sockets.SocketException ||
                    (ex.InnerException is System.Net.Sockets.SocketException))
                {
                    lastEx = ex;
                    int delayMs = 1000 * (1 << attempt);
                    Log.PostPassRetry(_logger, "populate_sequence_physicality", attempt + 1, delayMs, ex);
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
            }

            if (lastEx is not null)
            {
                Log.PostPassGivingUp(_logger, "populate_sequence_physicality", maxRetries, totalInserted, lastEx);
                throw new InvalidOperationException(
                    "populate_sequence_physicality failed after retry exhaustion; phase cannot complete with missing sequence physicality.",
                    lastEx);
            }

            totalInserted += inserted;
            chunksProcessed++;
            if (chunksProcessed % 50 == 0 || inserted == 0)
            {
                Log.PostPassProgress(_logger, "populate_sequence_physicality", chunksProcessed, totalInserted);
            }
            if (inserted == 0)
            {
                break;
            }
        }

        Log.SequencePhysicalityPopulated(_logger, totalInserted);
    }

    /// <summary>
    /// Prime substrate.edge_significance for every arena currently in
    /// substrate.significance_context, scanning current edges via the
    /// watermark-based <c>substrate.prime_unprimed_edges_chunk</c>. The scan
    /// is reset at the start of each phase-owned pass because later phases can
    /// add lower edge_type_id values. AP-1 compliant: re-reads the arena list
    /// at call time so newly-added arenas are included.
    /// </summary>
    public async Task PrimeAllSignificanceAsync(CancellationToken ct)
    {
        const int chunkSize = 16_384;
        const int maxRetries = 5;

        // Snapshot arena list under its own resilient connection.
        List<int> arenaIds = new();
        Exception? arenaListException = null;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                await using NpgsqlConnection listConn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
                await using NpgsqlCommand listCmd = NpgsqlSubstrateCommand.CreateFunction(
                    listConn,
                    SubstrateFunctionNames.SignificanceContextIds);
                listCmd.CommandTimeout = 0;
                await using NpgsqlDataReader r = await listCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                arenaIds.Clear();
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    arenaIds.Add(r.GetInt32(0));
                }
                arenaListException = null;
                break;
            }
            catch (Exception ex) when (
                ex is NpgsqlException || ex is System.IO.IOException ||
                ex is System.Net.Sockets.SocketException ||
                (ex.InnerException is System.Net.Sockets.SocketException))
            {
                arenaListException = ex;
                int delayMs = 1000 * (1 << attempt);
                Log.PostPassRetry(_logger, "significance_context_list", attempt + 1, delayMs, ex);
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }

        if (arenaListException is not null)
        {
            Log.PostPassGivingUp(_logger, "significance_context_list", maxRetries, 0, arenaListException);
            throw new InvalidOperationException(
                "significance_context_list failed after retry exhaustion; phase cannot complete without arena coverage.",
                arenaListException);
        }

        if (arenaIds.Count == 0)
        {
            throw new InvalidOperationException(
                "significance_context_list returned zero arenas; phase cannot complete without edge significance arena coverage.");
        }

        await using (NpgsqlConnection resetConn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        await using (NpgsqlCommand resetCmd = NpgsqlSubstrateCommand.CreateFunction(
                         resetConn,
                         SubstrateFunctionNames.ResetArenaPrimingState))
        {
            resetCmd.CommandTimeout = 0;
            await resetCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }

        long totalScanned = 0;
        foreach (int arenaId in arenaIds)
        {
            string label = $"prime_significance(arena={arenaId})";
            int chunksProcessed = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                long scanned = 0;
                Exception? lastEx = null;
                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    try
                    {
                        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
                        await using (NpgsqlCommand setCmd = new(IngestionSql.PostPassSessionSettings, conn))
                        {
                            await setCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        }
                        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
                            conn,
                            SubstrateFunctionNames.PrimeUnprimedEdgesChunk,
                            [
                                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = arenaId },
                                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = chunkSize }
                            ]);
                        cmd.CommandTimeout = 0;
                        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                        scanned = result is long l ? l : (long?)result ?? 0L;
                        lastEx = null;
                        break;
                    }
                    catch (Exception ex) when (
                        ex is NpgsqlException || ex is System.IO.IOException ||
                        ex is System.Net.Sockets.SocketException ||
                        (ex.InnerException is System.Net.Sockets.SocketException))
                    {
                        lastEx = ex;
                        int delayMs = 1000 * (1 << attempt);
                        Log.PostPassRetry(_logger, label, attempt + 1, delayMs, ex);
                        await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    }
                }
                if (lastEx is not null)
                {
                    Log.PostPassGivingUp(_logger, label, maxRetries, totalScanned, lastEx);
                    throw new InvalidOperationException(
                        $"{label} failed after retry exhaustion; phase cannot complete with incomplete edge significance.",
                        lastEx);
                }
                totalScanned += scanned;
                chunksProcessed++;
                if (chunksProcessed % 50 == 0 || scanned == 0)
                {
                    Log.PostPassProgress(_logger, label, chunksProcessed, totalScanned);
                }
                if (scanned == 0)
                {
                    break;
                }
            }
        }
        Log.SignificancePrimed(_logger, arenaIds.Count, totalScanned);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await FlushAsync(default).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }

        _shutdown.Cancel();
        try
        {
            await _periodicSnapshotTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }
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
        DrainSqlSpec sql = IngestionSql.Entity;
        await DrainKindAsync(
            _entities.Reader,
            tempCreate: sql.TempCreate,
            copySql: sql.Copy,
            truncateSql: sql.Truncate,
            drainSql: sql.Drain,
            kindName: "entities",
            kindIndex: KindIndex.Entity,
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
        DrainSqlSpec sql = IngestionSql.EntityClassification;
        await DrainKindAsync(
            _entityClassifications.Reader,
            tempCreate: sql.TempCreate,
            copySql: sql.Copy,
            truncateSql: sql.Truncate,
            drainSql: sql.Drain,
            kindName: "entity_classifications",
            kindIndex: KindIndex.EntityClassification,
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
        DrainSqlSpec sql = IngestionSql.Edge;
        await DrainKindAsync(
            _edges.Reader,
            tempCreate: sql.TempCreate,
            copySql: sql.Copy,
            truncateSql: sql.Truncate,
            // Inline geom path: ST_GeomFromWKB lifts producer-built EWKB to
            // substrate.edge.geom. Edges with NULL geom_wkb go in with
            // geom = NULL for substrate.populate_edge_trajectories
            // at end-of-phase via PopulateEdgeTrajectoriesAsync.
                        drainSql: sql.Drain,
            kindName: "edges",
            kindIndex: KindIndex.Edge,
            writeRow: async (writer, rec) =>
            {
                int edgeTypeId = await _codeResolver.EdgeTypeIdAsync(rec.EdgeTypeCode, ct).ConfigureAwait(false);
                int provenanceId = await _codeResolver.ProvenanceIdAsync(rec.ProvenanceCode, ct).ConfigureAwait(false);
                await writer.StartRowAsync(ct).ConfigureAwait(false);
                await writer.WriteAsync(edgeTypeId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.EdgeHash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                await writer.WriteAsync(provenanceId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                if (rec.GeomWkb is null)
                {
                    await writer.WriteNullAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteAsync(rec.GeomWkb, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                }
                Interlocked.Increment(ref _edgesEmitted);
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainEdgeMembersAsync(CancellationToken ct)
    {
        DrainSqlSpec sql = IngestionSql.EdgeMember;
        await DrainKindAsync(
            _edgeMembers.Reader,
            tempCreate: sql.TempCreate,
            copySql: sql.Copy,
            truncateSql: sql.Truncate,
            drainSql: sql.Drain,
            kindName: "edge_members",
            kindIndex: KindIndex.EdgeMember,
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
        DrainSqlSpec sql = IngestionSql.Junction;
        await DrainKindAsync(
            _junctions.Reader,
            tempCreate: sql.TempCreate,
            copySql: sql.Copy,
            truncateSql: sql.Truncate,
            // Junction routing: one INSERT per allowlisted target table. The
            // ELSE branch silently discards rows with unknown table_name —
            // EmitAsync's allowlist check should prevent this in practice.
                        drainSql: sql.Drain,
            kindName: "junctions",
            kindIndex: KindIndex.Junction,
            writeRow: async (writer, rec) =>
            {
                if (!AllowedJunctionTables.Contains(rec.JunctionTable))
                {
                    throw new ArgumentException(
                        $"JunctionRecord.JunctionTable not in allowlist: '{rec.JunctionTable}'");
                }
                int attestationTypeId = await _codeResolver
                    .AttestationTypeIdAsync(rec.AttestationTypeCode, ct).ConfigureAwait(false);
                await writer.StartRowAsync(ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.JunctionTable, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.EntityHash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.ReferenceId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                await writer.WriteAsync(attestationTypeId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
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
        DrainSqlSpec sql = IngestionSql.Physicality;
        await DrainKindAsync(
            _physicalities.Reader,
            tempCreate: sql.TempCreate,
            copySql: sql.Copy,
            truncateSql: sql.Truncate,
            // WKB → geometry conversion happens in this INSERT-SELECT step,
            // exactly as the deleted drain_staging_physicality_chunk did.
            // Producer streams raw WKB bytes (cheap to encode in C#);
            // ST_GeomFromWKB runs server-side once per chunk.
                        drainSql: sql.Drain,
            kindName: "physicalities",
            kindIndex: KindIndex.Physicality,
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
        DrainSqlSpec sql = IngestionSql.Sequence;
        await DrainKindAsync(
            _sequences.Reader,
            tempCreate: sql.TempCreate,
            copySql: sql.Copy,
            truncateSql: sql.Truncate,
            drainSql: sql.Drain,
            kindName: "sequences",
            kindIndex: KindIndex.Sequence,
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
        DrainSqlSpec sql = IngestionSql.EntitySignificance;
        await DrainKindAsync(
            _entitySignificances.Reader,
            tempCreate: sql.TempCreate,
            copySql: sql.Copy,
            truncateSql: sql.Truncate,
            drainSql: sql.Drain,
            kindName: "entity_significances",
            kindIndex: KindIndex.EntitySignificance,
            writeRow: async (writer, rec) =>
            {
                int contextId = await _codeResolver.SignificanceContextIdAsync(rec.ContextTypeCode, ct).ConfigureAwait(false);
                int attestationTypeId = await _codeResolver.AttestationTypeIdAsync(rec.AttestationTypeCode, ct).ConfigureAwait(false);
                await writer.StartRowAsync(ct).ConfigureAwait(false);
                await writer.WriteAsync(contextId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.EntityHash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                await writer.WriteAsync(attestationTypeId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.InitialMu, NpgsqlDbType.Double, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _entitySignificancesEmitted);
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainEdgeSignificancesAsync(CancellationToken ct)
    {
        DrainSqlSpec sql = IngestionSql.EdgeSignificance;
        await DrainKindAsync(
            _edgeSignificances.Reader,
            tempCreate: sql.TempCreate,
            copySql: sql.Copy,
            truncateSql: sql.Truncate,
            drainSql: sql.Drain,
            kindName: "edge_significances",
            kindIndex: KindIndex.EdgeSignificance,
            writeRow: async (writer, rec) =>
            {
                int contextId = await _codeResolver.SignificanceContextIdAsync(rec.ContextTypeCode, ct).ConfigureAwait(false);
                int edgeTypeId = await _codeResolver.EdgeTypeIdAsync(rec.EdgeTypeCode, ct).ConfigureAwait(false);
                int attestationTypeId = await _codeResolver.AttestationTypeIdAsync(rec.AttestationTypeCode, ct).ConfigureAwait(false);
                await writer.StartRowAsync(ct).ConfigureAwait(false);
                await writer.WriteAsync(contextId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                await writer.WriteAsync(edgeTypeId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.EdgeHash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                await writer.WriteAsync(attestationTypeId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                await writer.WriteAsync(rec.InitialMu, NpgsqlDbType.Double, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _edgeSignificancesEmitted);
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainEntityModelSourcesAsync(CancellationToken ct)
    {
        DrainSqlSpec sql = IngestionSql.EntityModelSource;
        await DrainKindAsync(
            _entityModelSources.Reader,
            tempCreate: sql.TempCreate,
            copySql: sql.Copy,
            truncateSql: sql.Truncate,
            drainSql: sql.Drain,
            kindName: "entity_model_sources",
            kindIndex: KindIndex.EntityModelSource,
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
        int kindIndex,
        Func<NpgsqlBinaryImporter, T, ValueTask> writeRow,
        CancellationToken ct)
    {
        try
        {
            await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

            // Per-connection temp_buffers bump. Default 8MB is far too small for
            // 32k-row PostGIS GeometryZM chunks (each LINESTRINGZM vertex is 32B
            // plus WKB overhead; a single physicality chunk can exceed 8MB and
            // trip PG 53000 'no empty local buffer available' which kills the
            // drain task and silently deadlocks the producer. Set per-session
            // before any temp pages are touched.
            await using (NpgsqlCommand tbufCmd = new(IngestionSql.DrainSessionSettings, conn))
            {
                await tbufCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

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
                        Interlocked.Add(ref _drainElapsedTicks[kindIndex], chunkSw.ElapsedTicks);
                        Interlocked.Add(ref _drainRowsCommitted[kindIndex], rowsInChunk);
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
            // Fail-fast: a dead drain leaves its channel writer with no consumer.
            // The producer's WriteAsync would block forever (this exact bug caused
            // a 1h+ silent stall when physicality drain hit PG 53000). Complete
            // every writer with the exception and trip the shutdown token so all
            // EmitAsync calls and sibling drains unwind immediately.
            FailAllWriters(ex);
            try { _shutdown.Cancel(); } catch { /* already disposed */ }
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

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Sequence physicality populated (post-pass): entities_inserted={EntitiesInserted}")]
        public static partial void SequencePhysicalityPopulated(ILogger logger, long entitiesInserted);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Edge trajectories populated (post-pass): edges_updated={EdgesUpdated}")]
        public static partial void EdgeTrajectoriesPopulated(ILogger logger, long edgesUpdated);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Edge significance primed (post-pass): arenas={Arenas} edge_rows_scanned={EdgeRowsScanned}")]
        public static partial void SignificancePrimed(ILogger logger, int arenas, long edgeRowsScanned);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Pipeline kind summary: kind={Kind} rows={Rows} drain={DrainElapsed} producerWait={WaitElapsed} rate={RowsPerSec:F0} rows/s")]
        public static partial void KindSummary(ILogger logger, string kind, long rows,
            TimeSpan drainElapsed, TimeSpan waitElapsed, double rowsPerSec);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Pipeline live: t={PhaseElapsed} kind={Kind} rows={Rows} drain={DrainElapsed} producerWait={WaitElapsed} rate={RowsPerSec:F0} rows/s")]
        public static partial void LiveSnapshot(ILogger logger, TimeSpan phaseElapsed, string kind, long rows,
            TimeSpan drainElapsed, TimeSpan waitElapsed, double rowsPerSec);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Pipeline periodic snapshot loop crashed")]
        public static partial void SnapshotLoopCrashed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Pipeline post-pass FAILED: pass={Pass}")]
        public static partial void PostPassFailed(ILogger logger, string pass, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Post-pass {Pass} transient failure (attempt {Attempt}); retrying in {DelayMs}ms")]
        public static partial void PostPassRetry(ILogger logger, string pass, int attempt, int delayMs, Exception ex);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Post-pass {Pass} gave up after {MaxRetries} retries; processed_so_far={ProcessedSoFar}")]
        public static partial void PostPassGivingUp(ILogger logger, string pass, int maxRetries, long processedSoFar, Exception ex);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Post-pass {Pass} progress: chunks={Chunks} processed={Processed}")]
        public static partial void PostPassProgress(ILogger logger, string pass, int chunks, long processed);
    }
}
