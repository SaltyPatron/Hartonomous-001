using System;
using System.Buffers.Binary;
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
/// Architecture:
///   * Persistent <c>substrate.staging_*</c> tables are not used. Drain happens
///     within the same connection that COPY-loaded the temp table before the
///     next chunk reads.
///   * Entity significance records are emitted inline by producers. Edge
///     significance is primed by the phase-owned post-pass, cross-producted
///     against every arena present at execution time.
///   * Edge LINESTRING4D geometry is built inline in C# when participant
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
    private const string SourceAuthorityContext = "source_authority";
    private const string ProvenanceAuthorityAttestation = "positive_evidence";
    private const double ProvenanceAuthorityEventWeight = 0.8;

    /// <summary>
    /// Channel capacity per record kind. ~256K bounded → ~MB-scale per-channel
    /// memory ceiling regardless of record count. EmitAsync awaits when full.
    /// Was 65_536 — bumped to reduce producer backpressure on multi-million-row
    /// seed phases (WordNet, Wiktionary, UD, Tatoeba) where producers were
    /// blocking on the bounded channel more often than necessary.
    /// </summary>
    private const int ChannelCapacity = 262_144;

    /// <summary>
    /// Default number of parallel drain workers per channel. Each worker owns its own
    /// long-lived NpgsqlConnection + its own pg_temp.X_inflight table (pg_temp
    /// is connection-local, so identical temp-table names don't collide). The
    /// local Linux PostgreSQL default is max_connections=50, so this is kept
    /// to two workers per regular kind; edge-rating events use one worker
    /// because Glicko-2 updates are state/order dependent.
    /// </summary>
    private const int DefaultDrainWorkersPerKind = 2;
    private const int EdgeRatingEventDrainWorkers = 1;

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
    /// Bounded substrate-diff window. This is intentionally larger than the
    /// COPY chunk so one streamed producer window can ask PostgreSQL about all
    /// candidate keys in one multi-result command, while drains still write in
    /// smaller COPY chunks. It is not an all-dataset preload.
    /// </summary>
    private const int DiffChunkRows = CopyChunkRows * 4;

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
    private readonly IngestionResourceMonitor _resourceMonitor = new();

    // One channel per record kind so each drain task can commit independently
    // without coordinating with other kinds. SingleReader=true means the
    // drain side is lock-free.
    private readonly Channel<EntityRecord> _entities;
    private readonly Channel<EntityClassificationRecord> _entityClassifications;
    private readonly Channel<EdgeRecord> _edges;
    private readonly Channel<EdgeMemberRecord> _edgeMembers;
    private readonly Channel<JunctionRecord> _junctions;
    private readonly Channel<PhysicalityRecord> _physicalities;
    private readonly Channel<EntitySignificanceRecord> _entitySignificances;
    private readonly Channel<EdgeSignificanceRecord> _edgeSignificances;
    private readonly Channel<EntityModelSourceRecord> _entityModelSources;
    private readonly Channel<EdgeRatingEventRecord> _edgeRatingEvents;
    private readonly SemaphoreSlim _ingressGate = new(1, 1);
    private List<IngestionRecord> _ingressBuffer = new(DiffChunkRows);
    private readonly SemaphoreSlim _diffGate = new(1, 1);
    private long _ingressBufferedRows;
    private long _ingressDiffRows;
    private long _nextIngressFlushSequence;
    private long _nextDiffFlushSequence;

    // Drain tasks are started lazily per kind on first emission. SQL-helper
    // phases such as UCD/UCA may construct a pipeline for phase orchestration
    // but emit no channel records; they must not reserve 40+ idle PG backends.
    private readonly List<Task> _drainTasks = [];
    private readonly object _drainTasksLock = new();
    private readonly bool[] _drainWorkersStarted = new bool[KindIndex.Count];

    // Per-kind row counters, updated atomically by drain tasks. Surfaces via
    // PipelineStats for observability and end-of-phase summary.
    private long _entitiesEmitted;
    private long _entityClassificationsEmitted;
    private long _edgesEmitted;
    private long _edgeMembersEmitted;
    private long _junctionsEmitted;
    private long _physicalitiesEmitted;
    private long _entitySignificancesEmitted;
    private long _edgeSignificancesEmitted;
    private long _entityModelSourcesEmitted;
    private long _edgeRatingEventsEmitted;
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
        public const int EntitySignificance = 6;
        public const int EdgeSignificance = 7;
        public const int EntityModelSource = 8;
        public const int EdgeRatingEvent = 9;
        public const int Count = 10;

        public static string Name(int idx) => idx switch
        {
            Entity => "entity",
            EntityClassification => "entity_classification",
            Edge => "edge",
            EdgeMember => "edge_member",
            Junction => "junction",
            Physicality => "physicality",
            EntitySignificance => "entity_significance",
            EdgeSignificance => "edge_significance",
            EntityModelSource => "entity_model_source",
            EdgeRatingEvent => "edge_rating_event",
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
        EnsureDrainWorkers(kindIndex);
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
    private readonly ConcurrentDictionary<Hash32, byte> _entityClassificationDedup = new();
    private readonly ConcurrentDictionary<Hash32, byte> _edgeDedup = new();
    private readonly ConcurrentDictionary<Hash32, byte> _edgeMemberDedup = new();
    private readonly ConcurrentDictionary<Hash32, byte> _physicalityDedup = new();
    private readonly ConcurrentDictionary<Hash32, byte> _entitySignificanceDedup = new();
    private readonly ConcurrentDictionary<Hash32, byte> _edgeSignificanceDedup = new();
    private long _entityDedupCount;
    private long _entityClassificationDedupCount;
    private long _edgeDedupCount;
    private long _edgeMemberDedupCount;
    private long _physicalityDedupCount;
    private long _entitySignificanceDedupCount;
    private long _edgeSignificanceDedupCount;

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
            SingleReader = false,
            SingleWriter = false,
        };

        _entities = Channel.CreateBounded<EntityRecord>(opts);
        _entityClassifications = Channel.CreateBounded<EntityClassificationRecord>(opts);
        _edges = Channel.CreateBounded<EdgeRecord>(opts);
        _edgeMembers = Channel.CreateBounded<EdgeMemberRecord>(opts);
        _junctions = Channel.CreateBounded<JunctionRecord>(opts);
        _physicalities = Channel.CreateBounded<PhysicalityRecord>(opts);
        _entitySignificances = Channel.CreateBounded<EntitySignificanceRecord>(opts);
        _edgeSignificances = Channel.CreateBounded<EdgeSignificanceRecord>(opts);
        _entityModelSources = Channel.CreateBounded<EntityModelSourceRecord>(opts);
        _edgeRatingEvents = Channel.CreateBounded<EdgeRatingEventRecord>(opts);

        // Periodic mid-phase progress snapshot. Fires every PeriodicSnapshotInterval
        // with one line per active kind: rows so far, drain elapsed, producer-wait
        // elapsed. Lets the operator watch progress live and tell whether the
        // pipeline is making forward motion or stuck. Fires under the same
        // CancellationToken; stops on dispose.
        _periodicSnapshotTask = Task.Run(() => PeriodicSnapshotAsync(_shutdown.Token));
    }

    private void EnsureDrainWorkers(int kindIndex)
    {
        if (Volatile.Read(ref _drainWorkersStarted[kindIndex]))
        {
            return;
        }

        lock (_drainTasksLock)
        {
            if (_drainWorkersStarted[kindIndex])
            {
                return;
            }

            int workerCount = DrainWorkerCount(kindIndex);
            for (int w = 0; w < workerCount; w++)
            {
                _drainTasks.Add(Task.Run(() => DrainKindAsync(kindIndex, _shutdown.Token)));
            }

            Volatile.Write(ref _drainWorkersStarted[kindIndex], true);
        }
    }

    private static int DrainWorkerCount(int kindIndex)
        => kindIndex == KindIndex.EdgeRatingEvent
            ? EdgeRatingEventDrainWorkers
            : DefaultDrainWorkersPerKind;

    private Task DrainKindAsync(int kindIndex, CancellationToken ct) => kindIndex switch
    {
        KindIndex.Entity => DrainEntitiesAsync(ct),
        KindIndex.EntityClassification => DrainEntityClassificationsAsync(ct),
        KindIndex.Edge => DrainEdgesAsync(ct),
        KindIndex.EdgeMember => DrainEdgeMembersAsync(ct),
        KindIndex.Junction => DrainJunctionsAsync(ct),
        KindIndex.Physicality => DrainPhysicalitiesAsync(ct),
        KindIndex.EntitySignificance => DrainEntitySignificancesAsync(ct),
        KindIndex.EdgeSignificance => DrainEdgeSignificancesAsync(ct),
        KindIndex.EntityModelSource => DrainEntityModelSourcesAsync(ct),
        KindIndex.EdgeRatingEvent => DrainEdgeRatingEventsAsync(ct),
        _ => throw new ArgumentOutOfRangeException(nameof(kindIndex), kindIndex, "Unknown ingestion record kind."),
    };

    private Task[] DrainTaskSnapshot()
    {
        lock (_drainTasksLock)
        {
            return _drainTasks.ToArray();
        }
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
        _entitySignificances.Writer.TryComplete(ex);
        _edgeSignificances.Writer.TryComplete(ex);
        _entityModelSources.Writer.TryComplete(ex);
        _edgeRatingEvents.Writer.TryComplete(ex);
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
                catch (OperationCanceledException) { return; } // BOUNDARY: periodic telemetry loop exits on pipeline shutdown.

                if (!_logger.IsEnabled(LogLevel.Information))
                {
                    continue;
                }

#pragma warning disable CA1873 // IsEnabled checked above; analyzer can't see across the loop.
                TimeSpan phaseElapsed = _phaseClock.Elapsed;
                long submittedTotal = 0;
                long drainedTotal = 0;
                int activeKinds = 0;
                for (int i = 0; i < KindIndex.Count; i++)
                {
                    long submitted = Interlocked.Read(ref _producerRowsSubmitted[i]);
                    long drained = Interlocked.Read(ref _drainRowsCommitted[i]);
                    submittedTotal += submitted;
                    drainedTotal += drained;
                    if (submitted > 0 || drained > 0)
                    {
                        activeKinds++;
                    }
                }
                long backlogTotal = Math.Max(0, submittedTotal - drainedTotal);
                long bufferedRows = Interlocked.Read(ref _ingressBufferedRows)
                    + Interlocked.Read(ref _ingressDiffRows);
                string pipelineState = bufferedRows > 0
                    ? "diffing producer records"
                    : backlogTotal > 0
                        ? "draining queued rows"
                        : activeKinds > 0
                            ? "caught up at phase boundary"
                            : "waiting for producer records";
                Log.PipelineState(_logger, phaseElapsed, pipelineState, bufferedRows, submittedTotal, drainedTotal,
                    backlogTotal, _producerDedupHits);
                IngestionResourceSnapshot resources = _resourceMonitor.Capture();
                Log.ResourceSnapshot(
                    _logger,
                    phaseElapsed,
                    resources.ProcessCpuPercent,
                    resources.ProcessCpuCores,
                    resources.SystemCpuPercent,
                    resources.SystemIoWaitPercent,
                    ToMib(resources.WorkingSetBytes),
                    ToMib(resources.ManagedHeapBytes),
                    resources.ThreadPoolBusyWorkers,
                    resources.ThreadPoolMaxWorkers,
                    ToMib(resources.SystemMemoryAvailableBytes),
                    ToMib(resources.SystemMemoryTotalBytes),
                    ToMib(resources.SwapFreeBytes),
                    ToMib(resources.SwapTotalBytes),
                    resources.ProcessReadMibPerSec,
                    resources.ProcessWriteMibPerSec,
                    ToGib(resources.PostgresDriveAvailableBytes),
                    ToGib(resources.PostgresDriveTotalBytes),
                    ToGib(resources.RootDriveAvailableBytes),
                    ToGib(resources.RootDriveTotalBytes));

                for (int i = 0; i < KindIndex.Count; i++)
                {
                    long rows = Interlocked.Read(ref _drainRowsCommitted[i]);
                    long submitted = Interlocked.Read(ref _producerRowsSubmitted[i]);
                    long backlog = Math.Max(0, submitted - rows);
                    long drainTicks = Interlocked.Read(ref _drainElapsedTicks[i]);
                    long waitTicks = Interlocked.Read(ref _producerWaitTicks[i]);
                    if (submitted == 0 && rows == 0 && drainTicks == 0 && waitTicks == 0)
                    {
                        continue;
                    }
                    TimeSpan drainElapsed = TimeSpan.FromSeconds((double)drainTicks / Stopwatch.Frequency);
                    TimeSpan waitElapsed = TimeSpan.FromSeconds((double)waitTicks / Stopwatch.Frequency);
                    double rowsPerSec = drainElapsed.TotalSeconds > 0
                        ? rows / drainElapsed.TotalSeconds : 0.0;
                    string kindState = backlog > 0 ? "draining backlog" : "caught up";
                    Log.LiveSnapshot(_logger, phaseElapsed, KindIndex.Name(i), kindState, submitted, rows, backlog,
                        drainElapsed, waitElapsed, rowsPerSec);
                }
#pragma warning restore CA1873
            }
        }

        catch (Exception ex) // BOUNDARY: periodic telemetry must never take down ingestion; it logs and exits.
        {
            // Snapshot loop must NEVER take down the pipeline. Log and exit.
            Log.SnapshotLoopCrashed(_logger, ex);
        }
    }

    private static double ToMib(long bytes) => bytes <= 0 ? 0.0 : bytes / 1048576.0;

    private static double ToGib(long bytes) => bytes <= 0 ? 0.0 : bytes / 1073741824.0;

    public StreamingPipelineStats Stats => new()
    {
        EntitiesEmitted = _entitiesEmitted,
        EntityClassificationsEmitted = _entityClassificationsEmitted,
        EdgesEmitted = _edgesEmitted,
        EdgeMembersEmitted = _edgeMembersEmitted,
        JunctionsEmitted = _junctionsEmitted,
        PhysicalitiesEmitted = _physicalitiesEmitted,
        EntitySignificancesEmitted = _entitySignificancesEmitted,
        EdgeSignificancesEmitted = _edgeSignificancesEmitted,
        EntityModelSourcesEmitted = _entityModelSourcesEmitted,
        CopyCommits = _copyCommits,
        CopyErrors = _copyErrors,
    };

    // ── Producer surfaces ────────────────────────────────────────────────
    // Batch and streaming producers both enter through the same funnel boundary:
    // records accumulate into a diff chunk, the pipeline asks PostgreSQL which
    // substrate identities already exist, then only the missing identity rows
    // enter the COPY drain channels. Evidence rows remain event streams and are
    // never suppressed by identity existence.

    public IIngestionBatch CreateBatch(string provenanceCode) => new IngestionBatch(provenanceCode);

    public IIngestionBatch CreateBatch() => new IngestionBatch("system_computed");

    public async Task SubmitBatchAsync(IIngestionBatch batch, CancellationToken ct)
    {
        if (batch is not IngestionBatch b)
        {
            throw new ArgumentException("Batch must be created by this pipeline.", nameof(batch));
        }

        List<IngestionRecord> records = await MaterializeBatchRecordsAsync(b, ct).ConfigureAwait(false);
        if (records.Count == 0)
        {
            return;
        }

        List<(long Sequence, List<IngestionRecord> Records)> buffersToFlush = [];
        await _ingressGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (IngestionRecord record in records)
            {
                BufferRecordUnderGate(record, buffersToFlush);
            }
        }
        finally
        {
            _ingressGate.Release();
        }

        foreach ((long sequence, List<IngestionRecord> buffer) in buffersToFlush)
        {
            await FlushIngressBufferCoreAsync(sequence, buffer, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask<List<IngestionRecord>> MaterializeBatchRecordsAsync(
        IngestionBatch b,
        CancellationToken ct)
    {
        int estimatedCount =
            b.Entities.Count
            + b.Physicalities.Count
            + b.Junctions.Count
            + b.Significances.Count
            + b.EntityModelSources.Count;
        foreach (EdgeEntry edge in b.Edges)
        {
            estimatedCount += 2 + edge.Members.Length + edge.SignificanceOverrides.Length + edge.RatingEvents.Length;
        }

        List<IngestionRecord> records = new(estimatedCount);
        string batchProvenance = b.ProvenanceCode;
        Dictionary<Hash32, (Hash32[] ChildHashes, int[] OrdinalStarts, int[] RleCounts)> compositionMetadata =
            BuildCompositionMetadata(b.CompositionChildren);
        HashSet<Hash32> parentsWithPhysicality = new();
        foreach (EntityEntry e in b.Entities)
        {
            records.Add(new EntityRecord(
                e.EntityTypeCode, e.Hash, batchProvenance,
                e.CentroidX, e.CentroidY, e.CentroidZ, e.CentroidM,
                e.HilbertIndex));
        }

        foreach (PhysicalityEntry p in b.Physicalities)
        {
            byte[] geometry;
            if (compositionMetadata.TryGetValue(p.Entity.Hash, out var metadata))
            {
                geometry = BuildCompositionGeometry(
                    metadata.ChildHashes, metadata.OrdinalStarts, metadata.RleCounts);
            }
            else if (p.ChildHashes is not null && p.OrdinalStarts is not null && p.RleCounts is not null)
            {
                geometry = BuildCompositionGeometry(p.ChildHashes, p.OrdinalStarts, p.RleCounts);
            }
            else
            {
                geometry = p.Geometry;
            }

            Hash32 contentHash = ComputePhysicalityContentHash(geometry);
            records.Add(new PhysicalityRecord(
                p.PhysicalityTypeCode,
                p.Entity.Hash,
                contentHash,
                geometry));
            parentsWithPhysicality.Add(p.Entity.Hash);
        }

        foreach (var pair in compositionMetadata)
        {
            if (parentsWithPhysicality.Contains(pair.Key))
            {
                continue;
            }

            byte[] geometry = BuildCompositionGeometry(
                pair.Value.ChildHashes, pair.Value.OrdinalStarts, pair.Value.RleCounts);
            Hash32 contentHash = ComputePhysicalityContentHash(geometry);
            records.Add(new PhysicalityRecord(
                "contour",
                pair.Key,
                contentHash,
                geometry));
        }

        Dictionary<string, int> edgeTypeIds = new(StringComparer.Ordinal);
        foreach (EdgeEntry edge in b.Edges)
        {
            if (!edgeTypeIds.TryGetValue(edge.EdgeTypeCode, out int edgeTypeId))
            {
                edgeTypeId = await _codeResolver.EdgeTypeIdAsync(edge.EdgeTypeCode, ct).ConfigureAwait(false);
                edgeTypeIds.Add(edge.EdgeTypeCode, edgeTypeId);
            }

            EdgeMemberSpec[] sorted = (EdgeMemberSpec[])edge.Members.Clone();
            Array.Sort(sorted, (a, c) => a.Position.CompareTo(c.Position));

            Hash32[] orderedHashes = new Hash32[sorted.Length];
            for (int j = 0; j < sorted.Length; j++)
            {
                orderedHashes[j] = sorted[j].Entity.Hash;
            }
            Hash32 edgeHash = ComputeEdgeHash(edgeTypeId, orderedHashes);
            byte[]? inlineGeometry = null;
            if (sorted.Length >= 2)
            {
                Hartonomous.Core.Geometry.Point4D[] verts =
                    new Hartonomous.Core.Geometry.Point4D[sorted.Length];
                for (int j = 0; j < sorted.Length; j++)
                {
                    verts[j] = IdentityPoint4D(sorted[j].Entity.Hash, j + 1);
                }
                inlineGeometry = Geometry4dPayloadBuilder.LineString((ReadOnlySpan<Hartonomous.Core.Geometry.Point4D>)verts);
            }

            records.Add(new EdgeRecord(edge.EdgeTypeCode, edgeHash, edge.ProvenanceCode, inlineGeometry));
            for (int j = 0; j < sorted.Length; j++)
            {
                records.Add(new EdgeMemberRecord(
                    edge.EdgeTypeCode, edgeHash,
                    sorted[j].Entity.Hash,
                    sorted[j].RoleCode,
                    sorted[j].Position));
            }

            EdgeSignificanceSpec[] overrides = edge.SignificanceOverrides;
            for (int o = 0; o < overrides.Length; o++)
            {
                EdgeSignificanceSpec sig = overrides[o];
                records.Add(new EdgeSignificanceRecord(
                    sig.ContextTypeCode,
                    string.IsNullOrEmpty(sig.AttestationTypeCode)
                        ? ProvenanceAuthorityAttestation
                        : sig.AttestationTypeCode,
                    edge.EdgeTypeCode,
                    edgeHash,
                    sig.InitialMu));
            }

            // Sign-bearing rating events fire for every observation; the SQL
            // function creates missing rows with the same provenance/edge-type
            // priors used by the end-of-phase priming pass.
            EdgeRatingEvent[] events = edge.RatingEvents;
            for (int e = 0; e < events.Length; e++)
            {
                EdgeRatingEvent ev = events[e];
                records.Add(new EdgeRatingEventRecord(
                    ev.ContextTypeCode, ev.AttestationTypeCode,
                    edge.EdgeTypeCode, edgeHash,
                    ev.Score, ev.Weight,
                    ev.ModelSourceId,
                    ev.TensorHash,
                    ev.PackageTensorHash,
                    ev.SourceTensorName,
                    ev.PrimitiveCode,
                    ev.TupleCode,
                    ev.SlotCode,
                    ev.ModalityCode,
                    ev.LayerIndex,
                    ev.HeadIndex,
                    ev.ExpertIndex,
                    ev.AdapterName,
                    ev.FusedSlice));
            }
            records.Add(new EdgeRatingEventRecord(
                SourceAuthorityContext,
                ProvenanceAuthorityAttestation,
                edge.EdgeTypeCode,
                edgeHash,
                Score: 1.0,
                Weight: ProvenanceAuthorityEventWeight));
        }

        foreach (JunctionEntry j in b.Junctions)
        {
            records.Add(new JunctionRecord(
                j.JunctionTable, j.Entity.Hash,
                j.ReferenceId,
                j.AttestationTypeCode ?? "positive_evidence",
                j.Mu));
        }

        foreach (SignificanceEntry sig in b.Significances)
        {
            records.Add(new EntitySignificanceRecord(
                sig.ContextTypeCode,
                sig.AttestationTypeCode ?? "positive_evidence",
                sig.Entity.Hash,
                sig.InitialMu));
        }

        foreach (EntityModelSourceEntry e in b.EntityModelSources)
        {
            records.Add(new EntityModelSourceRecord(
                e.Entity.Hash,
                e.ModelSourceId));
        }

        return records;
    }

    private static Dictionary<Hash32, (Hash32[] ChildHashes, int[] OrdinalStarts, int[] RleCounts)>
        BuildCompositionMetadata(IReadOnlyList<CompositionChildEntry> entries)
    {
        Dictionary<Hash32, List<CompositionChildEntry>> grouped = new();
        foreach (CompositionChildEntry entry in entries)
        {
            if (entry.RleCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(entries), "RLE count must be positive.");
            }
            if (!grouped.TryGetValue(entry.Parent.Hash, out             List<CompositionChildEntry>? list))
            {
                list = [];
                grouped.Add(entry.Parent.Hash, list);
            }
            list.Add(entry);
        }

        Dictionary<Hash32, (Hash32[] ChildHashes, int[] OrdinalStarts, int[] RleCounts)> metadata = new(grouped.Count);
        foreach (var pair in grouped)
        {
            pair.Value.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
            List<Hash32> childHashes = new(pair.Value.Count);
            List<int> ordinalStarts = new(pair.Value.Count);
            List<int> rleCounts = new(pair.Value.Count);
            int previousEnd = 0;
            for (int i = 0; i < pair.Value.Count; i++)
            {
                CompositionChildEntry entry = pair.Value[i];
                if (entry.Ordinal <= previousEnd)
                {
                    throw new InvalidOperationException(
                        $"Composition metadata for {pair.Key.ToHexString()} overlaps at ordinal {entry.Ordinal}.");
                }
                childHashes.Add(entry.Child.Hash);
                ordinalStarts.Add(entry.Ordinal);
                rleCounts.Add(entry.RleCount);
                previousEnd = entry.Ordinal + entry.RleCount - 1;
            }
            metadata.Add(pair.Key, (childHashes.ToArray(), ordinalStarts.ToArray(), rleCounts.ToArray()));
        }

        return metadata;
    }

    private static Hash32 ComputePhysicalityContentHash(byte[] geometry)
        => Hartonomous.Core.Compute.Common.Blake3.Hash32(geometry.AsSpan());

    /// <summary>
    /// Build a composition LINESTRINGZM payload from an ordered child manifest
    /// using the substrate mantissa packing contract. Each vertex encodes
    /// (X = bb_pack_hash_lo(child.bits_0_51),
    ///  Y = bb_pack_ordinal_rle(ordinal, rle),
    ///  Z = bb_pack_hash_hi(child.bits_52_103),
    ///  M = bb_pack_metadata(0)). The geometry IS the indexed relational
    /// child manifest at this composition tier; substrate.get_composition_children
    /// reverses by unpacking each vertex and joining against
    /// substrate.entity's (hash_bits_0_51, hash_bits_52_103) composite btree.
    /// </summary>
    private static byte[] BuildCompositionGeometry(
        Hash32[] childHashes, int[] ordinals, int[] rleCounts)
    {
        if (childHashes.Length != ordinals.Length || childHashes.Length != rleCounts.Length)
        {
            throw new InvalidOperationException("Composition manifest arrays must have matching lengths.");
        }
        var verts = new Hartonomous.Core.Geometry.Point4D[childHashes.Length];
        for (int i = 0; i < childHashes.Length; i++)
        {
            verts[i] = new Hartonomous.Core.Geometry.Point4D(
                MantissaPacking.PackHashLo(childHashes[i].BitsLow52()),
                MantissaPacking.PackOrdinalRle(ordinals[i], rleCounts[i]),
                MantissaPacking.PackHashHi(childHashes[i].BitsHigh52()),
                MantissaPacking.PackMetadata(0L));
        }
        return Geometry4dPayloadBuilder.LineString((ReadOnlySpan<Hartonomous.Core.Geometry.Point4D>)verts);
    }

    /// <summary>
    /// Mantissa-packed identity-POINTZM for an entity, used as a vertex of
    /// edge.geom LINESTRINGZM. Mirrors substrate.populate_edge_trajectories
    /// inline: (bb_pack_hash_lo(hash.bits_0_51), bb_pack_ordinal_rle(rolePosition, 1),
    /// bb_pack_hash_hi(hash.bits_52_103), bb_pack_metadata(0)).
    /// </summary>
    private static Hartonomous.Core.Geometry.Point4D IdentityPoint4D(Hash32 hash, int rolePosition)
        => new(
            MantissaPacking.PackHashLo(hash.BitsLow52()),
            MantissaPacking.PackOrdinalRle(rolePosition, 1),
            MantissaPacking.PackHashHi(hash.BitsHigh52()),
            MantissaPacking.PackMetadata(0L));

    public async Task DrainPendingAsync(CancellationToken ct)
    {
        await FlushIngressBufferAsync(ct).ConfigureAwait(false);

        long[] targetRows = new long[KindIndex.Count];
        for (int i = 0; i < KindIndex.Count; i++)
        {
            targetRows[i] = Interlocked.Read(ref _producerRowsSubmitted[i]);
        }

        while (true)
        {
            Task[] drainTasks = DrainTaskSnapshot();
            for (int i = 0; i < drainTasks.Length; i++)
            {
                if (drainTasks[i].IsFaulted)
                {
                    await Task.WhenAll(drainTasks).ConfigureAwait(false);
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
                // P1f: drain completion is the post-pass trigger, NOT phase
                // completion. SequentialPhaseRunner no longer invokes the
                // post-passes — the substrate is continuously queryable, no
                // phase boundaries. Every DrainPendingAsync invocation
                // guarantees edge.geom is non-null and significance is
                // primed across all current arenas before returning. New
                // edges still inserted with raw geom = NULL only briefly,
                // closed by the immediately-following bulk geom build.
                if (Interlocked.Read(ref _edgesEmitted) > 0)
                {
                    await PopulateEdgeTrajectoriesAsync(ct).ConfigureAwait(false);
                    await PrimeAllSignificanceAsync(ct).ConfigureAwait(false);
                }
                return;
            }

            await Task.Delay(IdleFlushAfter, ct).ConfigureAwait(false);
        }
    }

    // ── Substrate-aware ingestion: bulk existence checks ──────────────────
    //
    // EmitAsync calls these once per diff chunk. Producers compute identities
    // locally; this pipeline owns the substrate comparison and only forwards
    // missing identity rows into COPY. ON CONFLICT DO NOTHING in the drain
    // INSERT-SELECT remains as the cross-session race guard.

    public async Task<HashSet<HashKey>> GetExistingEntityHashesAsync(
        IReadOnlyCollection<Hash32> hashes, CancellationToken ct)
    {
        HashSet<HashKey> existing = new(hashes.Count);
        if (hashes.Count == 0)
        {
            return existing;
        }

        byte[][] arr = new byte[hashes.Count][];
        int i = 0;
        foreach (Hash32 h in hashes)
        {
            arr[i++] = h.ToByteArray();
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
        Dictionary<string, int> entityTypeIds = new(StringComparer.Ordinal);
        Dictionary<string, int> provenanceIds = new(StringComparer.Ordinal);
        foreach (EntityClassificationKey k in tuples)
        {
            hashArr[i] = k.EntityHash.ToByteArray();
            if (!entityTypeIds.TryGetValue(k.EntityTypeCode, out int entityTypeId))
            {
                entityTypeId = await _codeResolver.EntityTypeIdAsync(k.EntityTypeCode, ct).ConfigureAwait(false);
                entityTypeIds.Add(k.EntityTypeCode, entityTypeId);
            }
            if (!provenanceIds.TryGetValue(k.ProvenanceCode, out int provenanceId))
            {
                provenanceId = await _codeResolver.ProvenanceIdAsync(k.ProvenanceCode, ct).ConfigureAwait(false);
                provenanceIds.Add(k.ProvenanceCode, provenanceId);
            }
            etArr[i] = entityTypeId;
            pArr[i] = provenanceId;
            i++;
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
        Dictionary<string, int> edgeTypeIds = new(StringComparer.Ordinal);
        foreach (EdgeKey k in tuples)
        {
            if (!edgeTypeIds.TryGetValue(k.EdgeTypeCode, out int edgeTypeId))
            {
                edgeTypeId = await _codeResolver.EdgeTypeIdAsync(k.EdgeTypeCode, ct).ConfigureAwait(false);
                edgeTypeIds.Add(k.EdgeTypeCode, edgeTypeId);
            }
            etArr[i] = edgeTypeId;
            hashArr[i] = k.EdgeHash.ToByteArray();
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

    public async Task<HashSet<EdgeMemberKey>> GetExistingEdgeMembersAsync(
        IReadOnlyCollection<EdgeMemberKey> tuples, CancellationToken ct)
    {
        HashSet<EdgeMemberKey> existing = new(tuples.Count);
        if (tuples.Count == 0)
        {
            return existing;
        }

        int n = tuples.Count;
        int[] etArr = new int[n];
        byte[][] edgeHashArr = new byte[n][];
        byte[][] entityHashArr = new byte[n][];
        int[] roleArr = new int[n];
        int[] positionArr = new int[n];
        int i = 0;
        Dictionary<string, int> edgeTypeIds = new(StringComparer.Ordinal);
        Dictionary<string, int> roleIds = new(StringComparer.Ordinal);
        foreach (EdgeMemberKey k in tuples)
        {
            if (!edgeTypeIds.TryGetValue(k.EdgeTypeCode, out int edgeTypeId))
            {
                edgeTypeId = await _codeResolver.EdgeTypeIdAsync(k.EdgeTypeCode, ct).ConfigureAwait(false);
                edgeTypeIds.Add(k.EdgeTypeCode, edgeTypeId);
            }
            if (!roleIds.TryGetValue(k.RoleCode, out int roleId))
            {
                roleId = await _codeResolver.EdgeRoleIdAsync(k.RoleCode, ct).ConfigureAwait(false);
                roleIds.Add(k.RoleCode, roleId);
            }
            etArr[i] = edgeTypeId;
            edgeHashArr[i] = k.EdgeHash.ToByteArray();
            entityHashArr[i] = k.EntityHash.ToByteArray();
            roleArr[i] = roleId;
            positionArr[i] = k.RolePosition;
            i++;
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(IngestionSql.GetExistingEdgeMembers, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = etArr,          NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = edgeHashArr,    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        cmd.Parameters.Add(new NpgsqlParameter { Value = entityHashArr,  NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        cmd.Parameters.Add(new NpgsqlParameter { Value = roleArr,        NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = positionArr,    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string etCode = (string)r[0];
            byte[] edgeHash = (byte[])r[1];
            byte[] entityHash = (byte[])r[2];
            string roleCode = (string)r[3];
            int rolePosition = (int)r[4];
            existing.Add(new EdgeMemberKey(etCode, edgeHash, entityHash, roleCode, rolePosition));
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
        Dictionary<string, int> physicalityTypeIds = new(StringComparer.Ordinal);
        foreach (PhysicalityKey k in tuples)
        {
            if (!physicalityTypeIds.TryGetValue(k.PhysicalityTypeCode, out int physicalityTypeId))
            {
                physicalityTypeId = await _codeResolver.PhysicalityTypeIdAsync(k.PhysicalityTypeCode, ct).ConfigureAwait(false);
                physicalityTypeIds.Add(k.PhysicalityTypeCode, physicalityTypeId);
            }
            ptArr[i] = physicalityTypeId;
            ehArr[i] = k.EntityHash.ToByteArray();
            chArr[i] = k.ContentHash.ToByteArray();
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

    public async Task PopulateEdgeTrajectoriesAsync(CancellationToken ct)
    {
        // Populate geom on edges where the producer didn't (or couldn't)
        // attach an inline LINESTRING4D geometry. Per-chunk connection so a PG
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
                catch (Exception ex) when ( // BOUNDARY: transient post-pass connection failure is retried before fail-loud exhaustion.
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

    private static Hash32 ComputeEdgeHash(int edgeTypeId, ReadOnlySpan<Hash32> orderedMemberHashes)
    {
        Span<byte> buffer = orderedMemberHashes.Length <= 8
            ? stackalloc byte[4 + orderedMemberHashes.Length * Hash32.Length]
            : new byte[4 + orderedMemberHashes.Length * Hash32.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(0, 4), edgeTypeId);
        int offset = 4;
        for (int i = 0; i < orderedMemberHashes.Length; i++)
        {
            orderedMemberHashes[i].CopyTo(buffer.Slice(offset, Hash32.Length));
            offset += Hash32.Length;
        }
        return Hartonomous.Core.Compute.Common.Blake3.Hash32(buffer);
    }

    public async ValueTask EmitAsync(IngestionRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        (long Sequence, List<IngestionRecord> Records)? bufferToFlush;
        await _ingressGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bufferToFlush = BufferRecordUnderGate(record);
        }
        finally
        {
            _ingressGate.Release();
        }

        if (bufferToFlush is not null)
        {
            await FlushIngressBufferCoreAsync(
                bufferToFlush.Value.Sequence,
                bufferToFlush.Value.Records,
                ct).ConfigureAwait(false);
        }
    }

    private (long Sequence, List<IngestionRecord> Records)? BufferRecordUnderGate(IngestionRecord record)
    {
        _ingressBuffer.Add(record);
        Interlocked.Exchange(ref _ingressBufferedRows, _ingressBuffer.Count);
        if (_ingressBuffer.Count >= DiffChunkRows)
        {
            return DetachIngressBufferUnderGate();
        }

        return null;
    }

    private void BufferRecordUnderGate(
        IngestionRecord record,
        List<(long Sequence, List<IngestionRecord> Records)> buffersToFlush)
    {
        (long Sequence, List<IngestionRecord> Records)? bufferToFlush = BufferRecordUnderGate(record);
        if (bufferToFlush is not null)
        {
            buffersToFlush.Add(bufferToFlush.Value);
        }
    }

    private (long Sequence, List<IngestionRecord> Records) DetachIngressBufferUnderGate()
    {
        List<IngestionRecord> buffer = _ingressBuffer;
        _ingressBuffer = new List<IngestionRecord>(DiffChunkRows);
        Interlocked.Exchange(ref _ingressBufferedRows, 0);
        return (_nextIngressFlushSequence++, buffer);
    }

    private async ValueTask FlushIngressBufferAsync(CancellationToken ct)
    {
        (long Sequence, List<IngestionRecord> Records)? bufferToFlush;
        await _ingressGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bufferToFlush = _ingressBuffer.Count == 0
                ? null
                : DetachIngressBufferUnderGate();
        }
        finally
        {
            _ingressGate.Release();
        }

        if (bufferToFlush is not null)
        {
            await FlushIngressBufferCoreAsync(
                bufferToFlush.Value.Sequence,
                bufferToFlush.Value.Records,
                ct).ConfigureAwait(false);
        }
    }

    private async ValueTask FlushIngressBufferCoreAsync(long sequence, List<IngestionRecord> buffer, CancellationToken ct)
    {
        if (buffer.Count == 0)
        {
            return;
        }

        while (Volatile.Read(ref _nextDiffFlushSequence) != sequence)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        await _diffGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FlushIngressBufferCoreUnderGateAsync(buffer, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _ingressDiffRows, 0);
            Interlocked.Increment(ref _nextDiffFlushSequence);
            _diffGate.Release();
        }
    }

    private async ValueTask FlushIngressBufferCoreUnderGateAsync(List<IngestionRecord> buffer, CancellationToken ct)
    {
        Interlocked.Exchange(ref _ingressDiffRows, buffer.Count);

        HashSet<EdgeKey> edgeRecordsInBuffer = new();
        Dictionary<Hash32, (Hash32[] ChildHashes, int[] OrdinalStarts, int[] RleCounts)> compositionMetadata =
            BuildCompositionMetadata(buffer);
        foreach (IngestionRecord record in buffer)
        {
            if (record is EdgeRecord edge)
            {
                edgeRecordsInBuffer.Add(new EdgeKey(edge.EdgeTypeCode, edge.EdgeHash));
            }
        }

        foreach (IngestionRecord record in buffer)
        {
            switch (record)
            {
                case EdgeMemberRecord r:
                {
                    EdgeKey key = new(r.EdgeTypeCode, r.EdgeHash);
                    Hash32 edgeDedupKey = ComposeKey(r.EdgeTypeCode, r.EdgeHash);
                    if (edgeRecordsInBuffer.Contains(key) || _edgeDedup.ContainsKey(edgeDedupKey))
                    {
                        await EmitDirectAsync(r, ct).ConfigureAwait(false);
                        break;
                    }

                    throw new InvalidOperationException(
                        $"Edge member references missing edge {r.EdgeTypeCode}:{r.EdgeHash.ToHexString()}.");
                }
                case EntityRecord:
                case EntityClassificationRecord:
                case EdgeRecord:
                case PhysicalityRecord:
                case EntitySignificanceRecord:
                {
                    IngestionRecord recordToEmit = record;
                    if (record is PhysicalityRecord physicality &&
                        compositionMetadata.TryGetValue(physicality.EntityHash, out var metadata))
                    {
                        byte[] geometry = BuildCompositionGeometry(
                            metadata.ChildHashes, metadata.OrdinalStarts, metadata.RleCounts);
                        Hash32 contentHash = ComputePhysicalityContentHash(geometry);
                        recordToEmit = physicality with
                        {
                            Geometry = geometry,
                            ContentHash = contentHash,
                        };
                    }
                    await EmitDirectAsync(recordToEmit, ct).ConfigureAwait(false);
                    break;
                }
                case CompositionChildRecord:
                    break;
                default:
                    await EmitDirectAsync(record, ct).ConfigureAwait(false);
                    break;
            }
        }

        buffer.Clear();
    }

    private static Dictionary<Hash32, (Hash32[] ChildHashes, int[] OrdinalStarts, int[] RleCounts)>
        BuildCompositionMetadata(IEnumerable<IngestionRecord> records)
    {
        Dictionary<Hash32, List<CompositionChildRecord>> grouped = new();
        foreach (IngestionRecord record in records)
        {
            if (record is not CompositionChildRecord entry)
            {
                continue;
            }
            if (entry.RleCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(records), "RLE count must be positive.");
            }
            if (!grouped.TryGetValue(entry.ParentEntityHash, out List<CompositionChildRecord>? list))
            {
                list = [];
                grouped.Add(entry.ParentEntityHash, list);
            }
            list.Add(entry);
        }

        Dictionary<Hash32, (Hash32[] ChildHashes, int[] OrdinalStarts, int[] RleCounts)> metadata = new(grouped.Count);
        foreach (var pair in grouped)
        {
            pair.Value.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
            Hash32[] childHashes = new Hash32[pair.Value.Count];
            int[] ordinalStarts = new int[pair.Value.Count];
            int[] rleCounts = new int[pair.Value.Count];
            int previousEnd = 0;
            for (int i = 0; i < pair.Value.Count; i++)
            {
                CompositionChildRecord entry = pair.Value[i];
                if (entry.Ordinal <= previousEnd)
                {
                    throw new InvalidOperationException(
                        $"Composition metadata for {pair.Key.ToHexString()} overlaps at ordinal {entry.Ordinal}.");
                }
                childHashes[i] = entry.ChildEntityHash;
                ordinalStarts[i] = entry.Ordinal;
                rleCounts[i] = entry.RleCount;
                previousEnd = entry.Ordinal + entry.RleCount - 1;
            }
            metadata.Add(pair.Key, (childHashes, ordinalStarts, rleCounts));
        }

        return metadata;
    }

    private ValueTask EmitDirectAsync(IngestionRecord record, CancellationToken ct)
    {
        return record switch
        {
            EntityRecord r => EmitEntityWithClassificationAsync(r, ct),
            EntityClassificationRecord r => EmitEntityClassificationAsync(r, ct),
            EdgeRecord r => EmitEdgeAsync(r, ct),
            EdgeMemberRecord r => EmitEdgeMemberAsync(r, ct),
            JunctionRecord r => WriteTrackedAsync(_junctions.Writer, r, KindIndex.Junction, ct),
            PhysicalityRecord r => EmitPhysicalityAsync(r, ct),
            CompositionChildRecord => ValueTask.CompletedTask,
            EntitySignificanceRecord r => EmitEntitySignificanceAsync(r, ct),
            EdgeSignificanceRecord r => EmitEdgeSignificanceAsync(r, ct),
            EntityModelSourceRecord r => WriteTrackedAsync(_entityModelSources.Writer, r, KindIndex.EntityModelSource, ct),
            EdgeRatingEventRecord r => WriteTrackedAsync(_edgeRatingEvents.Writer, r, KindIndex.EdgeRatingEvent, ct),
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
        Hash32 key = r.Hash;
        if (TryAddDedup(_entityDedup, ref _entityDedupCount, key))
        {
            await WriteTrackedAsync(_entities.Writer, r, KindIndex.Entity, ct).ConfigureAwait(false);
        }
        await EmitEntityClassificationAsync(
            new EntityClassificationRecord(r.Hash, r.EntityTypeCode, r.ProvenanceCode),
            ct).ConfigureAwait(false);
    }

    private async ValueTask EmitEntityClassificationAsync(EntityClassificationRecord r, CancellationToken ct)
    {
        Hash32 key = ComposeKey(r.EntityTypeCode, r.ProvenanceCode, r.EntityHash);
        if (TryAddDedup(_entityClassificationDedup, ref _entityClassificationDedupCount, key))
        {
            await WriteTrackedAsync(_entityClassifications.Writer, r, KindIndex.EntityClassification, ct)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask EmitEdgeAsync(EdgeRecord r, CancellationToken ct)
    {
        // Dedup key includes edge type because (edge_type_id, hash) is the PK.
        Hash32 key = ComposeKey(r.EdgeTypeCode, r.EdgeHash);
        if (TryAddDedup(_edgeDedup, ref _edgeDedupCount, key))
        {
            await WriteTrackedAsync(_edges.Writer, r, KindIndex.Edge, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask EmitEdgeMemberAsync(EdgeMemberRecord r, CancellationToken ct)
    {
        Hash32 key = ComposeKey(r.EdgeTypeCode, r.RoleCode, r.EdgeHash, r.EntityHash, r.RolePosition);
        if (TryAddDedup(_edgeMemberDedup, ref _edgeMemberDedupCount, key))
        {
            await WriteTrackedAsync(_edgeMembers.Writer, r, KindIndex.EdgeMember, ct)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask EmitPhysicalityAsync(PhysicalityRecord r, CancellationToken ct)
    {
        // (physicality_type_id, entity_hash, content_hash) is the PK.
        Hash32 key = ComposeKey(r.PhysicalityTypeCode, r.EntityHash, r.ContentHash);
        if (TryAddDedup(_physicalityDedup, ref _physicalityDedupCount, key))
        {
            await WriteTrackedAsync(_physicalities.Writer, r, KindIndex.Physicality, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask EmitEntitySignificanceAsync(EntitySignificanceRecord r, CancellationToken ct)
    {
        // Dedup key includes attestation_type — same (arena, entity) under
        // different attestation_types is intentionally distinct rating data,
        // not a duplicate. Collapsing them would conflate evidence kinds.
        Hash32 key = ComposeKey(r.ContextTypeCode, r.AttestationTypeCode, r.EntityHash);
        if (TryAddDedup(_entitySignificanceDedup, ref _entitySignificanceDedupCount, key))
        {
            await WriteTrackedAsync(_entitySignificances.Writer, r, KindIndex.EntitySignificance, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask EmitEdgeSignificanceAsync(EdgeSignificanceRecord r, CancellationToken ct)
    {
        // Dedup key includes attestation_type for the same reason as the
        // entity-side: positive_evidence vs positive_evidence
        // vs model_attention_pattern attestations on the same edge are
        // distinct rating rows, not duplicates.
        Hash32 key = ComposeKey(r.ContextTypeCode, r.AttestationTypeCode, r.EdgeTypeCode, r.EdgeHash);
        if (TryAddDedup(_edgeSignificanceDedup, ref _edgeSignificanceDedupCount, key))
        {
            await WriteTrackedAsync(_edgeSignificances.Writer, r, KindIndex.EdgeSignificance, ct).ConfigureAwait(false);
        }
    }

    private static Hash32 ComposeKey(string codeA, string codeB, string codeC, Hash32 hash)
    {
        byte[] aBytes = System.Text.Encoding.UTF8.GetBytes(codeA);
        byte[] bBytes = System.Text.Encoding.UTF8.GetBytes(codeB);
        byte[] cBytes = System.Text.Encoding.UTF8.GetBytes(codeC);
        byte[] buf = new byte[aBytes.Length + 1 + bBytes.Length + 1 + cBytes.Length + 1 + Hash32.Length];
        int o = 0;
        Buffer.BlockCopy(aBytes, 0, buf, o, aBytes.Length); o += aBytes.Length;
        buf[o++] = 0x1F;
        Buffer.BlockCopy(bBytes, 0, buf, o, bBytes.Length); o += bBytes.Length;
        buf[o++] = 0x1F;
        Buffer.BlockCopy(cBytes, 0, buf, o, cBytes.Length); o += cBytes.Length;
        buf[o++] = 0x1F;
        hash.CopyTo(buf.AsSpan(o, Hash32.Length));
        return Hartonomous.Core.Compute.Common.Blake3.Hash32(buf);
    }

    private static Hash32 ComposeKey(string codeA, string codeB, Hash32 hashA, Hash32 hashB, int ordinal)
    {
        byte[] aBytes = System.Text.Encoding.UTF8.GetBytes(codeA);
        byte[] bBytes = System.Text.Encoding.UTF8.GetBytes(codeB);
        byte[] buf = new byte[aBytes.Length + 1 + bBytes.Length + 1 + Hash32.Length + 1 + Hash32.Length + 1 + 4];
        int o = 0;
        Buffer.BlockCopy(aBytes, 0, buf, o, aBytes.Length); o += aBytes.Length;
        buf[o++] = 0x1F;
        Buffer.BlockCopy(bBytes, 0, buf, o, bBytes.Length); o += bBytes.Length;
        buf[o++] = 0x1F;
        hashA.CopyTo(buf.AsSpan(o, Hash32.Length)); o += Hash32.Length;
        buf[o++] = 0x1F;
        hashB.CopyTo(buf.AsSpan(o, Hash32.Length)); o += Hash32.Length;
        buf[o++] = 0x1F;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o, 4), ordinal);
        return Hartonomous.Core.Compute.Common.Blake3.Hash32(buf);
    }

    /// <summary>
    /// Try to record a dedup key. Returns true if the key was added (i.e.,
    /// the caller should proceed to emit); false if already present (drop).
    /// When the dictionary exceeds <see cref="DedupCapacityPerChannel"/> the
    /// dedup state is cleared — false negatives become possible afterward
    /// (re-emission of an earlier hash) but ON CONFLICT DO NOTHING in the
    /// drain INSERT-SELECT catches them. Bounded memory by design.
    /// </summary>
    private bool TryAddDedup(ConcurrentDictionary<Hash32, byte> dedup, ref long approximateCount, Hash32 key)
    {
        if (Volatile.Read(ref approximateCount) >= DedupCapacityPerChannel)
        {
            // Best-effort reset; another thread may add concurrently — fine.
            dedup.Clear();
            Volatile.Write(ref approximateCount, 0);
        }
        if (dedup.TryAdd(key, 0))
        {
            Interlocked.Increment(ref approximateCount);
            return true;
        }
        Interlocked.Increment(ref _producerDedupHits);
        return false;
    }

    private static void MarkDedup(ConcurrentDictionary<Hash32, byte> dedup, ref long approximateCount, Hash32 key)
    {
        if (Volatile.Read(ref approximateCount) >= DedupCapacityPerChannel)
        {
            dedup.Clear();
            Volatile.Write(ref approximateCount, 0);
        }
        if (dedup.TryAdd(key, 0))
        {
            Interlocked.Increment(ref approximateCount);
        }
    }

    /// <summary>
    /// Mix a string code and a 32-byte hash into a Hash32 dedup key. Uses
    /// BLAKE3 over the concatenation. Used for composite PKs whose key
    /// includes a reference-table code that the producer hasn't yet
    /// resolved to an int (resolution happens in the drain task at write
    /// time). Stable across calls because BLAKE3 is deterministic.
    /// </summary>
    private static Hash32 ComposeKey(string code, Hash32 hash)
    {
        byte[] codeBytes = System.Text.Encoding.UTF8.GetBytes(code);
        byte[] buf = new byte[codeBytes.Length + 1 + Hash32.Length];
        Buffer.BlockCopy(codeBytes, 0, buf, 0, codeBytes.Length);
        buf[codeBytes.Length] = 0x1F; // unit separator
        hash.CopyTo(buf.AsSpan(codeBytes.Length + 1, Hash32.Length));
        return Hartonomous.Core.Compute.Common.Blake3.Hash32(buf);
    }

    private static Hash32 ComposeKey(string codeA, Hash32 hashA, Hash32 hashB)
    {
        byte[] codeBytes = System.Text.Encoding.UTF8.GetBytes(codeA);
        byte[] buf = new byte[codeBytes.Length + 1 + Hash32.Length + 1 + Hash32.Length];
        int o = 0;
        Buffer.BlockCopy(codeBytes, 0, buf, o, codeBytes.Length); o += codeBytes.Length;
        buf[o++] = 0x1F;
        hashA.CopyTo(buf.AsSpan(o, Hash32.Length)); o += Hash32.Length;
        buf[o++] = 0x1F;
        hashB.CopyTo(buf.AsSpan(o, Hash32.Length));
        return Hartonomous.Core.Compute.Common.Blake3.Hash32(buf);
    }

    private static Hash32 ComposeKey(string codeA, string codeB, Hash32 hash)
    {
        byte[] aBytes = System.Text.Encoding.UTF8.GetBytes(codeA);
        byte[] bBytes = System.Text.Encoding.UTF8.GetBytes(codeB);
        byte[] buf = new byte[aBytes.Length + 1 + bBytes.Length + 1 + Hash32.Length];
        int o = 0;
        Buffer.BlockCopy(aBytes, 0, buf, o, aBytes.Length); o += aBytes.Length;
        buf[o++] = 0x1F;
        Buffer.BlockCopy(bBytes, 0, buf, o, bBytes.Length); o += bBytes.Length;
        buf[o++] = 0x1F;
        hash.CopyTo(buf.AsSpan(o, Hash32.Length));
        return Hartonomous.Core.Compute.Common.Blake3.Hash32(buf);
    }

    private static Hash32 ComposeKey(Hash32 hash, int ordinal)
    {
        byte[] buf = new byte[Hash32.Length + 4];
        hash.CopyTo(buf.AsSpan(0, Hash32.Length));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(Hash32.Length, 4), ordinal);
        return Hartonomous.Core.Compute.Common.Blake3.Hash32(buf);
    }

    public async ValueTask FlushAsync(CancellationToken ct)
    {
        await FlushIngressBufferAsync(ct).ConfigureAwait(false);

        // Mark all channels complete so drain loops exit their reader loops
        // after consuming everything currently buffered.
        _entities.Writer.TryComplete();
        _entityClassifications.Writer.TryComplete();
        _edges.Writer.TryComplete();
        _edgeMembers.Writer.TryComplete();
        _junctions.Writer.TryComplete();
        _physicalities.Writer.TryComplete();
        _entitySignificances.Writer.TryComplete();
        _edgeSignificances.Writer.TryComplete();
        _entityModelSources.Writer.TryComplete();
        _edgeRatingEvents.Writer.TryComplete();

        // Wait for all drain tasks to finish their final chunks. Each drain
        // task drains its in-flight temp table after the channel closes
        // before exiting — so when WhenAll returns, every emitted record is
        // already in substrate. There is no separate catch-up drain phase.
        await Task.WhenAll(DrainTaskSnapshot()).ConfigureAwait(false);
        Log.PipelineFlushed(_logger,
            _entitiesEmitted, _entityClassificationsEmitted,
            _edgesEmitted, _edgeMembersEmitted,
            _junctionsEmitted, _physicalitiesEmitted,
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

        // FlushAsync drains channels only. Post-phase enrichment (edge trajectory
        // population and significance priming) is the phase orchestrator's
        // responsibility and must be called explicitly via
        // PopulateEdgeTrajectoriesAsync and PrimeAllSignificanceAsync.
        // Keeping them here would execute them twice — once per phase by the
        // orchestrator and again at dispose — wasting time on already-complete
        // work. Idempotency of the underlying functions makes the double-call
        // safe but not free; at Wiktionary scale each redundant pass is minutes.
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
            catch (Exception ex) when ( // BOUNDARY: transient post-pass connection failure is retried before fail-loud exhaustion.
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
                    catch (Exception ex) when ( // BOUNDARY: transient post-pass connection failure is retried before fail-loud exhaustion.
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
        catch (OperationCanceledException) { /* shutdown */ } // BOUNDARY: dispose ignores cancellation caused by pipeline shutdown.

        _shutdown.Cancel();
        try
        {
            await _periodicSnapshotTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ } // BOUNDARY: dispose ignores cancellation caused by pipeline shutdown.
        _shutdown.Dispose();
        _ingressGate.Dispose();
        _codeResolver.Dispose();
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
            writeRow: (writer, rec) =>
            {
                writer.StartRow();
                writer.Write(rec.Hash.ToByteArray(), NpgsqlDbType.Bytea);
                // Producer-side computed centroid + Hilbert. Native text decomposer
                // emits them on RecEntity records (cp_c / gc_c / w_c / comp_c
                // arrays in text_decompose.c); SubstrateTextDecomposer.OnRecord
                // surfaces them via EntityRecord. NaN sentinels mean "producer did
                // not compute" — written as NULL to the temp table.
                if (double.IsNaN(rec.CentroidX)) { writer.WriteNull(); } else { writer.Write(rec.CentroidX, NpgsqlDbType.Double); }
                if (double.IsNaN(rec.CentroidY)) { writer.WriteNull(); } else { writer.Write(rec.CentroidY, NpgsqlDbType.Double); }
                if (double.IsNaN(rec.CentroidZ)) { writer.WriteNull(); } else { writer.Write(rec.CentroidZ, NpgsqlDbType.Double); }
                if (double.IsNaN(rec.CentroidM)) { writer.WriteNull(); } else { writer.Write(rec.CentroidM, NpgsqlDbType.Double); }
                if (rec.HilbertIndex is long h) { writer.Write(h, NpgsqlDbType.Bigint); } else { writer.WriteNull(); }
                Interlocked.Increment(ref _entitiesEmitted);
                return ValueTask.CompletedTask;
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainEntityClassificationsAsync(CancellationToken ct)
    {
        DrainSqlSpec sql = IngestionSql.EntityClassification;
        Dictionary<string, int> entityTypeIds = new(StringComparer.Ordinal);
        Dictionary<string, int> provenanceIds = new(StringComparer.Ordinal);
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
                if (!entityTypeIds.TryGetValue(rec.EntityTypeCode, out int typeId))
                {
                    typeId = await _codeResolver.EntityTypeIdAsync(rec.EntityTypeCode, ct).ConfigureAwait(false);
                    entityTypeIds.Add(rec.EntityTypeCode, typeId);
                }
                if (!provenanceIds.TryGetValue(rec.ProvenanceCode, out int provenanceId))
                {
                    provenanceId = await _codeResolver.ProvenanceIdAsync(rec.ProvenanceCode, ct).ConfigureAwait(false);
                    provenanceIds.Add(rec.ProvenanceCode, provenanceId);
                }
                writer.StartRow();
                writer.Write(rec.EntityHash.ToByteArray(), NpgsqlDbType.Bytea);
                writer.Write(typeId, NpgsqlDbType.Integer);
                writer.Write(provenanceId, NpgsqlDbType.Integer);
                Interlocked.Increment(ref _entityClassificationsEmitted);
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainEdgesAsync(CancellationToken ct)
    {
        DrainSqlSpec sql = IngestionSql.Edge;
        Dictionary<string, int> edgeTypeIds = new(StringComparer.Ordinal);
        Dictionary<string, int> provenanceIds = new(StringComparer.Ordinal);
        await DrainKindAsync(
            _edges.Reader,
            tempCreate: sql.TempCreate,
            copySql: sql.Copy,
            truncateSql: sql.Truncate,
                        drainSql: sql.Drain,
            kindName: "edges",
            kindIndex: KindIndex.Edge,
            writeRow: async (writer, rec) =>
            {
                if (!edgeTypeIds.TryGetValue(rec.EdgeTypeCode, out int edgeTypeId))
                {
                    edgeTypeId = await _codeResolver.EdgeTypeIdAsync(rec.EdgeTypeCode, ct).ConfigureAwait(false);
                    edgeTypeIds.Add(rec.EdgeTypeCode, edgeTypeId);
                }
                if (!provenanceIds.TryGetValue(rec.ProvenanceCode, out int provenanceId))
                {
                    provenanceId = await _codeResolver.ProvenanceIdAsync(rec.ProvenanceCode, ct).ConfigureAwait(false);
                    provenanceIds.Add(rec.ProvenanceCode, provenanceId);
                }
                writer.StartRow();
                writer.Write(edgeTypeId, NpgsqlDbType.Integer);
                writer.Write(rec.EdgeHash.ToByteArray(), NpgsqlDbType.Bytea);
                writer.Write(provenanceId, NpgsqlDbType.Integer);
                if (rec.Geometry is null)
                {
                    writer.WriteNull();
                }
                else
                {
                    writer.Write(rec.Geometry, NpgsqlDbType.Bytea);
                }
                Interlocked.Increment(ref _edgesEmitted);
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainEdgeMembersAsync(CancellationToken ct)
    {
        DrainSqlSpec sql = IngestionSql.EdgeMember;
        Dictionary<string, int> edgeTypeIds = new(StringComparer.Ordinal);
        Dictionary<string, int> roleIds = new(StringComparer.Ordinal);
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
                if (!edgeTypeIds.TryGetValue(rec.EdgeTypeCode, out int edgeTypeId))
                {
                    edgeTypeId = await _codeResolver.EdgeTypeIdAsync(rec.EdgeTypeCode, ct).ConfigureAwait(false);
                    edgeTypeIds.Add(rec.EdgeTypeCode, edgeTypeId);
                }
                if (!roleIds.TryGetValue(rec.RoleCode, out int roleId))
                {
                    roleId = await _codeResolver.EdgeRoleIdAsync(rec.RoleCode, ct).ConfigureAwait(false);
                    roleIds.Add(rec.RoleCode, roleId);
                }
                writer.StartRow();
                writer.Write(edgeTypeId, NpgsqlDbType.Integer);
                writer.Write(rec.EdgeHash.ToByteArray(), NpgsqlDbType.Bytea);
                writer.Write(rec.EntityHash.ToByteArray(), NpgsqlDbType.Bytea);
                writer.Write(roleId, NpgsqlDbType.Integer);
                writer.Write(rec.RolePosition, NpgsqlDbType.Integer);
                Interlocked.Increment(ref _edgeMembersEmitted);
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainJunctionsAsync(CancellationToken ct)
    {
        DrainSqlSpec sql = IngestionSql.Junction;
        Dictionary<string, int> attestationTypeIds = new(StringComparer.Ordinal);
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
                if (!attestationTypeIds.TryGetValue(rec.AttestationTypeCode, out int attestationTypeId))
                {
                    attestationTypeId = await _codeResolver
                        .AttestationTypeIdAsync(rec.AttestationTypeCode, ct).ConfigureAwait(false);
                    attestationTypeIds.Add(rec.AttestationTypeCode, attestationTypeId);
                }
                writer.StartRow();
                writer.Write(rec.JunctionTable, NpgsqlDbType.Text);
                writer.Write(rec.EntityHash.ToByteArray(), NpgsqlDbType.Bytea);
                writer.Write(rec.ReferenceId, NpgsqlDbType.Integer);
                writer.Write(attestationTypeId, NpgsqlDbType.Integer);
                if (rec.Mu.HasValue)
                {
                    writer.Write(rec.Mu.Value, NpgsqlDbType.Double);
                }
                else
                {
                    writer.WriteNull();
                }
                Interlocked.Increment(ref _junctionsEmitted);
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainPhysicalitiesAsync(CancellationToken ct)
    {
        DrainSqlSpec sql = IngestionSql.Physicality;
        Dictionary<string, int> physicalityTypeIds = new(StringComparer.Ordinal);
        await DrainKindAsync(
            _physicalities.Reader,
            tempCreate: sql.TempCreate,
            copySql: sql.Copy,
            truncateSql: sql.Truncate,
                        drainSql: sql.Drain,
            kindName: "physicalities",
            kindIndex: KindIndex.Physicality,
            writeRow: async (writer, rec) =>
            {
                if (!physicalityTypeIds.TryGetValue(rec.PhysicalityTypeCode, out int physTypeId))
                {
                    physTypeId = await _codeResolver.PhysicalityTypeIdAsync(rec.PhysicalityTypeCode, ct).ConfigureAwait(false);
                    physicalityTypeIds.Add(rec.PhysicalityTypeCode, physTypeId);
                }
                writer.StartRow();
                writer.Write(physTypeId, NpgsqlDbType.Integer);
                writer.Write(rec.EntityHash.ToByteArray(), NpgsqlDbType.Bytea);
                writer.Write(rec.ContentHash.ToByteArray(), NpgsqlDbType.Bytea);
                writer.Write(rec.Geometry, NpgsqlDbType.Bytea);
                Interlocked.Increment(ref _physicalitiesEmitted);
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainEntitySignificancesAsync(CancellationToken ct)
    {
        DrainSqlSpec sql = IngestionSql.EntitySignificance;
        Dictionary<string, int> contextIds = new(StringComparer.Ordinal);
        Dictionary<string, int> attestationTypeIds = new(StringComparer.Ordinal);
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
                if (!contextIds.TryGetValue(rec.ContextTypeCode, out int contextId))
                {
                    contextId = await _codeResolver.SignificanceContextIdAsync(rec.ContextTypeCode, ct).ConfigureAwait(false);
                    contextIds.Add(rec.ContextTypeCode, contextId);
                }
                if (!attestationTypeIds.TryGetValue(rec.AttestationTypeCode, out int attestationTypeId))
                {
                    attestationTypeId = await _codeResolver.AttestationTypeIdAsync(rec.AttestationTypeCode, ct).ConfigureAwait(false);
                    attestationTypeIds.Add(rec.AttestationTypeCode, attestationTypeId);
                }
                writer.StartRow();
                writer.Write(contextId, NpgsqlDbType.Integer);
                writer.Write(rec.EntityHash.ToByteArray(), NpgsqlDbType.Bytea);
                writer.Write(attestationTypeId, NpgsqlDbType.Integer);
                writer.Write(rec.InitialMu, NpgsqlDbType.Double);
                Interlocked.Increment(ref _entitySignificancesEmitted);
            },
            ct).ConfigureAwait(false);
    }

    private async Task DrainEdgeSignificancesAsync(CancellationToken ct)
    {
        DrainSqlSpec sql = IngestionSql.EdgeSignificance;
        Dictionary<string, int> contextIds = new(StringComparer.Ordinal);
        Dictionary<string, int> edgeTypeIds = new(StringComparer.Ordinal);
        Dictionary<string, int> attestationTypeIds = new(StringComparer.Ordinal);
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
                if (!contextIds.TryGetValue(rec.ContextTypeCode, out int contextId))
                {
                    contextId = await _codeResolver.SignificanceContextIdAsync(rec.ContextTypeCode, ct).ConfigureAwait(false);
                    contextIds.Add(rec.ContextTypeCode, contextId);
                }
                if (!edgeTypeIds.TryGetValue(rec.EdgeTypeCode, out int edgeTypeId))
                {
                    edgeTypeId = await _codeResolver.EdgeTypeIdAsync(rec.EdgeTypeCode, ct).ConfigureAwait(false);
                    edgeTypeIds.Add(rec.EdgeTypeCode, edgeTypeId);
                }
                if (!attestationTypeIds.TryGetValue(rec.AttestationTypeCode, out int attestationTypeId))
                {
                    attestationTypeId = await _codeResolver.AttestationTypeIdAsync(rec.AttestationTypeCode, ct).ConfigureAwait(false);
                    attestationTypeIds.Add(rec.AttestationTypeCode, attestationTypeId);
                }
                writer.StartRow();
                writer.Write(contextId, NpgsqlDbType.Integer);
                writer.Write(edgeTypeId, NpgsqlDbType.Integer);
                writer.Write(rec.EdgeHash.ToByteArray(), NpgsqlDbType.Bytea);
                writer.Write(attestationTypeId, NpgsqlDbType.Integer);
                writer.Write(rec.InitialMu, NpgsqlDbType.Double);
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
            writeRow: (writer, rec) =>
            {
                writer.StartRow();
                writer.Write(rec.EntityHash.ToByteArray(), NpgsqlDbType.Bytea);
                writer.Write((int)rec.ModelSourceId, NpgsqlDbType.Integer);
                Interlocked.Increment(ref _entityModelSourcesEmitted);
                return ValueTask.CompletedTask;
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sign-bearing rating-event drain. Distinct from the COPY-into-temp
    /// shape every other drain uses: rating events are NOT row inserts;
    /// they are calls to substrate.record_attestations_bulk per
    /// (arena, attestation_type) bucket. The drain reads events from the
    /// channel, buffers up to <see cref="RatingEventBatchSize"/> per
    /// bucket, and flushes each bucket via ONE bulk-function call (per
    /// AP-2: no RBAR; the SQL function is itself ONE glicko2_bulk_update
    /// call across the entire bucket's events).
    ///
    /// No producer-side dedup: every event is a fresh observation. Two
    /// emissions of the same (arena, edge, attestation_type) MUST produce
    /// two Glicko events (cross-source corroboration). The dedup pattern
    /// other channels use is wrong for this kind of record.
    ///
    /// Per docs/01-tensor-primitive-spec.md §V and AP-31.
    /// </summary>
    private const int RatingEventBatchSize = 16_384;

    private async Task DrainEdgeRatingEventsAsync(CancellationToken ct)
    {
        try
        {
            await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

            // Per-bucket buffers keyed by (arena_id, attestation_type_id).
            Dictionary<(int Arena, int Atest), List<EdgeRatingEventRecord>> buckets = new();
            Dictionary<string, int> edgeTypeIds = new(StringComparer.Ordinal);
            Dictionary<string, int> contextIds = new(StringComparer.Ordinal);
            Dictionary<string, int> attestationTypeIds = new(StringComparer.Ordinal);

            async ValueTask InsertSafetensorObservationsAsync(
                List<EdgeRatingEventRecord> batch,
                int contextTypeId,
                int attestationTypeId,
                Dictionary<string, int> edgeTypeIdCache,
                CancellationToken cancellationToken)
            {
                List<EdgeRatingEventRecord> observations = new(batch.Count);
                foreach (EdgeRatingEventRecord r in batch)
                {
                    if (r.ModelSourceId.HasValue
                        || r.TensorHash.HasValue
                        || r.PackageTensorHash.HasValue
                        || !string.IsNullOrEmpty(r.TupleCode))
                    {
                        observations.Add(r);
                    }
                }
                if (observations.Count == 0)
                {
                    return;
                }

                int n = observations.Count;
                int?[] modelSourceIds = new int?[n];
                int[] contextTypeIds = new int[n];
                int[] attestationTypeIdsArr = new int[n];
                int[] edgeTypeIdsArr = new int[n];
                byte[][] edgeHashes = new byte[n][];
                double[] scores = new double[n];
                double[] weights = new double[n];
                byte[]?[] tensorHashes = new byte[]?[n];
                byte[]?[] packageTensorHashes = new byte[]?[n];
                string?[] sourceTensorNames = new string?[n];
                string?[] primitiveCodes = new string?[n];
                string?[] tupleCodes = new string?[n];
                string?[] slotCodes = new string?[n];
                string?[] modalityCodes = new string?[n];
                int?[] layerIndexes = new int?[n];
                int?[] headIndexes = new int?[n];
                int?[] expertIndexes = new int?[n];
                string?[] adapterNames = new string?[n];
                string?[] fusedSlices = new string?[n];

                for (int i = 0; i < n; i++)
                {
                    EdgeRatingEventRecord r = observations[i];
                    if (!edgeTypeIdCache.TryGetValue(r.EdgeTypeCode, out int edgeTypeId))
                    {
                        edgeTypeId = await _codeResolver.EdgeTypeIdAsync(r.EdgeTypeCode, cancellationToken).ConfigureAwait(false);
                        edgeTypeIdCache.Add(r.EdgeTypeCode, edgeTypeId);
                    }
                    modelSourceIds[i] = r.ModelSourceId.HasValue ? checked((int)r.ModelSourceId.Value) : null;
                    contextTypeIds[i] = contextTypeId;
                    attestationTypeIdsArr[i] = attestationTypeId;
                    edgeTypeIdsArr[i] = edgeTypeId;
                    edgeHashes[i] = r.EdgeHash.ToByteArray();
                    scores[i] = r.Score;
                    weights[i] = r.Weight;
                    tensorHashes[i] = r.TensorHash?.ToByteArray();
                    packageTensorHashes[i] = r.PackageTensorHash?.ToByteArray();
                    sourceTensorNames[i] = r.SourceTensorName;
                    primitiveCodes[i] = r.PrimitiveCode;
                    tupleCodes[i] = r.TupleCode;
                    slotCodes[i] = r.SlotCode;
                    modalityCodes[i] = r.ModalityCode;
                    layerIndexes[i] = r.LayerIndex;
                    headIndexes[i] = r.HeadIndex;
                    expertIndexes[i] = r.ExpertIndex;
                    adapterNames[i] = r.AdapterName;
                    fusedSlices[i] = r.FusedSlice;
                }

                await using NpgsqlCommand obsCmd = new(IngestionSql.InsertSafetensorObservations, conn);
                obsCmd.Parameters.Add(new NpgsqlParameter("model_source_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = modelSourceIds });
                obsCmd.Parameters.Add(new NpgsqlParameter("context_type_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = contextTypeIds });
                obsCmd.Parameters.Add(new NpgsqlParameter("attestation_type_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = attestationTypeIdsArr });
                obsCmd.Parameters.Add(new NpgsqlParameter("edge_type_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = edgeTypeIdsArr });
                obsCmd.Parameters.Add(new NpgsqlParameter("edge_hashes", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = edgeHashes });
                obsCmd.Parameters.Add(new NpgsqlParameter("scores", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = scores });
                obsCmd.Parameters.Add(new NpgsqlParameter("weights", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = weights });
                obsCmd.Parameters.Add(new NpgsqlParameter("tensor_hashes", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = tensorHashes });
                obsCmd.Parameters.Add(new NpgsqlParameter("package_tensor_hashes", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = packageTensorHashes });
                obsCmd.Parameters.Add(new NpgsqlParameter("source_tensor_names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = sourceTensorNames });
                obsCmd.Parameters.Add(new NpgsqlParameter("primitive_codes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = primitiveCodes });
                obsCmd.Parameters.Add(new NpgsqlParameter("tuple_codes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = tupleCodes });
                obsCmd.Parameters.Add(new NpgsqlParameter("slot_codes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = slotCodes });
                obsCmd.Parameters.Add(new NpgsqlParameter("modality_codes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = modalityCodes });
                obsCmd.Parameters.Add(new NpgsqlParameter("layer_indexes", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = layerIndexes });
                obsCmd.Parameters.Add(new NpgsqlParameter("head_indexes", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = headIndexes });
                obsCmd.Parameters.Add(new NpgsqlParameter("expert_indexes", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = expertIndexes });
                obsCmd.Parameters.Add(new NpgsqlParameter("adapter_names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = adapterNames });
                obsCmd.Parameters.Add(new NpgsqlParameter("fused_slices", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = fusedSlices });
                await obsCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            async ValueTask FlushBucketAsync((int Arena, int Atest) key, List<EdgeRatingEventRecord> batch)
            {
                if (batch.Count == 0) { return; }
                int n = batch.Count;
                int[] etypeIds = new int[n];
                byte[][] hashes = new byte[n][];
                double[] scores = new double[n];
                double[] weights = new double[n];
                for (int i = 0; i < n; i++)
                {
                    EdgeRatingEventRecord r = batch[i];
                    if (!edgeTypeIds.TryGetValue(r.EdgeTypeCode, out int edgeTypeId))
                    {
                        edgeTypeId = await _codeResolver.EdgeTypeIdAsync(r.EdgeTypeCode, ct).ConfigureAwait(false);
                        edgeTypeIds.Add(r.EdgeTypeCode, edgeTypeId);
                    }
                    etypeIds[i] = edgeTypeId;
                    hashes[i]   = r.EdgeHash.ToByteArray();
                    scores[i]   = r.Score;
                    weights[i]  = r.Weight;
                }
                long start = Stopwatch.GetTimestamp();
                await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
                    conn,
                    SubstrateFunctionNames.RecordAttestationsBulk,
                    new[]
                    {
                        new NpgsqlParameter { Value = key.Arena, NpgsqlDbType = NpgsqlDbType.Integer },
                        new NpgsqlParameter { Value = key.Atest, NpgsqlDbType = NpgsqlDbType.Integer },
                        new NpgsqlParameter { Value = etypeIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer },
                        new NpgsqlParameter { Value = hashes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea },
                        new NpgsqlParameter { Value = scores, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double },
                        new NpgsqlParameter { Value = weights, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double },
                    });
                cmd.CommandTimeout = 0;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await InsertSafetensorObservationsAsync(batch, key.Arena, key.Atest, edgeTypeIds, ct).ConfigureAwait(false);
                Interlocked.Add(ref _drainElapsedTicks[KindIndex.EdgeRatingEvent], Stopwatch.GetTimestamp() - start);
                Interlocked.Add(ref _drainRowsCommitted[KindIndex.EdgeRatingEvent], n);
                Interlocked.Add(ref _edgeRatingEventsEmitted, n);
                Interlocked.Increment(ref _copyCommits);
                batch.Clear();
            }

            async ValueTask ReadEventAsync(EdgeRatingEventRecord rec)
            {
                if (!contextIds.TryGetValue(rec.ContextTypeCode, out int arenaId))
                {
                    arenaId = await _codeResolver.SignificanceContextIdAsync(rec.ContextTypeCode, ct).ConfigureAwait(false);
                    contextIds.Add(rec.ContextTypeCode, arenaId);
                }
                if (!attestationTypeIds.TryGetValue(rec.AttestationTypeCode, out int atestId))
                {
                    atestId = await _codeResolver.AttestationTypeIdAsync(rec.AttestationTypeCode, ct).ConfigureAwait(false);
                    attestationTypeIds.Add(rec.AttestationTypeCode, atestId);
                }
                (int, int) key = (arenaId, atestId);
                if (!buckets.TryGetValue(key, out List<EdgeRatingEventRecord>? list))
                {
                    list = new List<EdgeRatingEventRecord>(RatingEventBatchSize);
                    buckets[key] = list;
                }
                list.Add(rec);
                if (list.Count >= RatingEventBatchSize)
                {
                    await FlushBucketAsync(key, list).ConfigureAwait(false);
                }
            }

            async ValueTask FlushAllBucketsAsync()
            {
                foreach (KeyValuePair<(int, int), List<EdgeRatingEventRecord>> kv in buckets)
                {
                    await FlushBucketAsync(kv.Key, kv.Value).ConfigureAwait(false);
                }
            }

            while (await _edgeRatingEvents.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_edgeRatingEvents.Reader.TryRead(out EdgeRatingEventRecord? rec))
                {
                    await ReadEventAsync(rec).ConfigureAwait(false);
                }

                await FlushAllBucketsAsync().ConfigureAwait(false);
            }

            await FlushAllBucketsAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) // BOUNDARY: pipeline shutdown.
        {
        }
        catch (Exception ex) // BOUNDARY: drain task failure must trip the writer-fail circuit so producers don't block.
        {
            Interlocked.Increment(ref _copyErrors);
            Log.DrainTaskCrashed(_logger, "edge_rating_events", ex);
            FailAllWriters(ex);
            throw;
        }
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
            // 32k-row geometry4d chunks (each LINESTRING4D vertex is 32B;
            // a single physicality chunk can exceed 8MB and
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
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested) // BOUNDARY: idle flush timeout drains partial COPY chunk.
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
                    catch { /* importer may already be in failed state */ } // BOUNDARY: cleanup after importer failure must preserve original exception.
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) // BOUNDARY: drain task exits on requested pipeline shutdown.
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
            try { _shutdown.Cancel(); } catch { /* already disposed */ } // BOUNDARY: fail-fast cancellation may race pipeline disposal.
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
            Message = "Pipeline flushed: entities={Entities} classifications={Classifications} edges={Edges} edge_members={EdgeMembers} junctions={Junctions} physicalities={Physicalities} entity_sigs={EntitySigs} edge_sigs={EdgeSigs} model_sources={ModelSources} commits={Commits} errors={Errors}")]
        public static partial void PipelineFlushed(ILogger logger,
            long entities, long classifications, long edges, long edgeMembers, long junctions,
            long physicalities, long entitySigs, long edgeSigs, long modelSources,
            long commits, long errors);

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
            Message = "Pipeline state: t={PhaseElapsed} state={State} buffered_for_diff={BufferedRows} submitted={SubmittedRows} drained={DrainedRows} backlog={BacklogRows} producer_dedup_hits={DedupHits}")]
        public static partial void PipelineState(ILogger logger, TimeSpan phaseElapsed, string state, long bufferedRows,
            long submittedRows, long drainedRows, long backlogRows, long dedupHits);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Pipeline live: t={PhaseElapsed} kind={Kind} state={State} submitted={SubmittedRows} drained={DrainedRows} backlog={BacklogRows} drain={DrainElapsed} producerWait={WaitElapsed} rate={RowsPerSec:F0} rows/s")]
        public static partial void LiveSnapshot(ILogger logger, TimeSpan phaseElapsed, string kind, string state,
            long submittedRows, long drainedRows, long backlogRows,
            TimeSpan drainElapsed, TimeSpan waitElapsed, double rowsPerSec);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Pipeline resources: t={PhaseElapsed} processCpu={ProcessCpuPercent:F1}% processCores={ProcessCpuCores:F2} systemCpu={SystemCpuPercent:F1}% ioWait={SystemIoWaitPercent:F1}% rss={WorkingSetMib:F0}MiB heap={HeapMib:F0}MiB threadpool={BusyWorkers}/{MaxWorkers} memAvail={MemAvailableMib:F0}/{MemTotalMib:F0}MiB swapFree={SwapFreeMib:F0}/{SwapTotalMib:F0}MiB procRead={ReadMibPerSec:F1}MiB/s procWrite={WriteMibPerSec:F1}MiB/s pgDiskFree={PostgresFreeGib:F1}/{PostgresTotalGib:F1}GiB rootFree={RootFreeGib:F1}/{RootTotalGib:F1}GiB")]
        public static partial void ResourceSnapshot(
            ILogger logger,
            TimeSpan phaseElapsed,
            double processCpuPercent,
            double processCpuCores,
            double systemCpuPercent,
            double systemIoWaitPercent,
            double workingSetMib,
            double heapMib,
            int busyWorkers,
            int maxWorkers,
            double memAvailableMib,
            double memTotalMib,
            double swapFreeMib,
            double swapTotalMib,
            double readMibPerSec,
            double writeMibPerSec,
            double postgresFreeGib,
            double postgresTotalGib,
            double rootFreeGib,
            double rootTotalGib);

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
