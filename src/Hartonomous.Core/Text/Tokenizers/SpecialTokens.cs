namespace Hartonomous.Core.Text.Tokenizers;

/// <summary>
/// Special-token ids declared by the tokenizer config.  <see cref="Additional"/>
/// captures any further tokens the config marks as special (e.g., per-model
/// control tokens, chat-template role markers).
/// </summary>
public sealed record SpecialTokens(
    int? Bos,
    int? Eos,
    int? Pad,
    int? Unk,
    int? Mask,
    IReadOnlyList<int> Additional);
