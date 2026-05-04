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
    private long _producerDedupHits;

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

        // Build a within-batch centroid map from any POINTZM physicalities
        // emitted by the decomposer. When an edge's participants are all
        // atoms with POINTZM physicality (the common case for codepoint /
        // word_form / lemma edges), we can attach the LINESTRINGZM EWKB
        // inline so the drain INSERT writes geom directly. Edges whose
        // participants don't have POINTZM here (compositions whose physicality
        // is LINESTRINGZM, or participants from prior batches) leave geom
        // NULL and are backfilled by PopulateEdgeTrajectoriesAsync.
        Dictionary<Hash32, (double X, double Y, double Z, double M)>? centroidMap = null;
        foreach (PhysicalityEntry p in b.Physicalities)
        {
            // POINTZM EWKB layout: byte_order(1) + type(4) + 4*float8(32) = 37 bytes.
            // Type word: 0xC0000001 (PostGIS EWKB POINT|Z|M) or 3001 (ISO).
            if (p.Wkb.Length != 37) continue;
            if (p.Wkb[0] != 0x01) continue; // require little-endian
            uint typeWord = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Wkb.AsSpan(1, 4));
            bool isPointZM = (typeWord == 0xC0000001u) || (typeWord == 3001u);
            if (!isPointZM) continue;
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

    public async Task PopulateEdgeTrajectoriesAsync(CancellationToken ct)
    {
        // Backfill geom on edges where the producer didn't (or couldn't)
        // attach an inline LINESTRINGZM EWKB. The substrate function is
        // set-based: one UPDATE walks substrate.edge WHERE geom IS NULL,
        // joins substrate.edge_member, calls substrate.entity_centroid_4d
        // per participant, and ST_MakeLine ORDER BY edge_role_id assembles
        // the trajectory preserving Z and M dimensions.
        const int chunkSize = 65_536;
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        long totalUpdated = 0;
        while (true)
        {
            await using NpgsqlCommand cmd = new(
                "SELECT substrate.populate_edge_trajectories($1)", conn);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, chunkSize);
            object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            long updated = result is long l ? l : (long?)result ?? 0L;
            totalUpdated += updated;
            if (updated == 0) break;
        }
        Log.EdgeTrajectoriesPopulated(_logger, totalUpdated);
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
            EdgeRecord r                => EmitEdgeAsync(r, ct),
            EdgeMemberRecord r          => _edgeMembers.Writer.WriteAsync(r, ct),
            JunctionRecord r            => _junctions.Writer.WriteAsync(r, ct),
            PhysicalityRecord r         => EmitPhysicalityAsync(r, ct),
            SequenceRecord r            => EmitSequenceAsync(r, ct),
            EntitySignificanceRecord r  => EmitEntitySignificanceAsync(r, ct),
            EdgeSignificanceRecord r    => EmitEdgeSignificanceAsync(r, ct),
            EntityModelSourceRecord r   => _entityModelSources.Writer.WriteAsync(r, ct),
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
            await _entities.Writer.WriteAsync(r, ct).ConfigureAwait(false);
        }
        // Classification always goes through — substrate.entity_classification
        // ON CONFLICT handles cross-session dupes; within-session a decomposer
        // emitting the same (entity, type, provenance) twice is harmless.
        await _entityClassifications.Writer.WriteAsync(
            new EntityClassificationRecord(r.Hash, r.EntityTypeCode, r.ProvenanceCode), ct)
            .ConfigureAwait(false);
    }

    private async ValueTask EmitEdgeAsync(EdgeRecord r, CancellationToken ct)
    {
        // Dedup key includes edge type because (edge_type_id, hash) is the PK.
        Hash32 key = ComposeKey(r.EdgeTypeCode, r.EdgeHash);
        if (TryAddDedup(_edgeDedup, key))
        {
            await _edges.Writer.WriteAsync(r, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask EmitPhysicalityAsync(PhysicalityRecord r, CancellationToken ct)
    {
        // (physicality_type_id, entity_hash, content_hash) is the PK.
        Hash32 key = ComposeKey(r.PhysicalityTypeCode, r.EntityHash, r.ContentHash);
        if (TryAddDedup(_physicalityDedup, key))
        {
            await _physicalities.Writer.WriteAsync(r, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask EmitSequenceAsync(SequenceRecord r, CancellationToken ct)
    {
        // (parent_hash, ordinal) is the PK; child_hash and rle_count not in PK.
        Hash32 key = ComposeKey(r.ParentEntityHash, r.Ordinal);
        if (TryAddDedup(_sequenceDedup, key))
        {
            await _sequences.Writer.WriteAsync(r, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask EmitEntitySignificanceAsync(EntitySignificanceRecord r, CancellationToken ct)
    {
        Hash32 key = ComposeKey(r.ContextTypeCode, r.EntityHash);
        if (TryAddDedup(_entitySignificanceDedup, key))
        {
            await _entitySignificances.Writer.WriteAsync(r, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask EmitEdgeSignificanceAsync(EdgeSignificanceRecord r, CancellationToken ct)
    {
        Hash32 key = ComposeKey(r.ContextTypeCode, r.EdgeTypeCode, r.EdgeHash);
        if (TryAddDedup(_edgeSignificanceDedup, key))
        {
            await _edgeSignificances.Writer.WriteAsync(r, ct).ConfigureAwait(false);
        }
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

        // End-of-phase post-passes (replaces the deleted background workers):
        //   1. Backfill substrate.edge.geom for any edges whose producer
        //      didn't attach inline LINESTRINGZM EWKB (compositions whose
        //      participants have LINESTRINGZM physicality, cross-batch
        //      participants, etc.).
        //   2. Prime substrate.edge_significance with the compound-formula
        //      μ across every arena currently in substrate.significance_context
        //      (AP-1: cross-product, no cherry-picking).
        // Both are idempotent (ON CONFLICT DO NOTHING / IS NULL guards) and
        // run set-based once per phase. No long-lived background tasks.
        try
        {
            await PopulateEdgeTrajectoriesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.PostPassFailed(_logger, "populate_edge_trajectories", ex);
        }
        try
        {
            await PrimeAllSignificanceAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.PostPassFailed(_logger, "prime_significance", ex);
        }
    }

    /// <summary>
    /// Prime substrate.edge_significance for every arena currently in
    /// substrate.significance_context, walking unprimed edges via the
    /// watermark-based <c>substrate.prime_unprimed_edges_chunk</c>. Replaces
    /// the deleted BackgroundSignificancePrimer's continuous loop — runs once
    /// per phase from FlushAsync. AP-1 compliant: re-reads the arena list
    /// at call time so newly-added arenas auto-prime against existing edges.
    /// </summary>
    public async Task PrimeAllSignificanceAsync(CancellationToken ct)
    {
        const int chunkSize = 65_536;
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        // Snapshot the arena list at call time. AP-1: cross-product against
        // every arena that exists right now. Don't filter, don't cherry-pick.
        List<int> arenaIds = new();
        await using (NpgsqlCommand listCmd = new(
            "SELECT id FROM substrate.significance_context ORDER BY id", conn))
        await using (NpgsqlDataReader r = await listCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                arenaIds.Add(r.GetInt32(0));
            }
        }

        long totalPrimed = 0;
        foreach (int arenaId in arenaIds)
        {
            while (true)
            {
                await using NpgsqlCommand cmd = new(
                    "SELECT substrate.prime_unprimed_edges_chunk($1, $2)", conn);
                cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, arenaId);
                cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, chunkSize);
                object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                long inserted = result is long l ? l : (long?)result ?? 0L;
                totalPrimed += inserted;
                if (inserted == 0) break;
            }
        }
        Log.SignificancePrimed(_logger, arenaIds.Count, totalPrimed);
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
                    provenance_id INT   NOT NULL,
                    geom_wkb      BYTEA
                )
                """,
            copySql: "COPY pg_temp.edge_inflight (edge_type_id, hash, provenance_id, geom_wkb) FROM STDIN (FORMAT binary)",
            truncateSql: "TRUNCATE pg_temp.edge_inflight",
            // Inline geom path: ST_GeomFromWKB lifts producer-built EWKB to
            // substrate.edge.geom. Edges with NULL geom_wkb go in with
            // geom = NULL and are backfilled by substrate.populate_edge_trajectories
            // at end-of-phase via PopulateEdgeTrajectoriesAsync.
            drainSql: """
                INSERT INTO substrate.edge (edge_type_id, hash, provenance_id, geom)
                SELECT DISTINCT ON (edge_type_id, hash)
                       edge_type_id, hash, provenance_id,
                       CASE WHEN geom_wkb IS NULL THEN NULL ELSE ST_GeomFromWKB(geom_wkb, 0) END
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

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Edge trajectories populated (post-pass): edges_updated={EdgesUpdated}")]
        public static partial void EdgeTrajectoriesPopulated(ILogger logger, long edgesUpdated);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Edge significance primed (post-pass): arenas={Arenas} edges_primed={EdgesPrimed}")]
        public static partial void SignificancePrimed(ILogger logger, int arenas, long edgesPrimed);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Pipeline post-pass FAILED: pass={Pass}")]
        public static partial void PostPassFailed(ILogger logger, string pass, Exception ex);
    }
}
