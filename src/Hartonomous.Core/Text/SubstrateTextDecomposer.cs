using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Native;

namespace Hartonomous.Core.Text;

/// <summary>
/// In-process text decomposer. Calls libhartonomous's native
/// <c>hartonomous_text_decompose</c>, which performs the entire UAX #29 +
/// BLAKE3 + 4D centroid pipeline against the embedded UCD 17.0.0 blob
/// and fires a callback once per emitted record. The C# callback drops
/// the records into the caller's <see cref="IIngestionBatch"/>; the bulk
/// writes happen later via the streaming pipeline's COPY paths. No PG
/// roundtrip per text — one P/Invoke + N callback fires.
///
/// Lives in <c>Hartonomous.Core.Text</c> so <c>BaseDecomposer.EmitText</c>
/// (in Core) can call it without creating a circular reference back to
/// Hartonomous.Decomposers.
///
/// Determinism: same UTF-8 input → byte-identical hashes. The native
/// pipeline shares its source with <c>pg_text_decompose</c>; both walk
/// the same UCD blob and produce the same output (Law #6).
/// </summary>
public sealed class SubstrateTextDecomposer
{
    private static int s_ucdLoaded;
    private static readonly object s_ucdLoadLock = new();
    private static readonly TextDecomposeNative.EmitCallback s_noopEmit = static (IntPtr _, ref TextDecomposeRecord _) => 0;

    /// <summary>
    /// Constructor signature kept compatible with the prior Npgsql-backed
    /// version. The opaque object argument is ignored (it was an
    /// <c>NpgsqlDataSource</c> before; native pipeline doesn't need it,
    /// and Hartonomous.Core can't reference Npgsql).
    /// </summary>
    public SubstrateTextDecomposer(object? unused = null)
    {
        _ = unused;
    }

    /// <summary>
    /// Initialise the embedded UCD blob (idempotent, thread-safe). Probes
    /// the canonical install paths plus an explicit override via
    /// <c>HARTONOMOUS_UCD_BLOB_DIR</c>.
    /// </summary>
    public static void EnsureUcdLoaded()
    {
        if (Volatile.Read(ref s_ucdLoaded) != 0)
        {
            return;
        }

        lock (s_ucdLoadLock)
        {
            if (Volatile.Read(ref s_ucdLoaded) != 0)
            {
                return;
            }

            string? envDir = Environment.GetEnvironmentVariable("HARTONOMOUS_UCD_BLOB_DIR");
            string[] candidates =
            [
                envDir ?? string.Empty,
                "/opt/pg18/share/postgresql/extension/hartonomous-ucd",
                System.IO.Path.Combine(AppContext.BaseDirectory, "ucd"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ext", "hartonomous_pg", "src", "generated"),
            ];
            foreach (string dir in candidates)
            {
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }
                if (!System.IO.Directory.Exists(dir))
                {
                    continue;
                }
                int rc = TextDecomposeNative.UcdLoad(dir);
                if (rc == 0)
                {
                    if (TextDecomposeNative.UcdCatalogReady() != 1)
                    {
                        throw new InvalidOperationException(
                            "SubstrateTextDecomposer: loaded UCD atom catalog failed "
                            + "hash/centroid/reverse-lookup validation.");
                    }

                    if (TextDecomposeNative.UcdTablesReady() != 1)
                    {
                        throw new InvalidOperationException(
                            "SubstrateTextDecomposer: libhartonomous loaded UCD atoms, "
                            + "but required generated UCD normalization/segmentation tables "
                            + "are not linked into the native library.");
                    }

                    Volatile.Write(ref s_ucdLoaded, 1);
                    return;
                }
            }

            throw new InvalidOperationException(
                "SubstrateTextDecomposer: could not locate the UCD blob. Set "
                + "HARTONOMOUS_UCD_BLOB_DIR or install hartonomous-ucd to a "
                + "discoverable path. Probed: "
                + string.Join("; ", candidates));
        }
    }

    /// <summary>
    /// Decompose <paramref name="utf8"/> directly into <paramref name="batch"/>.
    /// Instance shim; delegates to <see cref="EmitStatic"/>.
    /// </summary>
#pragma warning disable CA1822
    public TextDecomposeResult Emit(
        IIngestionBatch batch,
        ReadOnlySpan<byte> utf8,
        TextDecomposeOptions options)
        => EmitStatic(batch, utf8, options);

    public ValueTask<TextDecomposeResult> EmitAsync(
        IRecordSink sink,
        byte[] utf8,
        TextDecomposeOptions options,
        CancellationToken ct)
        => EmitStaticAsync(sink, utf8, options, ct);
#pragma warning restore CA1822

    /// <summary>
    /// Static text-decompose entry point. The native walk has no per-instance
    /// state — every input gets a fresh <see cref="EmitContext"/> built on
    /// the stack of this call. Used by <c>BaseDecomposer.EmitText</c> to
    /// avoid threading an instance dependency through every decomposer.
    /// </summary>
    public static TextDecomposeResult EmitStatic(
        IIngestionBatch batch,
        ReadOnlySpan<byte> utf8,
        TextDecomposeOptions options)
        => EmitStaticCore(batch, utf8, options);

    public static TextDecomposeResult EmitStatic(
        IIngestionBatch batch,
        byte[] utf8,
        TextDecomposeOptions options)
    {
        ArgumentNullException.ThrowIfNull(utf8);
        return EmitStaticCore(batch, utf8, options);
    }

    private static unsafe TextDecomposeResult EmitStaticCore(
        IIngestionBatch batch,
        ReadOnlySpan<byte> utf8,
        TextDecomposeOptions options)
    {
        ArgumentNullException.ThrowIfNull(batch);
        EnsureUcdLoaded();

        if (utf8.Length == 0)
        {
            Hash32 emptyHash = Hash32.Zero;
            return new TextDecomposeResult(
                RootHandle: new EntityHandle(emptyHash, options.TopEntityType),
                RootHash: emptyHash,
                EntitiesEmitted: 0, CompositionChildrenEmitted: 0,
                PhysicalityRowsEmitted: 0, SignificanceRowsEmitted: 0,
                RootCentroid: (0, 0, 0, 0));
        }

        EmitContext context = new(batch, options);
        TextDecomposeNative.EmitCallback cb = context.OnRecord;

        byte[] rootHashBuf = new byte[32];
        double[] rootCentroidBuf = new double[4];
        GCHandle rootPin = GCHandle.Alloc(rootHashBuf, GCHandleType.Pinned);
        GCHandle rootCentroidPin = GCHandle.Alloc(rootCentroidBuf, GCHandleType.Pinned);
        int rc;
        try
        {
            fixed (byte* utf8Ptr = utf8)
            {
                rc = TextDecomposeNative.TextDecompose(
                    (IntPtr)utf8Ptr,
                    (nuint) utf8.Length,
                    NativeKindFor(options.TopEntityType),
                    options.TrustMu,
                    cb,
                    IntPtr.Zero,
                    rootPin.AddrOfPinnedObject(),
                    out _,
                    rootCentroidPin.AddrOfPinnedObject());
            }
        }
        finally
        {
            rootPin.Free();
            rootCentroidPin.Free();
            GC.KeepAlive(cb);
        }

        if (rc != 0)
        {
            throw new InvalidOperationException(
                $"hartonomous_text_decompose returned {rc} (input {utf8.Length} bytes, top_kind={options.TopEntityType})");
        }

        Hash32 rootHash = new(rootHashBuf);
        EntityHandle rootHandle = new(rootHash, options.TopEntityType);
        AddTopEntityClassificationIfNeeded(batch, options, rootHash);
        context.FlushCompositionChildren();

        return new TextDecomposeResult(
            RootHandle: rootHandle,
            RootHash: rootHash,
            EntitiesEmitted: context.EntityCount,
            CompositionChildrenEmitted: context.CompositionChildCount,
            PhysicalityRowsEmitted: context.PhysicalityCount,
            SignificanceRowsEmitted: context.SignificanceCount,
            RootCentroid: (rootCentroidBuf[0], rootCentroidBuf[1], rootCentroidBuf[2], rootCentroidBuf[3]));
    }

    /// <summary>
    /// Compute the native text root hash without emitting records into an
    /// ingestion batch. This is for lookup/planning paths that need the exact
    /// same content identity as <see cref="EmitStatic"/> but should not create
    /// substrate rows.
    /// </summary>
    public static unsafe Hash32 ComputeRootHash(
        ReadOnlySpan<byte> utf8,
        string topEntityType,
        double trustMu = 1500.0)
    {
        EnsureUcdLoaded();

        if (utf8.IsEmpty)
        {
            return Hash32.Zero;
        }

        byte[] rootHashBuf = new byte[32];
        GCHandle rootPin = GCHandle.Alloc(rootHashBuf, GCHandleType.Pinned);
        int rc;
        try
        {
            fixed (byte* utf8Ptr = utf8)
            {
                rc = TextDecomposeNative.TextDecompose(
                    (IntPtr)utf8Ptr,
                    (nuint) utf8.Length,
                    NativeKindFor(topEntityType),
                    trustMu,
                    s_noopEmit,
                    IntPtr.Zero,
                    rootPin.AddrOfPinnedObject(),
                    out _,
                    IntPtr.Zero);
            }
        }
        finally
        {
            rootPin.Free();
            GC.KeepAlive(s_noopEmit);
        }

        if (rc != 0)
        {
            throw new InvalidOperationException(
                $"hartonomous_text_decompose returned {rc} while computing root hash (input {utf8.Length} bytes, top_kind={topEntityType})");
        }

        return new Hash32(rootHashBuf);
    }

    public static async ValueTask<TextDecomposeResult> EmitStaticAsync(
        IRecordSink sink,
        byte[] utf8,
        TextDecomposeOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(utf8);
        EnsureUcdLoaded();

        if (utf8.Length == 0)
        {
            Hash32 emptyHash = Hash32.Zero;
            EntityHandle empty = new(emptyHash, options.TopEntityType);
            return new TextDecomposeResult(
                RootHandle: empty, RootHash: emptyHash,
                EntitiesEmitted: 0, CompositionChildrenEmitted: 0,
                PhysicalityRowsEmitted: 0, SignificanceRowsEmitted: 0,
                RootCentroid: (0, 0, 0, 0));
        }

        BufferedEmitContext context = new(options);
        TextDecomposeNative.EmitCallback cb = context.OnRecord;

        byte[] rootHashBuf = new byte[32];
        double[] rootCentroidBuf = new double[4];
        GCHandle utf8Pin = GCHandle.Alloc(utf8, GCHandleType.Pinned);
        GCHandle rootPin = GCHandle.Alloc(rootHashBuf, GCHandleType.Pinned);
        GCHandle rootCentroidPin = GCHandle.Alloc(rootCentroidBuf, GCHandleType.Pinned);
        int rc;
        try
        {
            rc = TextDecomposeNative.TextDecompose(
                utf8Pin.AddrOfPinnedObject(),
                (nuint) utf8.Length,
                NativeKindFor(options.TopEntityType),
                options.TrustMu,
                cb,
                IntPtr.Zero,
                rootPin.AddrOfPinnedObject(),
                out _,
                rootCentroidPin.AddrOfPinnedObject());
        }
        finally
        {
            utf8Pin.Free();
            rootPin.Free();
            rootCentroidPin.Free();
            GC.KeepAlive(cb);
        }

        if (rc != 0)
        {
            throw new InvalidOperationException(
                $"hartonomous_text_decompose returned {rc} (input {utf8.Length} bytes, top_kind={options.TopEntityType})");
        }

        foreach (IngestionRecord record in context.Records)
        {
            await sink.EmitAsync(record, ct).ConfigureAwait(false);
        }

        Hash32 rootHash = new(rootHashBuf);
        EntityHandle rootHandle = new(rootHash, options.TopEntityType);
        await EmitTopEntityClassificationIfNeededAsync(sink, options, rootHash, ct).ConfigureAwait(false);

        return new TextDecomposeResult(
            RootHandle: rootHandle,
            RootHash: rootHash,
            EntitiesEmitted: context.EntityCount,
            CompositionChildrenEmitted: context.CompositionChildCount,
            PhysicalityRowsEmitted: context.PhysicalityCount,
            SignificanceRowsEmitted: context.SignificanceCount,
            RootCentroid: (rootCentroidBuf[0], rootCentroidBuf[1], rootCentroidBuf[2], rootCentroidBuf[3]));
    }

    private static int NativeKindFor(string topEntityType) => topEntityType switch
    {
        "codepoint"        => TextDecomposeNative.KindCodepoint,
        "grapheme_cluster" => TextDecomposeNative.KindGraphemeCluster,
        "word_form"        => TextDecomposeNative.KindWordForm,
        _                  => TextDecomposeNative.KindTextComposition,
    };

    private static string EntityCodeFor(int subkind) => subkind switch
    {
        TextDecomposeNative.KindCodepoint        => "codepoint",
        TextDecomposeNative.KindGraphemeCluster  => "grapheme_cluster",
        TextDecomposeNative.KindWordForm         => "word_form",
        TextDecomposeNative.KindTextComposition  => "text_composition",
        _                                        => "text_composition",
    };

    private static bool ShouldEmitEntity(TextDecomposeOptions options, string entityTypeCode, Hash32 hash)
        => options.EmissionCache?.TryRegisterEntity(entityTypeCode, hash, options.ProvenanceCode) ?? true;

    private static bool ShouldEmitPhysicality(TextDecomposeOptions options, string physicalityTypeCode, Hash32 entityHash)
        => options.EmissionCache?.TryRegisterPhysicality(physicalityTypeCode, entityHash) ?? true;

    private static bool ShouldEmitSequence(TextDecomposeOptions options, Hash32 parentHash, int ordinal)
        => options.EmissionCache?.TryRegisterCompositionChild(parentHash, ordinal) ?? true;

    private static bool ShouldEmitSignificance(
        TextDecomposeOptions options,
        string contextTypeCode,
        string attestationTypeCode,
        Hash32 entityHash)
        => options.EmissionCache?.TryRegisterSignificance(contextTypeCode, attestationTypeCode, entityHash) ?? true;

    private static void AddTopEntityClassificationIfNeeded(
        IIngestionBatch batch,
        TextDecomposeOptions options,
        Hash32 rootHash)
    {
        string nativeRootType = EntityCodeFor(NativeKindFor(options.TopEntityType));
        if (!string.Equals(nativeRootType, options.TopEntityType, StringComparison.Ordinal)
            && ShouldEmitEntity(options, options.TopEntityType, rootHash))
        {
            batch.AddEntity(rootHash, options.TopEntityType);
        }
    }

    private static async ValueTask EmitTopEntityClassificationIfNeededAsync(
        IRecordSink sink,
        TextDecomposeOptions options,
        Hash32 rootHash,
        CancellationToken ct)
    {
        string nativeRootType = EntityCodeFor(NativeKindFor(options.TopEntityType));
        if (!string.Equals(nativeRootType, options.TopEntityType, StringComparison.Ordinal)
            && ShouldEmitEntity(options, options.TopEntityType, rootHash))
        {
            await sink.EmitAsync(
                new EntityRecord(options.TopEntityType, rootHash, options.ProvenanceCode),
                ct).ConfigureAwait(false);
        }
    }

    private sealed class EmitContext
    {
        public IIngestionBatch Batch { get; }
        public TextDecomposeOptions Options { get; }
        public Dictionary<Hash32, string> KindByHash { get; } = new();
        private readonly List<PendingCompositionChild> _compositionChildren = [];
        public long EntityCount;
        public long CompositionChildCount;
        public long PhysicalityCount;
        public long SignificanceCount;

        public EmitContext(IIngestionBatch batch, TextDecomposeOptions options)
        {
            Batch = batch;
            Options = options;
        }

        public int OnRecord(IntPtr ctx, ref TextDecomposeRecord record)
        {
            switch (record.Kind)
            {
                case TextDecomposeNative.RecEntity:
                {
                    Hash32 hash = ReadHash(record.HashA);
                    string code = EntityCodeFor(record.Subkind);
                    KindByHash[hash] = code;
                    if (ShouldEmitEntity(Options, code, hash))
                    {
                        Batch.AddEntity(hash, code);
                        EntityCount++;
                    }
                    break;
                }
                case TextDecomposeNative.RecClassification:
                    break;
                case TextDecomposeNative.RecPhysicality:
                {
                    Hash32 entHash = ReadHash(record.HashA);
                    string entityCode = KindByHash.TryGetValue(entHash, out string? c)
                        ? c : "text_composition";
                    EntityHandle eh = new(entHash, entityCode);
                    string physCode = record.Subkind switch
                    {
                        TextDecomposeNative.PhysS3Position => "s3_position",
                        TextDecomposeNative.PhysContour    => "contour",
                        _                                   => "contour",
                    };
                    if (!ShouldEmitPhysicality(Options, physCode, entHash))
                    {
                        break;
                    }
                    byte[] geometry = ReadBytes(record.Geometry, (int) record.GeometryLen);
                    Batch.AddPhysicality(eh, physCode, geometry, new Hartonomous.Core.Geometry.Point4D(
                        record.CentroidX,
                        record.CentroidY,
                        record.CentroidZ,
                        record.CentroidM));
                    PhysicalityCount++;
                    break;
                }
                case TextDecomposeNative.RecSequence:
                {
                    Hash32 parentHash = ReadHash(record.HashA);
                    Hash32 childHash  = ReadHash(record.HashB);
                    string parentCode = KindByHash.TryGetValue(parentHash, out string? pc)
                        ? pc : "text_composition";
                    string childCode = KindByHash.TryGetValue(childHash, out string? cc)
                        ? cc : "text_composition";
                    EntityHandle parent = new(parentHash, parentCode);
                    EntityHandle child  = new(childHash,  childCode);
                    AddCompositionChild(parent, record.IntParam, child);
                    break;
                }
                case TextDecomposeNative.RecSignificance:
                {
                    Hash32 entHash = ReadHash(record.HashA);
                    string entityCode = KindByHash.TryGetValue(entHash, out string? c)
                        ? c : "text_composition";
                    EntityHandle eh = new(entHash, entityCode);
                    string ctxCode = record.Subkind switch
                    {
                        TextDecomposeNative.SigSourceAuthority => "source_authority",
                        _                                       => "source_authority",
                    };
                    const string attestationTypeCode = "provenance_authority_corroboration";
                    if (ShouldEmitSignificance(Options, ctxCode, attestationTypeCode, entHash))
                    {
                        Batch.AddSignificance(eh, ctxCode, record.DoubleParam, attestationTypeCode);
                        SignificanceCount++;
                    }
                    break;
                }
            }
            return 0;
        }

        public void FlushCompositionChildren()
        {
            foreach (PendingCompositionChild child in _compositionChildren)
            {
                Batch.AddCompositionChild(child.Parent, child.Ordinal, child.Child, child.RleCount);
            }
        }

        private void AddCompositionChild(EntityHandle parent, int ordinal, EntityHandle child)
        {
            if (!ShouldEmitSequence(Options, parent.Hash, ordinal))
            {
                return;
            }

            if (_compositionChildren.Count > 0)
            {
                PendingCompositionChild tail = _compositionChildren[^1];
                if (tail.Ordinal + tail.RleCount == ordinal &&
                    tail.Parent.Hash == parent.Hash &&
                    tail.Child.Hash == child.Hash)
                {
                    _compositionChildren[^1] = tail with { RleCount = tail.RleCount + 1 };
                    return;
                }
            }

            _compositionChildren.Add(new PendingCompositionChild(parent, ordinal, child, 1));
            CompositionChildCount++;
        }

        private static Hash32 ReadHash(IntPtr ptr)
        {
            byte[] dst = new byte[32];
            if (ptr != IntPtr.Zero)
            {
                Marshal.Copy(ptr, dst, 0, 32);
            }
            return new Hash32(dst);
        }

        private static byte[] ReadBytes(IntPtr ptr, int len)
        {
            byte[] dst = new byte[len];
            if (ptr != IntPtr.Zero && len > 0)
            {
                Marshal.Copy(ptr, dst, 0, len);
            }
            return dst;
        }

        private sealed record PendingCompositionChild(EntityHandle Parent, int Ordinal, EntityHandle Child, int RleCount);
    }

    private sealed class BufferedEmitContext
    {
        public TextDecomposeOptions Options { get; }
        public Dictionary<Hash32, string> KindByHash { get; } = new();
        public List<IngestionRecord> Records { get; } = [];
        public long EntityCount;
        public long CompositionChildCount;
        public long PhysicalityCount;
        public long SignificanceCount;

        public BufferedEmitContext(TextDecomposeOptions options)
        {
            Options = options;
        }

        public int OnRecord(IntPtr ctx, ref TextDecomposeRecord record)
        {
            switch (record.Kind)
            {
                case TextDecomposeNative.RecEntity:
                {
                    Hash32 hash = ReadHash(record.HashA);
                    string code = EntityCodeFor(record.Subkind);
                    KindByHash[hash] = code;
                    if (ShouldEmitEntity(Options, code, hash))
                    {
                        Records.Add(new EntityRecord(code, hash, Options.ProvenanceCode));
                        EntityCount++;
                    }
                    break;
                }
                case TextDecomposeNative.RecClassification:
                    break;
                case TextDecomposeNative.RecPhysicality:
                {
                    Hash32 entHash = ReadHash(record.HashA);
                    string physCode = record.Subkind switch
                    {
                        TextDecomposeNative.PhysS3Position => "s3_position",
                        TextDecomposeNative.PhysContour    => "contour",
                        _                                   => "contour",
                    };
                    if (!ShouldEmitPhysicality(Options, physCode, entHash))
                    {
                        break;
                    }
                    byte[] geometry = ReadBytes(record.Geometry, (int) record.GeometryLen);
                    Hartonomous.Core.Geometry.Point4D centroid = new(
                        record.CentroidX,
                        record.CentroidY,
                        record.CentroidZ,
                        record.CentroidM);
                    Records.Add(new PhysicalityRecord(
                        physCode,
                        entHash,
                        Blake3.Hash32(geometry.AsSpan()),
                        geometry,
                        centroid));
                    PhysicalityCount++;
                    break;
                }
                case TextDecomposeNative.RecSequence:
                {
                    Hash32 parentHash = ReadHash(record.HashA);
                    Hash32 childHash = ReadHash(record.HashB);
                    AddCompositionChild(parentHash, record.IntParam, childHash);
                    break;
                }
                case TextDecomposeNative.RecSignificance:
                {
                    Hash32 entHash = ReadHash(record.HashA);
                    string ctxCode = record.Subkind switch
                    {
                        TextDecomposeNative.SigSourceAuthority => "source_authority",
                        _                                       => "source_authority",
                    };
                    // Native text_decompose ships source_authority priors —
                    // attestation_type 'provenance_authority_corroboration'
                    // is the canonical match for ingestion-time priming.
                    const string attestationTypeCode = "provenance_authority_corroboration";
                    if (ShouldEmitSignificance(Options, ctxCode, attestationTypeCode, entHash))
                    {
                        Records.Add(new EntitySignificanceRecord(
                            ctxCode, attestationTypeCode, entHash, record.DoubleParam));
                        SignificanceCount++;
                    }
                    break;
                }
            }
            return 0;
        }

        private void AddCompositionChild(Hash32 parentHash, int ordinal, Hash32 childHash)
        {
            if (!ShouldEmitSequence(Options, parentHash, ordinal))
            {
                return;
            }

            if (Records.Count > 0 &&
                Records[^1] is CompositionChildRecord tail &&
                tail.Ordinal + tail.RleCount == ordinal &&
                tail.ParentEntityHash == parentHash &&
                tail.ChildEntityHash == childHash)
            {
                Records[^1] = tail with { RleCount = tail.RleCount + 1 };
                return;
            }

            Records.Add(new CompositionChildRecord(parentHash, ordinal, childHash, 1));
            CompositionChildCount++;
        }

        private static Hash32 ReadHash(IntPtr ptr)
        {
            byte[] dst = new byte[32];
            if (ptr != IntPtr.Zero)
            {
                Marshal.Copy(ptr, dst, 0, 32);
            }
            return new Hash32(dst);
        }

        private static byte[] ReadBytes(IntPtr ptr, int len)
        {
            byte[] dst = new byte[len];
            if (ptr != IntPtr.Zero && len > 0)
            {
                Marshal.Copy(ptr, dst, 0, len);
            }
            return dst;
        }
    }
}
