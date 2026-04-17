namespace Hartonomous.Core.Text.Tokenizers;

/// <summary>
/// One step in a tokenizer's normalizer chain. <see cref="Kind"/> is the
/// tokenizer-family name (<c>"NFC"</c>, <c>"Lowercase"</c>, <c>"BertNormalizer"</c>,
/// <c>"Replace"</c>, etc.); <see cref="Parameters"/> carries the kind-specific
/// configuration verbatim so the substrate records behavior identity, not an
/// interpretation of it.
/// </summary>
public sealed record Normalizer(string Kind, IReadOnlyDictionary<string, string> Parameters);
