using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text.Segmentation;
using Hartonomous.Core.Text;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers;

/// <summary>
/// Base class for decomposers that ingest free-text strings (glosses,
/// examples, etymology text, IPA, hyphenations, etc.) into the substrate's
/// shared text DAG. Centralizes the IngestText helper and adds a per-instance
/// memoization cache so repeated content (e.g., Wiktionary etymology
/// boilerplate appearing in tens of thousands of entries) short-circuits
/// even the substrate-side round-trip.
///
/// Post-W3B: <see cref="IngestText"/> hands UTF-8 bytes to the
/// C-implemented <c>substrate.text_decompose</c> extension function via
/// <see cref="SubstrateTextDecomposer"/>. The codepoint/grapheme/word_form/
/// composition entities + their physicalities + sequence rows + significance
/// rows are emitted DIRECTLY by the extension to substrate core tables —
/// they never flow through the C# pipeline channels. Only the root entity
/// is registered on the batch so downstream edges can FK to it.
///
/// Subclasses override <see cref="CodepointProperties"/> for legacy metadata
/// lookup surfaces and <see cref="TrustPriorMu"/> to bind per-decomposer constants.
/// </summary>
public abstract partial class TextIngestingDecomposer : BaseDecomposer
{
    private readonly TextIngestionCache _textCache;
    private readonly SubstrateTextDecomposer _substrateTextDecomposer;

    // Centroid sidecar for the sync EmitText path. Same-text fast-path needs
    // the geometry on cache hit (synset LINESTRINGZM vertices, etc.) — the
    // hash cache alone doesn't carry it. Bounded by the hash cache's capacity
    // so memory is O(capacity); evictions on the hash cache leave stale
    // entries here briefly, but the next miss re-populates correctly.
    // ConcurrentDictionary so parallel-producer fan-out (ParallelChunkProcessor)
    // is safe — multiple worker tasks may TryGet/Set the same surface form
    // concurrently when ingesting the same content from different chunks.
    private readonly ConcurrentDictionary<string, (double X, double Y, double Z, double M)> _centroidCache = new(StringComparer.Ordinal);

    protected TextIngestingDecomposer(
        DecomposerConfig config,
        SubstrateTextDecomposer substrateTextDecomposer,
        ILogger logger,
        int textCacheCapacity = 100_000)
        : base(config, logger)
    {
        _textCache = new TextIngestionCache(textCacheCapacity);
        _substrateTextDecomposer = substrateTextDecomposer
            ?? throw new ArgumentNullException(nameof(substrateTextDecomposer));
    }

    protected abstract ICodepointProperties CodepointProperties { get; }

    protected abstract double TrustPriorMu { get; }

    /// <summary>
    /// Ingest a UTF-16 string as a text_composition entity. On cache hit,
    /// only the document entity is registered in the current batch (so
    /// downstream edges can FK to it); the substrate already has the
    /// codepoint/grapheme/word/composition DAG from a prior call. On cache
    /// miss, <see cref="SubstrateTextDecomposer.EmitAsync"/> hands UTF-8
    /// bytes to the C extension which writes the full DAG to substrate
    /// core tables in a single SPI call; we register the returned root
    /// hash on the batch and cache it for subsequent calls.
    /// </summary>
    protected EntityHandle IngestText(IIngestionBatch batch, string text)
    {
        if (_textCache.TryGet(text, out byte[]? cachedHash))
        {
            return batch.AddEntity(cachedHash!, "text_composition");
        }
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        // In-process native call. libhartonomous's hartonomous_text_decompose
        // walks the codepoint/grapheme/word/composition DAG against the
        // embedded UCD blob and fires a callback per emission; the callback
        // populates `batch`. No SQL roundtrip, no Postgres handshake — just
        // one P/Invoke + N callback fires.
        Hartonomous.Core.Text.TextDecomposeResult r = _substrateTextDecomposer.Emit(
            batch,
            utf8,
            new Hartonomous.Core.Text.TextDecomposeOptions(
                ProvenanceCode: ProvenanceCode,
                TopEntityType: "text_composition",
                TrustMu: TrustPriorMu));
        _textCache.Add(text, r.RootHash);
        return r.RootHandle;
    }

    protected async ValueTask<EntityHandle> IngestTextAsync(IRecordSink sink, string text, CancellationToken ct)
    {
        if (_textCache.TryGet(text, out byte[]? cachedHash))
        {
            return await EmitEntityAsync(sink, cachedHash!, "text_composition", ProvenanceCode, ct).ConfigureAwait(false);
        }
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        Hartonomous.Core.Text.TextDecomposeResult r = await _substrateTextDecomposer.EmitAsync(
            sink,
            utf8,
            new Hartonomous.Core.Text.TextDecomposeOptions(
                ProvenanceCode: ProvenanceCode,
                TopEntityType: "text_composition",
                TrustMu: TrustPriorMu),
            ct).ConfigureAwait(false);
        _textCache.Add(text, r.RootHash);
        return r.RootHandle;
    }

    /// <summary>
    /// Subclasses call this at the end of <c>DecomposeCoreAsync</c> to surface
    /// cache effectiveness in the log. Emits a single Information-level line
    /// with hits / misses / evictions / hit ratio.
    /// </summary>
    protected void LogTextCacheStats()
    {
        TextCacheLog.Stats(
            Logger,
            _textCache.Hits,
            _textCache.Misses,
            _textCache.Evictions,
            _textCache.SkippedTooLong,
            _textCache.Count,
            _textCache.Capacity,
            _textCache.HitRatio);
    }

    /// <summary>
    /// Override BaseDecomposer's hook to share the same TextIngestionCache
    /// between IngestTextAsync (long-form glosses/etymologies) and
    /// EmitTextAsync (lemmas/forms/relation targets/translation targets).
    /// Critical for Wiktionary throughput: the same English vocabulary words
    /// ("the", "of", "be", common form bases) appear thousands of times as
    /// translation/relation/etymology targets. Without this cache they would
    /// re-walk the full codepoint→grapheme→word_form→composition AST and
    /// re-emit hundreds of substrate records per occurrence. With the cache
    /// they short-circuit to a single EntityRecord registration.
    /// </summary>
    protected override bool TryGetCachedTextHash(string text, out byte[]? hash)
    {
        if (_textCache.TryGet(text, out byte[]? cached))
        {
            hash = cached;
            return true;
        }
        hash = null;
        return false;
    }

    protected override void CacheTextHash(string text, byte[] hash)
    {
        _textCache.Add(text, hash);
    }

    /// <summary>
    /// Centroid-aware override for the sync <c>EmitText</c> path. Returns
    /// (hash, centroid) on cache hit; both came from the same prior native
    /// decompose call and reflect the same content's geometry in this
    /// process.
    /// </summary>
    protected override bool TryGetCachedTextEntry(
        string text, out byte[]? hash, out (double X, double Y, double Z, double M) centroid)
    {
        if (_textCache.TryGet(text, out byte[]? cached) && _centroidCache.TryGetValue(text, out var c))
        {
            hash = cached;
            centroid = c;
            return true;
        }
        hash = null;
        centroid = default;
        return false;
    }

    protected override void CacheTextEntry(string text, byte[] hash, (double X, double Y, double Z, double M) centroid)
    {
        _textCache.Add(text, hash);
        // ConcurrentDictionary indexer is atomic; last-writer-wins is fine
        // because identical content from any concurrent task produces an
        // identical centroid (deterministic native decompose).
        _centroidCache[text] = centroid;
    }

    private static partial class TextCacheLog
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Text cache: hits={Hits} misses={Misses} evictions={Evictions} skipped_too_long={SkippedTooLong} size={Size}/{Capacity} hit_ratio={HitRatio:P1}")]
        public static partial void Stats(
            ILogger logger,
            long hits,
            long misses,
            long evictions,
            long skippedTooLong,
            int size,
            int capacity,
            double hitRatio);
    }
}
