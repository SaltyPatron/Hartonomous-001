namespace Hartonomous.Core.Text.Tokenizers;

/// <summary>
/// One BPE merge rule: concatenate <see cref="Left"/> and <see cref="Right"/>
/// to form a new token. Lower <see cref="Priority"/> values apply earlier —
/// priority matches the row index in the source merge table. Empty for
/// WordPiece / SentencePiece unigram tokenizers.
/// </summary>
public sealed record MergeRule(byte[] Left, byte[] Right, int Priority);
