namespace Hartonomous.Core.Decomposition;

public sealed class DecomposerConfig
{
    public required string SourceDirectory { get; init; }
    public int BatchSize { get; init; } = 25_000;
    public required string ConnectionString { get; init; }

    /// <summary>
    /// ISO 639-3 codes the decomposer should ingest. Multi-language sources
    /// (UD, OMW, Wiktionary, Tatoeba) MUST honor this filter and skip every
    /// entry whose language is not in the set. <c>null</c> = no filter (process
    /// every language present in the source — typically only valid for UCD,
    /// ISO 639, and English-only sources like WordNet).
    /// Default = English-only. Matches every test fixture, every probe in this
    /// repo, the only model corpora present (TinyLlama, Qwen Coder, Florence,
    /// DETR variants), and the Tatoeba audio bundle on disk (eng-only).
    /// </summary>
    public IReadOnlyCollection<string>? LanguageFilter { get; init; } = new[] { "eng" };
}
