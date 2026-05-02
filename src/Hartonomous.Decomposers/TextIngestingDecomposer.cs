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
/// shared text DAG. Centralizes the previously copy-pasted IngestText helper
/// and adds a per-instance memoization cache so repeated content
/// (e.g., Wiktionary etymology boilerplate appearing in tens of thousands
/// of entries) short-circuits the codepoint → grapheme → word_form →
/// text_composition decompose path.
///
/// Subclasses override <see cref="CodepointProperties"/> and
/// <see cref="TrustPriorMu"/> to bind their per-decomposer constants.
///
/// Cache stats are emitted via <see cref="LogTextCacheStats"/> at the end
/// of decomposition so each run reports its hit ratio.
/// </summary>
public abstract partial class TextIngestingDecomposer : BaseDecomposer
{
    private readonly TextIngestionCache _textCache;

    protected TextIngestingDecomposer(
        DecomposerConfig config,
        ILogger logger,
        int textCacheCapacity = 100_000)
        : base(config, logger)
    {
        _textCache = new TextIngestionCache(textCacheCapacity);
    }

    protected abstract ICodepointProperties CodepointProperties { get; }

    protected abstract double TrustPriorMu { get; }

    /// <summary>
    /// Ingest a UTF-16 string as a text_composition entity. On cache hit,
    /// only the document entity is registered in the current batch (so
    /// downstream edges can FK to it); all sub-tree emission is skipped
    /// because the substrate either already has the rows from the first
    /// occurrence's flush, or will receive them via the same batch's drain.
    /// On cache miss, the full <c>codepoint → grapheme → word_form →
    /// text_composition</c> DAG is emitted and the resulting document hash
    /// is cached for subsequent calls.
    /// </summary>
    protected EntityHandle IngestText(IIngestionBatch batch, string text)
    {
        if (_textCache.TryGet(text, out byte[]? cachedHash))
        {
            return batch.AddEntity(cachedHash!, "text_composition");
        }
        // Canonical text decomposer — single authoritative path. Same content
        // from any decomposer collapses to one hash.
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        Hartonomous.Core.Text.TextDecomposeResult r = Hartonomous.Core.Text.CanonicalTextDecomposer.Emit(
            batch, utf8, CodepointProperties,
            new Hartonomous.Core.Text.TextDecomposeOptions(
                ProvenanceCode: ProvenanceCode,
                TopEntityType: "text_composition",
                TrustMu: TrustPriorMu));
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
