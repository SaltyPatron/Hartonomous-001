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
}
