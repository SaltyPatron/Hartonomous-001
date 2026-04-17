namespace Hartonomous.Core.Text.Tokenizers;

/// <summary>
/// Canonical, parser-agnostic representation of a trained tokenizer. Parsers
/// for tokenizer.json / SentencePiece protobuf / WordPiece vocab.txt / tiktoken
/// emit one of these. <see cref="ConfigHash"/> is BLAKE3 over the canonicalized
/// source artifact bytes and serves as the <c>tokenizer_model</c> entity
/// signature — two cosmetically different but semantically identical configs
/// deduplicate on the same hash.
/// </summary>
public sealed record TokenizerModel(
    TokenizerKind Kind,
    byte[] ConfigHash,
    IReadOnlyList<Normalizer> Normalizers,
    IReadOnlyList<PreTokenizer> PreTokenizers,
    IReadOnlyList<PostProcessor> PostProcessors,
    IReadOnlyDictionary<int, VocabularyEntry> Vocab,
    IReadOnlyList<MergeRule> Merges,
    SpecialTokens Specials);
