namespace Hartonomous.Core.Text.Tokenizers;

/// <summary>
/// Single vocabulary row parsed from a tokenizer artifact. <see cref="TokenBytes"/>
/// is the raw byte representation the tokenizer emits — it may include
/// whitespace markers (SentencePiece <c>▁</c>, GPT-2 <c>Ġ</c>), byte-fallback
/// sequences, or be exactly the codepoint bytes. The substrate stores these
/// bytes verbatim as part of the <c>bpe_token</c> entity signature.
/// </summary>
public sealed record VocabularyEntry(int TokenId, byte[] TokenBytes, bool IsSpecial);
