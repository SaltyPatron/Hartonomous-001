namespace Hartonomous.Core.Text.Tokenizers;

/// <summary>
/// One step in a tokenizer's pre-tokenizer chain (whitespace / punctuation
/// splitters, ByteLevel byte-to-char mapping, Metaspace markers, etc.).
/// </summary>
public sealed record PreTokenizer(string Kind, IReadOnlyDictionary<string, string> Parameters);
