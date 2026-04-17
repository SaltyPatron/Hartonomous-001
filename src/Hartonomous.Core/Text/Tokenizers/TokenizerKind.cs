namespace Hartonomous.Core.Text.Tokenizers;

/// <summary>
/// Tokenizer algorithm family. Determines how <see cref="Tokenize.Encode"/>
/// walks the vocabulary — BPE merges, WordPiece longest-match, SentencePiece
/// unigram/BPE with sentencepiece markers, or tiktoken-style byte-BPE.
/// </summary>
public enum TokenizerKind : byte
{
    Unknown = 0,
    Bpe,
    ByteBpe,
    WordPiece,
    SentencePiece,
    Tiktoken,
    CharLevel,
}
