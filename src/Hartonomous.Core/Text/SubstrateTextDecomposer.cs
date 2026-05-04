using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
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

    /// <summary>
    /// Constructor signature kept compatible with the prior Npgsql-backed
    /// version. The opaque object argument is ignored (it was an
    /// <c>NpgsqlDataSource</c> before; native pipeline doesn't need it,
    /// and Hartonomous.Core can't reference Npgsql).
    /// </summary>
    public SubstrateTextDecomposer(object? unused = null)
    {
        _ = unused;
        EnsureUcdLoaded();
    }

    /// <summary>
    /// Initialise the embedded UCD blob (idempotent, thread-safe). Probes
    /// the canonical install paths plus an explicit override via
    /// <c>HARTONOMOUS_UCD_BLOB_DIR</c>.
    /// </summary>
    public static void EnsureUcdLoaded()
    {
        if (Interlocked.CompareExchange(ref s_ucdLoaded, 1, 0) != 0)
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
            string idx = System.IO.Path.Combine(dir, "hartonomous-ucd-17.0.0.idx");
            if (!System.IO.File.Exists(idx))
            {
                continue;
            }
            int rc = TextDecomposeNative.UcdLoad(dir);
            if (rc == 0)
            {
                return;
            }
        }
        // Reset the flag so the next call retries — common in tests where
        // the env var is set after the first ctor.
        Interlocked.Exchange(ref s_ucdLoaded, 0);
        throw new InvalidOperationException(
            "SubstrateTextDecomposer: could not locate the UCD blob. Set "
            + "HARTONOMOUS_UCD_BLOB_DIR or install hartonomous-ucd to a "
            + "discoverable path. Probed: "
            + string.Join("; ", candidates));
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
    {
        ArgumentNullException.ThrowIfNull(batch);
        EnsureUcdLoaded();

        if (utf8.IsEmpty)
        {
            byte[] emptyHash = new byte[32];
            EntityHandle empty = new(emptyHash, options.TopEntityType);
            return new TextDecomposeResult(
                RootHandle: empty, RootHash: emptyHash,
                EntitiesEmitted: 0, SequenceRowsEmitted: 0,
                PhysicalityRowsEmitted: 0, SignificanceRowsEmitted: 0,
                RootCentroid: (0, 0, 0, 0));
        }

        EmitContext context = new(batch, options);
        TextDecomposeNative.EmitCallback cb = context.OnRecord;

        byte[] utf8Copy = utf8.ToArray();
        byte[] rootHashBuf = new byte[32];
        GCHandle utf8Pin = GCHandle.Alloc(utf8Copy, GCHandleType.Pinned);
        GCHandle rootPin = GCHandle.Alloc(rootHashBuf, GCHandleType.Pinned);
        int rc;
        try
        {
            rc = TextDecomposeNative.TextDecompose(
                utf8Pin.AddrOfPinnedObject(),
                (nuint) utf8Copy.Length,
                NativeKindFor(options.TopEntityType),
                options.TrustMu,
                cb,
                IntPtr.Zero,
                rootPin.AddrOfPinnedObject(),
                out _);
        }
        finally
        {
            utf8Pin.Free();
            rootPin.Free();
            GC.KeepAlive(cb);
        }

        if (rc != 0)
        {
            throw new InvalidOperationException(
                $"hartonomous_text_decompose returned {rc} (input {utf8Copy.Length} bytes, top_kind={options.TopEntityType})");
        }

        EntityHandle rootHandle = new(rootHashBuf, options.TopEntityType);
        batch.AddEntity(rootHashBuf, options.TopEntityType);

        return new TextDecomposeResult(
            RootHandle: rootHandle,
            RootHash: rootHashBuf,
            EntitiesEmitted: context.EntityCount,
            SequenceRowsEmitted: context.SequenceCount,
            PhysicalityRowsEmitted: context.PhysicalityCount,
            SignificanceRowsEmitted: context.SignificanceCount,
            RootCentroid: (0, 0, 0, 0));
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

    private sealed class EmitContext
    {
        public IIngestionBatch Batch { get; }
        public TextDecomposeOptions Options { get; }
        public Dictionary<Hash32, string> KindByHash { get; } = new();
        public long EntityCount;
        public long SequenceCount;
        public long PhysicalityCount;
        public long SignificanceCount;

        public EmitContext(IIngestionBatch batch, TextDecomposeOptions options)
        {
            Batch = batch;
            Options = options;
        }

        public int OnRecord(IntPtr ctx, ref TextDecomposeNative.Record record)
        {
            switch (record.Kind)
            {
                case TextDecomposeNative.RecEntity:
                {
                    byte[] hash = ReadHash(record.HashA);
                    string code = EntityCodeFor(record.Subkind);
                    KindByHash[new Hash32(hash)] = code;
                    Batch.AddEntity(hash, code);
                    EntityCount++;
                    break;
                }
                case TextDecomposeNative.RecClassification:
                    break;
                case TextDecomposeNative.RecPhysicality:
                {
                    byte[] entHash = ReadHash(record.HashA);
                    string entityCode = KindByHash.TryGetValue(new Hash32(entHash), out string? c)
                        ? c : "text_composition";
                    EntityHandle eh = new(entHash, entityCode);
                    string physCode = record.Subkind switch
                    {
                        TextDecomposeNative.PhysS3Position => "s3_position",
                        TextDecomposeNative.PhysContour    => "contour",
                        _                                   => "contour",
                    };
                    byte[] wkb = ReadBytes(record.Wkb, (int) record.WkbLen);
                    Batch.AddPhysicality(eh, physCode, wkb);
                    PhysicalityCount++;
                    break;
                }
                case TextDecomposeNative.RecSequence:
                {
                    byte[] parentHash = ReadHash(record.HashA);
                    byte[] childHash  = ReadHash(record.HashB);
                    string parentCode = KindByHash.TryGetValue(new Hash32(parentHash), out string? pc)
                        ? pc : "text_composition";
                    string childCode = KindByHash.TryGetValue(new Hash32(childHash), out string? cc)
                        ? cc : "text_composition";
                    EntityHandle parent = new(parentHash, parentCode);
                    EntityHandle child  = new(childHash,  childCode);
                    Batch.AddSequence(parent, record.IntParam, child, rleCount: 1);
                    SequenceCount++;
                    break;
                }
                case TextDecomposeNative.RecSignificance:
                {
                    byte[] entHash = ReadHash(record.HashA);
                    string entityCode = KindByHash.TryGetValue(new Hash32(entHash), out string? c)
                        ? c : "text_composition";
                    EntityHandle eh = new(entHash, entityCode);
                    string ctxCode = record.Subkind switch
                    {
                        TextDecomposeNative.SigSourceAuthority => "source_authority",
                        _                                       => "source_authority",
                    };
                    Batch.AddSignificance(eh, ctxCode, record.DoubleParam);
                    SignificanceCount++;
                    break;
                }
            }
            return 0;
        }

        private static byte[] ReadHash(IntPtr ptr)
        {
            byte[] dst = new byte[32];
            if (ptr != IntPtr.Zero)
            {
                Marshal.Copy(ptr, dst, 0, 32);
            }
            return dst;
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
