namespace Hartonomous.Core.Text.Tokenizers;

/// <summary>
/// One step in a tokenizer's post-processor chain (BOS/EOS/CLS/SEP insertion,
/// pair-template application, etc.).
/// </summary>
public sealed record PostProcessor(string Kind, IReadOnlyDictionary<string, string> Parameters);
