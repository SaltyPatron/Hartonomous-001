using System;
using System.Text;
using System.Threading;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text.Segmentation;
using Hartonomous.Decomposers.Text;
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
/// Subclasses override <see cref="CodepointProperties"/> (legacy hook —
/// no longer referenced on the IngestText path; retained until W3C audit
/// completes) and <see cref="TrustPriorMu"/> to bind per-decomposer
/// constants.
/// </summary>
public abstract partial class TextIngestingDecomposer : BaseDecomposer
{
    private readonly TextIngestionCache _textCache;
    private readonly SubstrateTextDecomposer _substrateTextDecomposer;

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
        // The substrate-side call is one SPI invocation that does all the
        // work in C. Blocking on the async API here is fine — the cost is
        // dominated by the substrate execution, not the .NET continuation.
        Hartonomous.Core.Text.TextDecomposeResult r = _substrateTextDecomposer
            .EmitAsync(
                utf8,
                new Hartonomous.Core.Text.TextDecomposeOptions(
                    ProvenanceCode: ProvenanceCode,
                    TopEntityType: "text_composition",
                    TrustMu: TrustPriorMu),
                modelSourceId: null,
                ct: CancellationToken.None)
            .GetAwaiter().GetResult();
        _textCache.Add(text, r.RootHash);
        // Register the root on the batch so downstream batch.AddEdge calls
        // can express FKs via the returned handle. The duplicate-entity
        // INSERT that would result from the batch flush is caught by ON
        // CONFLICT (hash) DO NOTHING in the pipeline drain.
        return batch.AddEntity(r.RootHash, "text_composition");
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
