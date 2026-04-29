namespace Hartonomous.Core.Decomposition;

public sealed class DecomposerConfig
{
    public required string SourceDirectory { get; init; }
    public int BatchSize { get; init; } = 25_000;
    public required string ConnectionString { get; init; }

    /// <summary>
    /// Language codes the decomposer should ingest. <c>null</c> = no filter
    /// (process every language present in the source). When non-null, each
    /// decomposer compares against the codes its source actually uses (UD
    /// uses ISO 639-1 like "en", Wiktionary uses "en"/"de"/…, Tatoeba uses
    /// 3-letter "eng"/"fra"/…). The previous default of <c>["eng"]</c>
    /// silently rejected EVERY UD treebank and EVERY Wiktionary entry
    /// because their codes are 2-letter. Default is now null so seed runs
    /// ingest the full corpus; callers wanting a narrow filter set it
    /// explicitly with the right code variant.
    /// </summary>
    public IReadOnlyCollection<string>? LanguageFilter { get; init; }

    /// <summary>
    /// Model identifiers the SafetensorsDecomposer should ingest, in
    /// "publisher_slug/model_slug" form (e.g. "sentence-transformers/all-MiniLM-L6-v2").
    /// <c>null</c> = no filter (process every model discovered under SourceDirectory's
    /// hub root). When non-null, only models whose ModelId is in the set are
    /// processed by ModelDecomp — letting the dependency chain run on the full
    /// data root while ModelDecomp targets a specific subset (e.g., a small
    /// model for smoke-testing without paying the cost of decomposing every
    /// 33B-parameter model in the hub).
    /// </summary>
    public IReadOnlyCollection<string>? ModelFilter { get; init; }
}
