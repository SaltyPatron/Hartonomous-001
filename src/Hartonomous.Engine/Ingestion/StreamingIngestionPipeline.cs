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
using Hartonomous.Core.Geometry;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// Bundled-emit streaming ingestion pipeline. N partitioned worker threads
/// (N = ProcessorCount/2, clamped [4,16]) each own one
/// <c>Channel&lt;RecordBundle&gt;</c>, one long-lived <see cref="NpgsqlConnection"/>,
/// one set of pg_temp.X_inflight tables, one dedup HashSet, and one
/// ID-resolution cache loaded at startup. Bundles route to workers by
/// hash-prefix of the bundle's leader entity — deterministic (Law #6) and
/// contention-free.
///
/// <para>
/// Each worker's <c>DrainBundleAsync</c> loop accumulates bundles into a
/// chunk (capped at <see cref="CopyChunkRows"/> total records or
/// <see cref="IdleFlushAfter"/>), opens a transaction, COPYs each kind's
/// records to pg_temp.X_inflight in dependency order
/// (entity → entity_classification → physicality → edge → edge_member →
/// edge_significance → entity_significance → entity_model_source → junction →
/// edge_rating_event), runs the INSERT-SELECT chain, commits. Edge geometry
/// is built inline at bundle emit from participant identity-POINTZMs;
/// per-arena edge_significance priors are emitted inline at edge emit by
/// cross-producting the edge against every arena currently in
/// substrate.significance_context (AP-1 compliant, open vocabulary).
/// </para>
///
/// <para>
/// There is NO end-of-phase post-pass. No populate_edge_trajectories. No
/// prime_unprimed_edges_chunk. No arena_priming_state. Every
/// <see cref="DrainPendingAsync"/> returns with edge.geom populated and
/// per-arena significance primed, because both happen inline at edge emit.
/// The substrate is continuously queryable; phases are an orchestration
/// convenience, NOT a substrate boundary (AP-37).
/// </para>
/// </summary>
public sealed partial class StreamingIngestionPipeline : IRecordSink, IIngestionPipeline, IAsyncDisposable
{
    private const string SourceAuthorityContext = "source_authority";
    private const string ProvenanceAuthorityAttestation = "positive_evidence";
    private const double ProvenanceAuthorityEventWeight = 0.8;

    /// <summary>
    /// Bundles in flight per worker channel. Each bundle is one source-unit's
    /// worth of records (typically 5–50). 8192 bundles ≈ low-MB per-channel
    /// memory cap regardless of bundle size; bundle producers backpressure
    /// naturally when full.
    /// </summary>
    private const int BundleChannelCapacity = 8192;

    /// <summary>
    /// Per-worker COPY chunk threshold in total-record count. The worker
    /// accumulates whole bundles until the sum of their record counts crosses
    /// this threshold, then commits the chunk in one transaction. Larger
    /// chunks amortize COPY overhead better; smaller chunks reduce crash
    /// blast radius.
    /// </summary>
    private const int CopyChunkRows = 32_768;

    /// <summary>
    /// Idle timeout per worker. When the worker has bundles buffered but the
    /// channel hasn't produced any new ones for this long, commit the partial
    /// chunk so producers see records persisted with bounded latency.
    /// </summary>
    private static readonly TimeSpan IdleFlushAfter = TimeSpan.FromMilliseconds(250);

    private readonly NpgsqlDataSource _dataSource;
    private readonly CodeResolver _codeResolver;
    private readonly ILogger<StreamingIngestionPipeline> _logger;
    private readonly CancellationTokenSource _shutdown = new();

    // Worker partitioning. N channels keyed by hash-prefix of bundle leader;
    // each worker has 1:1 channel ownership so no inter-worker contention.
    private readonly int _workerCount;
    private readonly Channel<RecordBundle>[] _workerChannels;
    private readonly Task[] _workerTasks;
    private readonly long[] _workerBundlesSubmitted;
    private readonly long[] _workerBundlesDrained;
    private readonly long[] _workerRecordsSubmitted;
    private readonly long[] _workerRecordsDrained;
    private readonly long[] _workerCommits;
    private readonly long[] _workerErrors;

    // Inline edge-significance priming cache. Precomputed once at pipeline
    // startup: every arena currently in substrate.significance_context
    // cross-producted against every provenance × edge_type combination, with
    // initial_mu = provenance.initial_mu × edge_type.semantic_weight ×
    // provenance.derivation_decay. Edge emission consults this to insert one
    // EdgeSignificanceRecord per (edge, arena) tuple — no end-of-phase
    // priming pass.
    private InlinePrimerTable? _primerTable;
    private readonly SemaphoreSlim _primerInit = new(1, 1);

    // Per-record-kind emit counters (best-effort, for telemetry only).
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

        _workerCount = ComputeWorkerCount();
        _workerChannels = new Channel<RecordBundle>[_workerCount];
        _workerTasks = new Task[_workerCount];
        _workerBundlesSubmitted = new long[_workerCount];
        _workerBundlesDrained = new long[_workerCount];
        _workerRecordsSubmitted = new long[_workerCount];
        _workerRecordsDrained = new long[_workerCount];
        _workerCommits = new long[_workerCount];
        _workerErrors = new long[_workerCount];

        BoundedChannelOptions opts = new(BundleChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        };

        for (int i = 0; i < _workerCount; i++)
        {
            _workerChannels[i] = Channel.CreateBounded<RecordBundle>(opts);
        }

        // Worker tasks start eagerly. Each owns its own connection + temp
        // tables + dedup state — disjoint by construction, no shared mutable
        // state across workers.
        for (int i = 0; i < _workerCount; i++)
        {
            int workerId = i;
            _workerTasks[i] = Task.Run(() => RunWorkerAsync(workerId, _shutdown.Token));
        }
    }

    /// <summary>
    /// N = ProcessorCount/2 clamped [4, 16]. Leaves headroom for PG backends
    /// (default max_connections=50; N workers + per-worker existence-probe
    /// connections + reference-data + user queries must fit comfortably).
    /// </summary>
    private static int ComputeWorkerCount()
    {
        int n = Math.Max(1, Environment.ProcessorCount) / 2;
        if (n < 4) { n = 4; }
        if (n > 16) { n = 16; }
        return n;
    }

    // ── Public surfaces (IIngestionPipeline + IRecordSink) ──────────────────

    public IIngestionBatch CreateBatch(string provenanceCode) => new IngestionBatch(provenanceCode);

    public IIngestionBatch CreateBatch() => new IngestionBatch("system_computed");

    public async Task SubmitBatchAsync(IIngestionBatch batch, CancellationToken ct)
    {
        if (batch is not IngestionBatch b)
        {
            throw new ArgumentException("Batch must be created by this pipeline.", nameof(batch));
        }

        await EnsurePrimerTableAsync(ct).ConfigureAwait(false);

        List<IngestionRecord> records = await MaterializeBatchRecordsAsync(b, ct).ConfigureAwait(false);
        if (records.Count == 0)
        {
            return;
        }

        Hash32 leader = FindLeaderHash(records);
        RecordBundle bundle = new(leader, records);
        await DispatchBundleAsync(bundle, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Direct <see cref="IRecordSink.EmitAsync"/> ingress for decomposers
    /// using the per-record streaming surface (BaseDecomposer.EmitEntityAsync
    /// etc.). Wraps the singleton record as a one-record bundle. Higher-volume
    /// decomposers use <see cref="SubmitBatchAsync"/> which bundles multiple
    /// records under one source-unit's leader hash.
    /// </summary>
    public async ValueTask EmitAsync(IngestionRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        await EnsurePrimerTableAsync(ct).ConfigureAwait(false);

        // CompositionChildRecord is metadata; on the bundled-emit path the
        // composition LINESTRINGZM geometry is built from b.CompositionChildren
        // inside MaterializeBatchRecordsAsync — these singleton emissions are
        // dropped here, mirroring the prior pipeline's behavior. Real
        // composition trajectories ride on PhysicalityRecord whose Geometry
        // payload is already a mantissa-packed LINESTRINGZM.
        if (record is CompositionChildRecord)
        {
            return;
        }

        Hash32 leader = ExtractLeaderHash(record);
        // For singleton EmitAsync flows we still cross-product an edge against
        // arenas if a primer table is available — preserves AP-1 inline-prime
        // semantics on the legacy emit path.
        if (record is EdgeRecord edge && _primerTable is { } table)
        {
            List<IngestionRecord> withPriors = new(1 + table.Count);
            withPriors.Add(record);
            string? provCode = await TryProvenanceForEdgeAsync(edge, ct).ConfigureAwait(false);
            if (provCode is not null)
            {
                foreach (InlinePrimerEntry e in table.For(provCode, edge.EdgeTypeCode))
                {
                    withPriors.Add(new EdgeSignificanceRecord(
                        e.ArenaCode, ProvenanceAuthorityAttestation,
                        edge.EdgeTypeCode, edge.EdgeHash, e.InitialMu));
                }
            }
            RecordBundle bundle = new(leader, withPriors);
            await DispatchBundleAsync(bundle, ct).ConfigureAwait(false);
            return;
        }

        RecordBundle singleton = new(leader, new[] { record });
        await DispatchBundleAsync(singleton, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Force-flush all bundles currently buffered in worker channels into
    /// substrate. Idempotent; safe to call multiple times. Does NOT close the
    /// channels — pipeline remains open to further emissions.
    /// </summary>
    public ValueTask FlushAsync(CancellationToken ct) => new(DrainPendingAsync(ct));

    public async Task DrainPendingAsync(CancellationToken ct)
    {
        // Snapshot the per-worker submitted counts, then wait until each
        // worker's drained count reaches its snapshot value.
        long[] targets = new long[_workerCount];
        for (int i = 0; i < _workerCount; i++)
        {
            targets[i] = Interlocked.Read(ref _workerBundlesSubmitted[i]);
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Surface worker exceptions immediately — a dead worker means
            // bundles will never drain.
            for (int i = 0; i < _workerCount; i++)
            {
                if (_workerTasks[i].IsFaulted)
                {
                    await Task.WhenAll(_workerTasks).ConfigureAwait(false);
                }
            }

            bool allDrained = true;
            for (int i = 0; i < _workerCount; i++)
            {
                if (Interlocked.Read(ref _workerBundlesDrained[i]) < targets[i])
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

    PipelineStats IIngestionPipeline.Stats => new()
    {
        EntitiesSubmitted = Interlocked.Read(ref _entitiesEmitted),
        EdgesSubmitted = Interlocked.Read(ref _edgesEmitted),
        JunctionsSubmitted = Interlocked.Read(ref _junctionsEmitted),
        PhysicalitiesSubmitted = Interlocked.Read(ref _physicalitiesEmitted),
        SignificanceInitialized = Interlocked.Read(ref _entitySignificancesEmitted),
        EntityModelSourcesLinked = Interlocked.Read(ref _entityModelSourcesEmitted),
        BatchesCommitted = SumLong(_workerCommits),
        BatchesFailed = SumLong(_workerErrors),
        TotalCommitTime = TimeSpan.Zero,
    };

    public StreamingPipelineStats Stats => new()
    {
        EntitiesEmitted = Interlocked.Read(ref _entitiesEmitted),
        EntityClassificationsEmitted = Interlocked.Read(ref _entityClassificationsEmitted),
        EdgesEmitted = Interlocked.Read(ref _edgesEmitted),
        EdgeMembersEmitted = Interlocked.Read(ref _edgeMembersEmitted),
        JunctionsEmitted = Interlocked.Read(ref _junctionsEmitted),
        PhysicalitiesEmitted = Interlocked.Read(ref _physicalitiesEmitted),
        EntitySignificancesEmitted = Interlocked.Read(ref _entitySignificancesEmitted),
        EdgeSignificancesEmitted = Interlocked.Read(ref _edgeSignificancesEmitted),
        EntityModelSourcesEmitted = Interlocked.Read(ref _entityModelSourcesEmitted),
        CopyCommits = SumLong(_workerCommits),
        CopyErrors = SumLong(_workerErrors),
    };

    private static long SumLong(long[] arr)
    {
        long sum = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            sum += Interlocked.Read(ref arr[i]);
        }
        return sum;
    }

    public async ValueTask DisposeAsync()
    {
        // Mark every worker channel complete so workers exit their read
        // loops after consuming everything currently buffered.
        for (int i = 0; i < _workerCount; i++)
        {
            _workerChannels[i].Writer.TryComplete();
        }

        // Wait for every worker to drain its final chunk before tearing down
        // the data source. Worker faults propagate.
        try
        {
            await Task.WhenAll(_workerTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ } // BOUNDARY: pipeline disposal ignores cancellation from shutdown propagation.

        _shutdown.Cancel();
        _shutdown.Dispose();
        _primerInit.Dispose();
        _codeResolver.Dispose();
        await _dataSource.DisposeAsync().ConfigureAwait(false);
    }

    // ── Bundle dispatch ─────────────────────────────────────────────────────

    /// <summary>
    /// Hash-prefix partition assignment. Same hash → same worker; deterministic
    /// across runs (Law #6).
    /// </summary>
    private int PartitionFor(Hash32 leader)
    {
        // Take the first 8 bytes of the BLAKE3 prefix; treat as unsigned 64
        // then mod N. Sufficient entropy at any sane N. Hash32.BitsLow52()
        // returns the bottom 52 bits of the first 8 bytes; right-shift to
        // upper bits to maximize partition spread.
        Span<byte> bytes = stackalloc byte[Hash32.Length];
        leader.CopyTo(bytes);
        ulong prefix = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return (int)(prefix % (ulong)_workerCount);
    }

    private async ValueTask DispatchBundleAsync(RecordBundle bundle, CancellationToken ct)
    {
        int partition = PartitionFor(bundle.LeaderHash);
        await _workerChannels[partition].Writer.WriteAsync(bundle, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _workerBundlesSubmitted[partition]);
        Interlocked.Add(ref _workerRecordsSubmitted[partition], bundle.Records.Count);
    }

    /// <summary>
    /// The bundle's leader is the first hash-bearing record; the partition
    /// key derives from it deterministically. Order: entity → edge →
    /// edge_member → physicality → … — pick the strongest identity first so
    /// related bundles land on the same worker.
    /// </summary>
#pragma warning disable CA1859 // signature is fine; List is passed in practice but the contract is the interface.
    private static Hash32 FindLeaderHash(IReadOnlyList<IngestionRecord> records)
#pragma warning restore CA1859
    {
        for (int i = 0; i < records.Count; i++)
        {
            if (records[i] is EntityRecord e) { return e.Hash; }
        }
        for (int i = 0; i < records.Count; i++)
        {
            if (records[i] is EntityClassificationRecord ec) { return ec.EntityHash; }
        }
        for (int i = 0; i < records.Count; i++)
        {
            if (records[i] is EdgeRecord ed) { return ed.EdgeHash; }
        }
        return records.Count > 0 ? ExtractLeaderHash(records[0]) : default;
    }

    private static Hash32 ExtractLeaderHash(IngestionRecord r) => r switch
    {
        EntityRecord e => e.Hash,
        EntityClassificationRecord ec => ec.EntityHash,
        EdgeRecord ed => ed.EdgeHash,
        EdgeMemberRecord em => em.EdgeHash,
        EdgeSignificanceRecord es => es.EdgeHash,
        EntitySignificanceRecord esig => esig.EntityHash,
        EntityModelSourceRecord ems => ems.EntityHash,
        PhysicalityRecord p => p.EntityHash,
        JunctionRecord j => j.EntityHash,
        EdgeRatingEventRecord ev => ev.EdgeHash,
        CompositionChildRecord cc => cc.ParentEntityHash,
        _ => default,
    };

    // ── Bundle materialization (IIngestionBatch → IngestionRecord list) ─────

    private async ValueTask<List<IngestionRecord>> MaterializeBatchRecordsAsync(
        IngestionBatch b,
        CancellationToken ct)
    {
        InlinePrimerTable primer = await EnsurePrimerTableAsync(ct).ConfigureAwait(false);

        int estimatedCount =
            b.Entities.Count
            + b.Physicalities.Count
            + b.Junctions.Count
            + b.Significances.Count
            + b.EntityModelSources.Count;
        // Per-edge contribution bounds the inline-primer fan-out by the
        // per-(provenance, edge_type) arena count, not the total cross-
        // product (which can be ~63 provenances × 133 edge_types × 19 arenas
        // ≈ 160K entries and overflows Int32 when multiplied by edges.Count).
        // This is a List<> capacity hint only — under-estimating just defers
        // a regrowth; over-estimating burns memory.
        int perEdgeArenaCap = Math.Min(primer.MaxPerPair, 64);
        foreach (EdgeEntry edge in b.Edges)
        {
            estimatedCount += 2
                + edge.Members.Length
                + edge.SignificanceOverrides.Length
                + edge.RatingEvents.Length
                + perEdgeArenaCap;
        }
        if (estimatedCount < 0)
        {
            estimatedCount = b.Entities.Count + b.Edges.Count * 4;
        }

        List<IngestionRecord> records = new(estimatedCount);
        string batchProvenance = b.ProvenanceCode;

        Dictionary<Hash32, (Hash32[] ChildHashes, int[] OrdinalStarts, int[] RleCounts)> compositionMetadata =
            BuildCompositionMetadata(b.CompositionChildren);
        HashSet<Hash32> parentsWithPhysicality = new();

        foreach (EntityEntry e in b.Entities)
        {
            records.Add(new EntityRecord(e.EntityTypeCode, e.Hash, batchProvenance));
        }

        foreach (PhysicalityEntry p in b.Physicalities)
        {
            byte[] geometry;
            if (compositionMetadata.TryGetValue(p.Entity.Hash, out var meta))
            {
                geometry = BuildCompositionGeometry(meta.ChildHashes, meta.OrdinalStarts, meta.RleCounts);
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
            records.Add(new PhysicalityRecord(p.PhysicalityTypeCode, p.Entity.Hash, contentHash, geometry));
            parentsWithPhysicality.Add(p.Entity.Hash);
        }

        Dictionary<Hash32, string> parentTypeCodes = new();
        foreach (CompositionChildEntry cc in b.CompositionChildren)
        {
            parentTypeCodes[cc.Parent.Hash] = cc.Parent.EntityTypeCode;
        }

        foreach (var pair in compositionMetadata)
        {
            if (parentsWithPhysicality.Contains(pair.Key)) { continue; }
            byte[] geometry = BuildCompositionGeometry(
                pair.Value.ChildHashes, pair.Value.OrdinalStarts, pair.Value.RleCounts);
            Hash32 contentHash = ComputePhysicalityContentHash(geometry);
            string fallbackType = parentTypeCodes.TryGetValue(pair.Key, out string? tc)
                ? Hartonomous.Core.Text.SubstrateTextDecomposer.PhysicalityCodeFor(tc)
                : "entity";
            records.Add(new PhysicalityRecord(fallbackType, pair.Key, contentHash, geometry));
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

            // Inline LINESTRINGZM geometry from participant identity-POINTZMs
            // in role order. No cross-batch fallback — every edge gets geom
            // at INSERT time, no NULL window, no post-pass backfill.
            byte[]? inlineGeometry = null;
            if (sorted.Length >= 2)
            {
                Point4D[] verts = new Point4D[sorted.Length];
                for (int j = 0; j < sorted.Length; j++)
                {
                    verts[j] = IdentityPoint4D(sorted[j].Entity.Hash, j + 1);
                }
                inlineGeometry = Geometry4dPayloadBuilder.LineString(verts);
            }

            records.Add(new EdgeRecord(edge.EdgeTypeCode, edgeHash, edge.ProvenanceCode, inlineGeometry));
            for (int j = 0; j < sorted.Length; j++)
            {
                records.Add(new EdgeMemberRecord(
                    edge.EdgeTypeCode, edgeHash,
                    sorted[j].Entity.Hash, sorted[j].RoleCode, sorted[j].Position));
            }

            // Per-edge edge_significance priors. Producer overrides emit
            // explicit rows. The (provenance × edge_type × arena) primer
            // table's full cross-product fan-out is DEFERRED: at full UCD
            // ingest scale (1.1M codepoints × ~10 edges × 19 arenas →
            // ~190M rows) the inline fan-out dominates wall time.
            // record_attestations_bulk creates per-(arena, edge) rows on
            // first event via ON CONFLICT DO UPDATE; arenas that never
            // receive an event are computed lazily from the COALESCE prior
            // formula at query time. AP-1 (open vocabulary) is preserved —
            // arena set is unbounded; no arena is cherry-picked or excluded.
            _ = primer;
            EdgeSignificanceSpec[] overrides = edge.SignificanceOverrides;
            for (int o = 0; o < overrides.Length; o++)
            {
                EdgeSignificanceSpec sig = overrides[o];
                records.Add(new EdgeSignificanceRecord(
                    sig.ContextTypeCode,
                    string.IsNullOrEmpty(sig.AttestationTypeCode)
                        ? ProvenanceAuthorityAttestation
                        : sig.AttestationTypeCode,
                    edge.EdgeTypeCode, edgeHash, sig.InitialMu));
            }

            EdgeRatingEvent[] events = edge.RatingEvents;
            for (int e = 0; e < events.Length; e++)
            {
                EdgeRatingEvent ev = events[e];
                records.Add(new EdgeRatingEventRecord(
                    ev.ContextTypeCode, ev.AttestationTypeCode,
                    edge.EdgeTypeCode, edgeHash, ev.Score, ev.Weight,
                    ev.ModelSourceId, ev.TensorHash, ev.PackageTensorHash,
                    ev.SourceTensorName, ev.PrimitiveCode, ev.TupleCode,
                    ev.SlotCode, ev.ModalityCode, ev.LayerIndex, ev.HeadIndex,
                    ev.ExpertIndex, ev.AdapterName, ev.FusedSlice));
            }
            records.Add(new EdgeRatingEventRecord(
                SourceAuthorityContext, ProvenanceAuthorityAttestation,
                edge.EdgeTypeCode, edgeHash,
                Score: 1.0, Weight: ProvenanceAuthorityEventWeight));
        }

        foreach (JunctionEntry j in b.Junctions)
        {
            records.Add(new JunctionRecord(
                j.JunctionTable, j.Entity.Hash, j.ReferenceId,
                j.AttestationTypeCode ?? "positive_evidence", j.Mu));
        }

        foreach (SignificanceEntry sig in b.Significances)
        {
            records.Add(new EntitySignificanceRecord(
                sig.ContextTypeCode,
                sig.AttestationTypeCode ?? "positive_evidence",
                sig.Entity.Hash, sig.InitialMu));
        }

        foreach (EntityModelSourceEntry e in b.EntityModelSources)
        {
            records.Add(new EntityModelSourceRecord(e.Entity.Hash, e.ModelSourceId));
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
            if (!grouped.TryGetValue(entry.Parent.Hash, out List<CompositionChildEntry>? list))
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
            Hash32[] childHashes = new Hash32[pair.Value.Count];
            int[] ordinalStarts = new int[pair.Value.Count];
            int[] rleCounts = new int[pair.Value.Count];
            int previousEnd = 0;
            for (int i = 0; i < pair.Value.Count; i++)
            {
                CompositionChildEntry entry = pair.Value[i];
                if (entry.Ordinal <= previousEnd)
                {
                    throw new InvalidOperationException(
                        $"Composition metadata for {pair.Key.ToHexString()} overlaps at ordinal {entry.Ordinal}.");
                }
                childHashes[i] = entry.Child.Hash;
                ordinalStarts[i] = entry.Ordinal;
                rleCounts[i] = entry.RleCount;
                previousEnd = entry.Ordinal + entry.RleCount - 1;
            }
            metadata.Add(pair.Key, (childHashes, ordinalStarts, rleCounts));
        }
        return metadata;
    }

    private static Hash32 ComputePhysicalityContentHash(byte[] geometry)
        => Hartonomous.Core.Compute.Common.Blake3.Hash32(geometry.AsSpan());

    private static byte[] BuildCompositionGeometry(
        Hash32[] childHashes, int[] ordinals, int[] rleCounts)
    {
        if (childHashes.Length != ordinals.Length || childHashes.Length != rleCounts.Length)
        {
            throw new InvalidOperationException("Composition manifest arrays must have matching lengths.");
        }
        Point4D[] verts = new Point4D[childHashes.Length];
        for (int i = 0; i < childHashes.Length; i++)
        {
            verts[i] = new Point4D(
                MantissaPacking.PackHashLo(childHashes[i].BitsLow52()),
                MantissaPacking.PackOrdinalRle(ordinals[i], rleCounts[i]),
                MantissaPacking.PackHashHi(childHashes[i].BitsHigh52()),
                MantissaPacking.PackMetadata(0L));
        }
        return Geometry4dPayloadBuilder.LineString((ReadOnlySpan<Point4D>)verts);
    }

    private static Point4D IdentityPoint4D(Hash32 hash, int rolePosition)
        => new(
            MantissaPacking.PackHashLo(hash.BitsLow52()),
            MantissaPacking.PackOrdinalRle(rolePosition, 1),
            MantissaPacking.PackHashHi(hash.BitsHigh52()),
            MantissaPacking.PackMetadata(0L));

    private static Hash32 ComputeEdgeHash(int edgeTypeId, ReadOnlySpan<Hash32> orderedMemberHashes)
    {
        Span<byte> buffer = orderedMemberHashes.Length <= 8
            ? stackalloc byte[4 + orderedMemberHashes.Length * Hash32.Length]
            : new byte[4 + orderedMemberHashes.Length * Hash32.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(0, 4), edgeTypeId);
        int offset = 4;
        for (int i = 0; i < orderedMemberHashes.Length; i++)
        {
            orderedMemberHashes[i].CopyTo(buffer.Slice(offset, Hash32.Length));
            offset += Hash32.Length;
        }
        return Hartonomous.Core.Compute.Common.Blake3.Hash32(buffer);
    }

    // ── Existence probes (IIngestionPipeline bulk APIs, AP-19) ──────────────

    public async Task<HashSet<HashKey>> GetExistingEntityHashesAsync(
        IReadOnlyCollection<Hash32> hashes, CancellationToken ct)
    {
        HashSet<HashKey> existing = new(hashes.Count);
        if (hashes.Count == 0) { return existing; }

        byte[][] arr = new byte[hashes.Count][];
        int i = 0;
        foreach (Hash32 h in hashes) { arr[i++] = h.ToByteArray(); }

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
        if (tuples.Count == 0) { return existing; }

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
        if (tuples.Count == 0) { return existing; }

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
        if (tuples.Count == 0) { return existing; }

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
        if (tuples.Count == 0) { return existing; }

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

    public async Task<bool[]> MerkleTreeFilterAsync(
        IReadOnlyList<Hash32> hashesInTierOrder,
        IReadOnlyList<int> parentIndices,
        CancellationToken ct)
    {
        if (hashesInTierOrder.Count == 0) { return System.Array.Empty<bool>(); }
        if (parentIndices.Count != hashesInTierOrder.Count)
        {
            throw new System.ArgumentException(
                $"MerkleTreeFilterAsync: array length mismatch ({hashesInTierOrder.Count} vs {parentIndices.Count})");
        }

        int n = hashesInTierOrder.Count;
        byte[][] hashArr = new byte[n][];
        int[] parentArr = new int[n];
        for (int i = 0; i < n; i++)
        {
            hashArr[i] = hashesInTierOrder[i].ToByteArray();
            parentArr[i] = parentIndices[i];
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new("SELECT substrate.merkle_tree_filter($1, $2)", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = hashArr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        cmd.Parameters.Add(new NpgsqlParameter { Value = parentArr, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is bool[] flags && flags.Length == n)
        {
            return flags;
        }
        // Defensive fallback — shouldn't happen with a well-formed substrate.
        return new bool[n];
    }

    // ── Worker loop ─────────────────────────────────────────────────────────

    private async Task RunWorkerAsync(int workerId, CancellationToken ct)
    {
        try
        {
            await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

            await using (NpgsqlCommand setCmd = new(IngestionSql.DrainSessionSettings, conn))
            {
                await setCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // One-time CREATE TEMP TABLE per surface still on the pg_temp
            // staging path. entity / classification / physicality / edge /
            // edge_member are no longer on this path — they go directly to
            // substrate.write_* via bulk array params. The remaining surfaces
            // (edge_significance / entity_significance / entity_model_source /
            // junction) are pending migration to substrate-native bulk writes.
            foreach (string tempSql in new[]
            {
                IngestionSql.EdgeSignificance.TempCreate,
                IngestionSql.EntitySignificance.TempCreate,
                IngestionSql.EntityModelSource.TempCreate,
                IngestionSql.Junction.TempCreate,
            })
            {
                await using NpgsqlCommand cmd = new(tempSql, conn);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // Per-worker dedup state — bounded; cleared when capacity hits.
            // Cross-session duplicates flow through and are caught by ON CONFLICT.
            HashSet<Hash32> entityDedup = new();
            HashSet<Hash32> entityClassDedup = new();
            HashSet<Hash32> edgeDedup = new();
            HashSet<Hash32> edgeMemberDedup = new();
            HashSet<Hash32> physicalityDedup = new();
            HashSet<Hash32> entitySigDedup = new();
            HashSet<Hash32> edgeSigDedup = new();

            // Worker-local ID resolution caches. The shared CodeResolver is
            // already a memoizing layer; these locals just avoid touching the
            // shared semaphore on every record.
            Dictionary<string, int> entityTypeIds = new(StringComparer.Ordinal);
            Dictionary<string, int> edgeTypeIds = new(StringComparer.Ordinal);
            Dictionary<string, int> edgeRoleIds = new(StringComparer.Ordinal);
            Dictionary<string, int> physicalityTypeIds = new(StringComparer.Ordinal);
            Dictionary<string, int> provenanceIds = new(StringComparer.Ordinal);
            Dictionary<string, int> contextIds = new(StringComparer.Ordinal);
            Dictionary<string, int> attestationTypeIds = new(StringComparer.Ordinal);

            ChannelReader<RecordBundle> reader = _workerChannels[workerId].Reader;
            List<RecordBundle> chunk = new();
            int chunkRecordCount = 0;

            while (!ct.IsCancellationRequested)
            {
                if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    // Channel closed and empty. If we have a partial chunk,
                    // commit it before exiting.
                    if (chunk.Count > 0)
                    {
                        await DrainChunkAsync(workerId, conn, chunk,
                            entityDedup, entityClassDedup, edgeDedup,
                            edgeMemberDedup, physicalityDedup,
                            entitySigDedup, edgeSigDedup,
                            entityTypeIds, edgeTypeIds, edgeRoleIds,
                            physicalityTypeIds, provenanceIds, contextIds,
                            attestationTypeIds,
                            ct).ConfigureAwait(false);
                        chunk.Clear();
                        chunkRecordCount = 0;
                    }
                    return;
                }

                // Drain everything immediately readable into the chunk; loop
                // again if we hit the cap or idle out.
                while (reader.TryRead(out RecordBundle? bundle) && bundle is not null)
                {
                    chunk.Add(bundle);
                    chunkRecordCount += bundle.Records.Count;
                    if (chunkRecordCount >= CopyChunkRows)
                    {
                        break;
                    }
                }

                if (chunkRecordCount >= CopyChunkRows)
                {
                    await DrainChunkAsync(workerId, conn, chunk,
                        entityDedup, entityClassDedup, edgeDedup,
                        edgeMemberDedup, physicalityDedup,
                        entitySigDedup, edgeSigDedup,
                        entityTypeIds, edgeTypeIds, edgeRoleIds,
                        physicalityTypeIds, provenanceIds, contextIds,
                        attestationTypeIds,
                        ct).ConfigureAwait(false);
                    chunk.Clear();
                    chunkRecordCount = 0;
                    continue;
                }

                // Idle-flush: wait briefly for more bundles; if none arrive,
                // commit what we have.
                using CancellationTokenSource idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idleCts.CancelAfter(IdleFlushAfter);
                bool more;
                try
                {
                    more = await reader.WaitToReadAsync(idleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) // BOUNDARY: idle-flush timeout commits partial chunk; not a real cancellation.
                {
                    more = false;
                }

                if (!more)
                {
                    if (chunk.Count > 0)
                    {
                        await DrainChunkAsync(workerId, conn, chunk,
                            entityDedup, entityClassDedup, edgeDedup,
                            edgeMemberDedup, physicalityDedup,
                            entitySigDedup, edgeSigDedup,
                            entityTypeIds, edgeTypeIds, edgeRoleIds,
                            physicalityTypeIds, provenanceIds, contextIds,
                            attestationTypeIds,
                            ct).ConfigureAwait(false);
                        chunk.Clear();
                        chunkRecordCount = 0;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) // BOUNDARY: worker exits cleanly on requested pipeline shutdown.
        {
            // Pipeline shutdown — clean exit.
        }
        catch (Exception ex) // BOUNDARY: worker failure must trip the writer-fail circuit on this worker's channel.
        {
            Interlocked.Increment(ref _workerErrors[workerId]);
            Log.WorkerCrashed(_logger, workerId, ex);
            _workerChannels[workerId].Writer.TryComplete(ex);
            throw;
        }
    }

    /// <summary>
    /// Open a transaction, COPY each kind's records into its pg_temp table,
    /// run the INSERT-SELECT chain in dependency order, commit. Bundles
    /// commit together — entity / classification / physicality land in the
    /// same txn that the edges referencing them land in, so cross-batch FK
    /// races are impossible by construction.
    /// </summary>
    private async Task DrainChunkAsync(
        int workerId,
        NpgsqlConnection conn,
        List<RecordBundle> chunk,
        HashSet<Hash32> entityDedup,
        HashSet<Hash32> entityClassDedup,
        HashSet<Hash32> edgeDedup,
        HashSet<Hash32> edgeMemberDedup,
        HashSet<Hash32> physicalityDedup,
        HashSet<Hash32> entitySigDedup,
        HashSet<Hash32> edgeSigDedup,
        Dictionary<string, int> entityTypeIds,
        Dictionary<string, int> edgeTypeIds,
        Dictionary<string, int> edgeRoleIds,
        Dictionary<string, int> physicalityTypeIds,
        Dictionary<string, int> provenanceIds,
        Dictionary<string, int> contextIds,
        Dictionary<string, int> attestationTypeIds,
        CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        int totalRecords = 0;
        foreach (RecordBundle b in chunk) { totalRecords += b.Records.Count; }

        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            // pg_temp staging is retired for entity / classification / physicality
            // / edge / edge_member — those go directly to substrate.write_* via
            // bulk array params (no COPY-to-temp + drain round-trip).
            // Surfaces still on the legacy pg_temp path get truncated here.
            await ExecuteAsync(conn, IngestionSql.EdgeSignificance.Truncate, ct).ConfigureAwait(false);
            await ExecuteAsync(conn, IngestionSql.EntitySignificance.Truncate, ct).ConfigureAwait(false);
            await ExecuteAsync(conn, IngestionSql.EntityModelSource.Truncate, ct).ConfigureAwait(false);
            await ExecuteAsync(conn, IngestionSql.Junction.Truncate, ct).ConfigureAwait(false);

            List<EdgeRatingEventRecord> ratingEvents = new();
            int entitiesIn = 0, entityClassIn = 0, physIn = 0, edgesIn = 0, edgeMembersIn = 0;
            int edgeSigIn = 0, entitySigIn = 0, modelSrcIn = 0, junctionsIn = 0;

            // Substrate-native bulk write path: one round-trip per surface,
            // no pg_temp staging, ON CONFLICT DO NOTHING inside the function
            // as race-safety net only (producer-side existence-check via AP-19
            // is the primary dedup).
            entitiesIn = await SubmitEntitiesAsync(conn, chunk, entityDedup, ct).ConfigureAwait(false);
            entityClassIn = await SubmitEntityClassificationsAsync(conn, chunk, entityClassDedup,
                entityTypeIds, provenanceIds, ct).ConfigureAwait(false);
            physIn = await SubmitPhysicalitiesAsync(conn, chunk, physicalityDedup,
                physicalityTypeIds, ct).ConfigureAwait(false);
            edgesIn = await SubmitEdgesAsync(conn, chunk, edgeDedup,
                edgeTypeIds, provenanceIds, ct).ConfigureAwait(false);
            edgeMembersIn = await SubmitEdgeMembersAsync(conn, chunk, edgeMemberDedup,
                edgeTypeIds, edgeRoleIds, ct).ConfigureAwait(false);

            // Legacy pg_temp path (to be migrated to substrate.write_* in follow-ups):
            edgeSigIn = await CopyEdgeSignificancesAsync(conn, chunk, edgeSigDedup,
                contextIds, edgeTypeIds, attestationTypeIds, ct).ConfigureAwait(false);
            entitySigIn = await CopyEntitySignificancesAsync(conn, chunk, entitySigDedup,
                contextIds, attestationTypeIds, ct).ConfigureAwait(false);
            modelSrcIn = await CopyEntityModelSourcesAsync(conn, chunk, ct).ConfigureAwait(false);
            junctionsIn = await CopyJunctionsAsync(conn, chunk, attestationTypeIds, ct).ConfigureAwait(false);

            foreach (RecordBundle b in chunk)
            {
                foreach (IngestionRecord r in b.Records)
                {
                    if (r is EdgeRatingEventRecord ev) { ratingEvents.Add(ev); }
                }
            }

            // Legacy drain SQL only fires for surfaces still on the pg_temp path.
            // entity / classification / physicality / edge / edge_member already
            // INSERTed by their substrate.write_* call above.
            if (edgeSigIn > 0)       { await ExecuteAsync(conn, IngestionSql.EdgeSignificance.Drain, ct).ConfigureAwait(false); }
            if (entitySigIn > 0)     { await ExecuteAsync(conn, IngestionSql.EntitySignificance.Drain, ct).ConfigureAwait(false); }
            if (modelSrcIn > 0)      { await ExecuteAsync(conn, IngestionSql.EntityModelSource.Drain, ct).ConfigureAwait(false); }
            if (junctionsIn > 0)     { await ExecuteAsync(conn, IngestionSql.Junction.Drain, ct).ConfigureAwait(false); }

            // Phase 4: rating events (bulk Glicko-2 update + safetensor
            // observations) fire after the edges they reference have been
            // INSERTed in this same txn.
            if (ratingEvents.Count > 0)
            {
                await FlushRatingEventsAsync(conn, ratingEvents,
                    edgeTypeIds, contextIds, attestationTypeIds, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);

            Interlocked.Add(ref _entitiesEmitted, entitiesIn);
            Interlocked.Add(ref _entityClassificationsEmitted, entityClassIn);
            Interlocked.Add(ref _physicalitiesEmitted, physIn);
            Interlocked.Add(ref _edgesEmitted, edgesIn);
            Interlocked.Add(ref _edgeMembersEmitted, edgeMembersIn);
            Interlocked.Add(ref _edgeSignificancesEmitted, edgeSigIn);
            Interlocked.Add(ref _entitySignificancesEmitted, entitySigIn);
            Interlocked.Add(ref _entityModelSourcesEmitted, modelSrcIn);
            Interlocked.Add(ref _junctionsEmitted, junctionsIn);
            Interlocked.Add(ref _edgeRatingEventsEmitted, ratingEvents.Count);

            Interlocked.Add(ref _workerBundlesDrained[workerId], chunk.Count);
            Interlocked.Add(ref _workerRecordsDrained[workerId], totalRecords);
            Interlocked.Increment(ref _workerCommits[workerId]);

            Log.ChunkCommitted(_logger, workerId, chunk.Count, totalRecords, sw.Elapsed);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            Interlocked.Increment(ref _workerErrors[workerId]);
            Log.ChunkFailed(_logger, workerId, chunk.Count, totalRecords, sw.Elapsed, ex);
            throw;
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = new(sql, conn);
        // INSERT-SELECT drains over 500K-record temp tables exceed the 30s
        // Npgsql default under N-worker contention; cancellation flows through
        // the CancellationToken instead of the command timeout.
        cmd.CommandTimeout = 0;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> SubmitEntitiesAsync(
        NpgsqlConnection conn, List<RecordBundle> chunk, HashSet<Hash32> dedup, CancellationToken ct)
    {
        // Substrate-native bulk-write path. Producer pre-computes hashes in
        // libhartonomous (BLAKE3 / Merkle, AVX2+FMA3+BMI2); in-batch dedup via
        // HashSet; cross-batch dedup at the call site via GetExistingEntityHashesAsync
        // per AP-19. This method sends the chunk's already-deduped candidate
        // hashes to substrate.write_entities, which INSERTs via unnest +
        // ON CONFLICT (hash) DO NOTHING (race-safety net only). No pg_temp,
        // no COPY round-trip, no INSERT-SELECT-from-staging.
        List<byte[]> hashes = new(256);
        foreach (RecordBundle b in chunk)
        {
            foreach (IngestionRecord r in b.Records)
            {
                if (r is not EntityRecord e) { continue; }
                if (!TryAdd(dedup, e.Hash)) { continue; }
                hashes.Add(e.Hash.ToByteArray());
            }
        }
        if (hashes.Count == 0) { return 0; }

        await using NpgsqlCommand cmd = new("SELECT substrate.write_entities($1)", conn);
        cmd.CommandTimeout = 0;
        NpgsqlParameter p = new() { Value = hashes.ToArray() };
        p.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        cmd.Parameters.Add(p);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is int n ? n : hashes.Count;
    }

    private async Task<int> SubmitEntityClassificationsAsync(
        NpgsqlConnection conn, List<RecordBundle> chunk, HashSet<Hash32> dedup,
        Dictionary<string, int> entityTypeIds, Dictionary<string, int> provenanceIds,
        CancellationToken ct)
    {
        List<byte[]> hashes = new(256);
        List<int> typeIds = new(256);
        List<int> provIds = new(256);
        foreach (RecordBundle b in chunk)
        {
            foreach (IngestionRecord r in b.Records)
            {
                Hash32 hash; string etCode; string pCode;
                if (r is EntityRecord e)
                {
                    hash = e.Hash; etCode = e.EntityTypeCode; pCode = e.ProvenanceCode;
                }
                else if (r is EntityClassificationRecord ec)
                {
                    hash = ec.EntityHash; etCode = ec.EntityTypeCode; pCode = ec.ProvenanceCode;
                }
                else { continue; }

                int etId = await GetOrLoadIntAsync(entityTypeIds, etCode, _codeResolver.EntityTypeIdAsync, ct).ConfigureAwait(false);
                int pId  = await GetOrLoadIntAsync(provenanceIds, pCode, _codeResolver.ProvenanceIdAsync, ct).ConfigureAwait(false);

                Hash32 key = ComposeKey3(etId, pId, hash);
                if (!TryAdd(dedup, key)) { continue; }

                hashes.Add(hash.ToByteArray());
                typeIds.Add(etId);
                provIds.Add(pId);
            }
        }
        if (hashes.Count == 0) { return 0; }

        await using NpgsqlCommand cmd = new(
            "SELECT substrate.write_entity_classifications($1, $2, $3)", conn);
        cmd.CommandTimeout = 0;
        NpgsqlParameter pHash = new() { Value = hashes.ToArray() };
        pHash.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        cmd.Parameters.Add(pHash);
        NpgsqlParameter pType = new() { Value = typeIds.ToArray() };
        pType.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer;
        cmd.Parameters.Add(pType);
        NpgsqlParameter pProv = new() { Value = provIds.ToArray() };
        pProv.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer;
        cmd.Parameters.Add(pProv);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is int n ? n : hashes.Count;
    }

    private async Task<int> SubmitPhysicalitiesAsync(
        NpgsqlConnection conn, List<RecordBundle> chunk, HashSet<Hash32> dedup,
        Dictionary<string, int> physicalityTypeIds, CancellationToken ct)
    {
        List<int> typeIds = new(256);
        List<byte[]> entHashes = new(256);
        List<byte[]> contentHashes = new(256);
        List<byte[]> geoms = new(256);
        foreach (RecordBundle b in chunk)
        {
            foreach (IngestionRecord r in b.Records)
            {
                if (r is not PhysicalityRecord p) { continue; }
                int ptId = await GetOrLoadIntAsync(physicalityTypeIds, p.PhysicalityTypeCode,
                    _codeResolver.PhysicalityTypeIdAsync, ct).ConfigureAwait(false);
                Hash32 key = ComposeKey3(ptId, 0, p.EntityHash);
                key = MixHash(key, p.ContentHash);
                if (!TryAdd(dedup, key)) { continue; }
                typeIds.Add(ptId);
                entHashes.Add(p.EntityHash.ToByteArray());
                contentHashes.Add(p.ContentHash.ToByteArray());
                geoms.Add(p.Geometry);
            }
        }
        if (typeIds.Count == 0) { return 0; }

        await using NpgsqlCommand cmd = new(
            "SELECT substrate.write_physicalities($1, $2, $3, $4)", conn);
        cmd.CommandTimeout = 0;
        NpgsqlParameter pType = new() { Value = typeIds.ToArray() };
        pType.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer;
        cmd.Parameters.Add(pType);
        NpgsqlParameter pEnt = new() { Value = entHashes.ToArray() };
        pEnt.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        cmd.Parameters.Add(pEnt);
        NpgsqlParameter pCh = new() { Value = contentHashes.ToArray() };
        pCh.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        cmd.Parameters.Add(pCh);
        NpgsqlParameter pGeom = new() { Value = geoms.ToArray() };
        pGeom.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        cmd.Parameters.Add(pGeom);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is int n ? n : typeIds.Count;
    }

    private async Task<int> SubmitEdgesAsync(
        NpgsqlConnection conn, List<RecordBundle> chunk, HashSet<Hash32> dedup,
        Dictionary<string, int> edgeTypeIds, Dictionary<string, int> provenanceIds,
        CancellationToken ct)
    {
        List<int> typeIds = new(256);
        List<byte[]> edgeHashes = new(256);
        List<int> provIds = new(256);
        List<byte[]?> geoms = new(256);
        foreach (RecordBundle b in chunk)
        {
            foreach (IngestionRecord r in b.Records)
            {
                if (r is not EdgeRecord e) { continue; }
                int etId = await GetOrLoadIntAsync(edgeTypeIds, e.EdgeTypeCode, _codeResolver.EdgeTypeIdAsync, ct).ConfigureAwait(false);
                int pId  = await GetOrLoadIntAsync(provenanceIds, e.ProvenanceCode, _codeResolver.ProvenanceIdAsync, ct).ConfigureAwait(false);
                Hash32 key = ComposeKey3(etId, 0, e.EdgeHash);
                if (!TryAdd(dedup, key)) { continue; }
                typeIds.Add(etId);
                edgeHashes.Add(e.EdgeHash.ToByteArray());
                provIds.Add(pId);
                geoms.Add(e.Geometry);
            }
        }
        if (typeIds.Count == 0) { return 0; }

        await using NpgsqlCommand cmd = new(
            "SELECT substrate.write_edges($1, $2, $3, $4)", conn);
        cmd.CommandTimeout = 0;
        NpgsqlParameter pType = new() { Value = typeIds.ToArray() };
        pType.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer;
        cmd.Parameters.Add(pType);
        NpgsqlParameter pHash = new() { Value = edgeHashes.ToArray() };
        pHash.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        cmd.Parameters.Add(pHash);
        NpgsqlParameter pProv = new() { Value = provIds.ToArray() };
        pProv.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer;
        cmd.Parameters.Add(pProv);
        // Geom array must use object[] so nulls (DBNull.Value) interleave with byte[] payloads.
        object[] geomArr = new object[geoms.Count];
        for (int i = 0; i < geoms.Count; i++) { geomArr[i] = geoms[i] is null ? (object)DBNull.Value : geoms[i]!; }
        NpgsqlParameter pGeom = new() { Value = geomArr };
        pGeom.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        cmd.Parameters.Add(pGeom);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is int n ? n : typeIds.Count;
    }

    private async Task<int> SubmitEdgeMembersAsync(
        NpgsqlConnection conn, List<RecordBundle> chunk, HashSet<Hash32> dedup,
        Dictionary<string, int> edgeTypeIds, Dictionary<string, int> edgeRoleIds,
        CancellationToken ct)
    {
        List<int> typeIds = new(256);
        List<byte[]> edgeHashes = new(256);
        List<byte[]> entHashes = new(256);
        List<int> roleIds = new(256);
        List<int> positions = new(256);
        foreach (RecordBundle b in chunk)
        {
            foreach (IngestionRecord r in b.Records)
            {
                if (r is not EdgeMemberRecord m) { continue; }
                int etId = await GetOrLoadIntAsync(edgeTypeIds, m.EdgeTypeCode, _codeResolver.EdgeTypeIdAsync, ct).ConfigureAwait(false);
                int roleId = await GetOrLoadIntAsync(edgeRoleIds, m.RoleCode, _codeResolver.EdgeRoleIdAsync, ct).ConfigureAwait(false);
                Hash32 key = ComposeKey4(etId, roleId, m.EdgeHash, m.EntityHash, m.RolePosition);
                if (!TryAdd(dedup, key)) { continue; }
                typeIds.Add(etId);
                edgeHashes.Add(m.EdgeHash.ToByteArray());
                entHashes.Add(m.EntityHash.ToByteArray());
                roleIds.Add(roleId);
                positions.Add(m.RolePosition);
            }
        }
        if (typeIds.Count == 0) { return 0; }

        await using NpgsqlCommand cmd = new(
            "SELECT substrate.write_edge_members($1, $2, $3, $4, $5)", conn);
        cmd.CommandTimeout = 0;
        NpgsqlParameter pType = new() { Value = typeIds.ToArray() };
        pType.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer;
        cmd.Parameters.Add(pType);
        NpgsqlParameter pEh = new() { Value = edgeHashes.ToArray() };
        pEh.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        cmd.Parameters.Add(pEh);
        NpgsqlParameter pEnt = new() { Value = entHashes.ToArray() };
        pEnt.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        cmd.Parameters.Add(pEnt);
        NpgsqlParameter pRole = new() { Value = roleIds.ToArray() };
        pRole.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer;
        cmd.Parameters.Add(pRole);
        NpgsqlParameter pPos = new() { Value = positions.ToArray() };
        pPos.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer;
        cmd.Parameters.Add(pPos);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is int n ? n : typeIds.Count;
    }

    private async Task<int> CopyEdgeSignificancesAsync(
        NpgsqlConnection conn, List<RecordBundle> chunk, HashSet<Hash32> dedup,
        Dictionary<string, int> contextIds, Dictionary<string, int> edgeTypeIds,
        Dictionary<string, int> attestationTypeIds, CancellationToken ct)
    {
        await using NpgsqlBinaryImporter w = await conn.BeginBinaryImportAsync(IngestionSql.EdgeSignificance.Copy, ct).ConfigureAwait(false);
        int rows = 0;
        foreach (RecordBundle b in chunk)
        {
            foreach (IngestionRecord r in b.Records)
            {
                if (r is not EdgeSignificanceRecord s) { continue; }
                int cId = await GetOrLoadIntAsync(contextIds, s.ContextTypeCode, _codeResolver.SignificanceContextIdAsync, ct).ConfigureAwait(false);
                int etId = await GetOrLoadIntAsync(edgeTypeIds, s.EdgeTypeCode, _codeResolver.EdgeTypeIdAsync, ct).ConfigureAwait(false);
                int aId = await GetOrLoadIntAsync(attestationTypeIds, s.AttestationTypeCode, _codeResolver.AttestationTypeIdAsync, ct).ConfigureAwait(false);
                Hash32 key = ComposeKey4(cId, etId, s.EdgeHash, default, aId);
                if (!TryAdd(dedup, key)) { continue; }
                w.StartRow();
                w.Write(cId, NpgsqlDbType.Integer);
                w.Write(etId, NpgsqlDbType.Integer);
                w.Write(s.EdgeHash.ToByteArray(), NpgsqlDbType.Bytea);
                w.Write(aId, NpgsqlDbType.Integer);
                w.Write(s.InitialMu, NpgsqlDbType.Double);
                rows++;
            }
        }
        await w.CompleteAsync(ct).ConfigureAwait(false);
        return rows;
    }

    private async Task<int> CopyEntitySignificancesAsync(
        NpgsqlConnection conn, List<RecordBundle> chunk, HashSet<Hash32> dedup,
        Dictionary<string, int> contextIds, Dictionary<string, int> attestationTypeIds,
        CancellationToken ct)
    {
        await using NpgsqlBinaryImporter w = await conn.BeginBinaryImportAsync(IngestionSql.EntitySignificance.Copy, ct).ConfigureAwait(false);
        int rows = 0;
        foreach (RecordBundle b in chunk)
        {
            foreach (IngestionRecord r in b.Records)
            {
                if (r is not EntitySignificanceRecord s) { continue; }
                int cId = await GetOrLoadIntAsync(contextIds, s.ContextTypeCode, _codeResolver.SignificanceContextIdAsync, ct).ConfigureAwait(false);
                int aId = await GetOrLoadIntAsync(attestationTypeIds, s.AttestationTypeCode, _codeResolver.AttestationTypeIdAsync, ct).ConfigureAwait(false);
                Hash32 key = ComposeKey3(cId, aId, s.EntityHash);
                if (!TryAdd(dedup, key)) { continue; }
                w.StartRow();
                w.Write(cId, NpgsqlDbType.Integer);
                w.Write(s.EntityHash.ToByteArray(), NpgsqlDbType.Bytea);
                w.Write(aId, NpgsqlDbType.Integer);
                w.Write(s.InitialMu, NpgsqlDbType.Double);
                rows++;
            }
        }
        await w.CompleteAsync(ct).ConfigureAwait(false);
        return rows;
    }

    private static async Task<int> CopyEntityModelSourcesAsync(
        NpgsqlConnection conn, List<RecordBundle> chunk, CancellationToken ct)
    {
        await using NpgsqlBinaryImporter w = await conn.BeginBinaryImportAsync(IngestionSql.EntityModelSource.Copy, ct).ConfigureAwait(false);
        int rows = 0;
        foreach (RecordBundle b in chunk)
        {
            foreach (IngestionRecord r in b.Records)
            {
                if (r is not EntityModelSourceRecord m) { continue; }
                w.StartRow();
                w.Write(m.EntityHash.ToByteArray(), NpgsqlDbType.Bytea);
                w.Write((int)m.ModelSourceId, NpgsqlDbType.Integer);
                rows++;
            }
        }
        await w.CompleteAsync(ct).ConfigureAwait(false);
        return rows;
    }

    private async Task<int> CopyJunctionsAsync(
        NpgsqlConnection conn, List<RecordBundle> chunk,
        Dictionary<string, int> attestationTypeIds, CancellationToken ct)
    {
        await using NpgsqlBinaryImporter w = await conn.BeginBinaryImportAsync(IngestionSql.Junction.Copy, ct).ConfigureAwait(false);
        int rows = 0;
        foreach (RecordBundle b in chunk)
        {
            foreach (IngestionRecord r in b.Records)
            {
                if (r is not JunctionRecord j) { continue; }
                if (!AllowedJunctionTables.Contains(j.JunctionTable))
                {
                    throw new ArgumentException(
                        $"JunctionRecord.JunctionTable not in allowlist: '{j.JunctionTable}'");
                }
                int aId = await GetOrLoadIntAsync(attestationTypeIds, j.AttestationTypeCode, _codeResolver.AttestationTypeIdAsync, ct).ConfigureAwait(false);
                w.StartRow();
                w.Write(j.JunctionTable, NpgsqlDbType.Text);
                w.Write(j.EntityHash.ToByteArray(), NpgsqlDbType.Bytea);
                w.Write(j.ReferenceId, NpgsqlDbType.Integer);
                w.Write(aId, NpgsqlDbType.Integer);
                if (j.Mu.HasValue) { w.Write(j.Mu.Value, NpgsqlDbType.Double); }
                else { w.WriteNull(); }
                rows++;
            }
        }
        await w.CompleteAsync(ct).ConfigureAwait(false);
        return rows;
    }

    private async Task FlushRatingEventsAsync(
        NpgsqlConnection conn, List<EdgeRatingEventRecord> events,
        Dictionary<string, int> edgeTypeIds,
        Dictionary<string, int> contextIds,
        Dictionary<string, int> attestationTypeIds,
        CancellationToken ct)
    {
        // Bucket events by (arena_id, attestation_type_id) so each bulk SQL
        // call covers one Glicko-2 surface.
        Dictionary<(int Arena, int Atest), List<EdgeRatingEventRecord>> buckets = new();
        foreach (EdgeRatingEventRecord ev in events)
        {
            int arenaId = await GetOrLoadIntAsync(contextIds, ev.ContextTypeCode, _codeResolver.SignificanceContextIdAsync, ct).ConfigureAwait(false);
            int atestId = await GetOrLoadIntAsync(attestationTypeIds, ev.AttestationTypeCode, _codeResolver.AttestationTypeIdAsync, ct).ConfigureAwait(false);
            (int, int) key = (arenaId, atestId);
            if (!buckets.TryGetValue(key, out List<EdgeRatingEventRecord>? list))
            {
                list = new List<EdgeRatingEventRecord>();
                buckets[key] = list;
            }
            list.Add(ev);
        }

        foreach (KeyValuePair<(int, int), List<EdgeRatingEventRecord>> kv in buckets)
        {
            List<EdgeRatingEventRecord> batch = kv.Value;
            int n = batch.Count;
            int[] etypeIds = new int[n];
            byte[][] hashes = new byte[n][];
            double[] scores = new double[n];
            double[] weights = new double[n];
            for (int i = 0; i < n; i++)
            {
                EdgeRatingEventRecord r = batch[i];
                int edgeTypeId = await GetOrLoadIntAsync(edgeTypeIds, r.EdgeTypeCode, _codeResolver.EdgeTypeIdAsync, ct).ConfigureAwait(false);
                etypeIds[i] = edgeTypeId;
                hashes[i]   = r.EdgeHash.ToByteArray();
                scores[i]   = r.Score;
                weights[i]  = r.Weight;
            }
            await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
                conn,
                SubstrateFunctionNames.RecordAttestationsBulk,
                new[]
                {
                    new NpgsqlParameter { Value = kv.Key.Item1, NpgsqlDbType = NpgsqlDbType.Integer },
                    new NpgsqlParameter { Value = kv.Key.Item2, NpgsqlDbType = NpgsqlDbType.Integer },
                    new NpgsqlParameter { Value = etypeIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer },
                    new NpgsqlParameter { Value = hashes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea },
                    new NpgsqlParameter { Value = scores, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double },
                    new NpgsqlParameter { Value = weights, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double },
                });
            cmd.CommandTimeout = 0;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await InsertSafetensorObservationsAsync(conn, batch, kv.Key.Item1, kv.Key.Item2, edgeTypeIds, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask InsertSafetensorObservationsAsync(
        NpgsqlConnection conn,
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
        if (observations.Count == 0) { return; }

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
            int edgeTypeId = await GetOrLoadIntAsync(edgeTypeIdCache, r.EdgeTypeCode, _codeResolver.EdgeTypeIdAsync, cancellationToken).ConfigureAwait(false);
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

    private static async ValueTask<int> GetOrLoadIntAsync(
        Dictionary<string, int> cache, string code,
        Func<string, CancellationToken, Task<int>> loader,
        CancellationToken ct)
    {
        if (cache.TryGetValue(code, out int id)) { return id; }
        id = await loader(code, ct).ConfigureAwait(false);
        cache[code] = id;
        return id;
    }

    private const int DedupCapacityPerWorker = 1_048_576;

    private static bool TryAdd(HashSet<Hash32> dedup, Hash32 key)
    {
        if (dedup.Count >= DedupCapacityPerWorker) { dedup.Clear(); }
        return dedup.Add(key);
    }

    private static Hash32 ComposeKey3(int a, int b, Hash32 hash)
    {
        Span<byte> buf = stackalloc byte[4 + 4 + Hash32.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(0, 4), a);
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(4, 4), b);
        hash.CopyTo(buf.Slice(8, Hash32.Length));
        return Hartonomous.Core.Compute.Common.Blake3.Hash32(buf);
    }

    private static Hash32 ComposeKey4(int a, int b, Hash32 hashA, Hash32 hashB, int pos)
    {
        Span<byte> buf = stackalloc byte[4 + 4 + Hash32.Length + Hash32.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(0, 4), a);
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(4, 4), b);
        hashA.CopyTo(buf.Slice(8, Hash32.Length));
        hashB.CopyTo(buf.Slice(8 + Hash32.Length, Hash32.Length));
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(8 + 2 * Hash32.Length, 4), pos);
        return Hartonomous.Core.Compute.Common.Blake3.Hash32(buf);
    }

    private static Hash32 MixHash(Hash32 a, Hash32 b)
    {
        Span<byte> buf = stackalloc byte[Hash32.Length * 2];
        a.CopyTo(buf.Slice(0, Hash32.Length));
        b.CopyTo(buf.Slice(Hash32.Length, Hash32.Length));
        return Hartonomous.Core.Compute.Common.Blake3.Hash32(buf);
    }

    // Junction allowlist mirrors the prior pipeline; defended-in-depth.
    private static readonly HashSet<string> AllowedJunctionTables = new(StringComparer.Ordinal)
    {
        "entity_pos", "entity_lexname", "entity_language", "entity_morph_feature",
        "model_architecture_class", "tensor_tensor_role", "pattern_deprel",
        // Per-codepoint UCD property analytics caches (Gate 1 #38 refactor 2026-05-18).
        "cp_general_category", "cp_script", "cp_block", "cp_bidi_class",
        "cp_east_asian_width", "cp_grapheme_break", "cp_word_break",
        "cp_sentence_break", "cp_line_break",
    };

    // ── Inline edge-significance primer table ───────────────────────────────

    private async ValueTask<InlinePrimerTable> EnsurePrimerTableAsync(CancellationToken ct)
    {
        InlinePrimerTable? table = Volatile.Read(ref _primerTable);
        if (table is not null) { return table; }

        await _primerInit.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_primerTable is not null) { return _primerTable; }
            _primerTable = await BuildPrimerTableAsync(ct).ConfigureAwait(false);
            return _primerTable;
        }
        finally
        {
            _primerInit.Release();
        }
    }

    /// <summary>
    /// Snapshot every (provenance, edge_type, arena) triple and compute the
    /// initial-mu prior once at pipeline startup. AP-1: every arena currently
    /// in substrate.significance_context is included; no hardcoded subset.
    /// </summary>
    private async Task<InlinePrimerTable> BuildPrimerTableAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        const string sql = @"
            SELECT p.code AS provenance_code,
                   et.code AS edge_type_code,
                   sc.code AS arena_code,
                   COALESCE(pea.initial_mu, p.initial_mu * et.semantic_weight * p.derivation_decay) AS initial_mu
              FROM substrate.provenance p
             CROSS JOIN substrate.edge_type et
             CROSS JOIN substrate.significance_context sc
              LEFT JOIN substrate.provenance_edge_authority pea
                ON pea.provenance_id = p.id
               AND pea.edge_type_id  = et.id
        ";
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.CommandTimeout = 0;
        Dictionary<(string Provenance, string EdgeType), List<InlinePrimerEntry>> byPair = new();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        int total = 0;
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string p = r.GetString(0);
            string et = r.GetString(1);
            string arena = r.GetString(2);
            double mu = r.GetDouble(3);
            (string, string) key = (p, et);
            if (!byPair.TryGetValue(key, out List<InlinePrimerEntry>? list))
            {
                list = new List<InlinePrimerEntry>();
                byPair[key] = list;
            }
            list.Add(new InlinePrimerEntry(arena, mu));
            total++;
        }
        int maxPerPair = 0;
        foreach (List<InlinePrimerEntry> list in byPair.Values)
        {
            if (list.Count > maxPerPair) { maxPerPair = list.Count; }
        }
        return new InlinePrimerTable(byPair, total, maxPerPair);
    }

    private static Task<string?> TryProvenanceForEdgeAsync(EdgeRecord edge, CancellationToken ct)
    {
        // The streaming-EmitAsync path provides the edge's provenance code on
        // the record itself; no DB lookup needed.
        return Task.FromResult<string?>(edge.ProvenanceCode);
    }

    private sealed class InlinePrimerTable
    {
        private static readonly IReadOnlyList<InlinePrimerEntry> Empty = Array.Empty<InlinePrimerEntry>();
        private readonly Dictionary<(string Provenance, string EdgeType), List<InlinePrimerEntry>> _byPair;
        public int Count { get; }
        public int MaxPerPair { get; }

        public InlinePrimerTable(
            Dictionary<(string Provenance, string EdgeType), List<InlinePrimerEntry>> byPair,
            int totalEntries,
            int maxPerPair)
        {
            _byPair = byPair;
            Count = totalEntries;
            MaxPerPair = maxPerPair;
        }

        public IReadOnlyList<InlinePrimerEntry> For(string provenanceCode, string edgeTypeCode)
        {
            return _byPair.TryGetValue((provenanceCode, edgeTypeCode), out List<InlinePrimerEntry>? list)
                ? list
                : Empty;
        }
    }

    private readonly record struct InlinePrimerEntry(string ArenaCode, double InitialMu);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Pipeline worker {Worker} committed chunk: bundles={Bundles} records={Records} elapsed={Elapsed}")]
        public static partial void ChunkCommitted(ILogger logger, int worker, int bundles, int records, TimeSpan elapsed);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Pipeline worker {Worker} chunk FAILED: bundles={Bundles} records={Records} elapsed={Elapsed}")]
        public static partial void ChunkFailed(ILogger logger, int worker, int bundles, int records, TimeSpan elapsed, Exception ex);

        [LoggerMessage(Level = LogLevel.Critical,
            Message = "Pipeline worker {Worker} CRASHED")]
        public static partial void WorkerCrashed(ILogger logger, int worker, Exception ex);
    }
}
