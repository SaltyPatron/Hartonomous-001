namespace Hartonomous.Core.Operations;

public sealed record OperationCode(string Value)
{
    public static readonly OperationCode Infer = new("infer");
    public static readonly OperationCode Translate = new("translate");
    public static readonly OperationCode Summarize = new("summarize");
    public static readonly OperationCode Classify = new("classify");
    public static readonly OperationCode Rerank = new("rerank");
    public static readonly OperationCode EmbedLookup = new("embed_lookup");
    public static readonly OperationCode Complete = new("complete");
    public static readonly OperationCode Recompose = new("recompose");

    public override string ToString() => Value;

    public static implicit operator string(OperationCode code) => code.Value;

    public static explicit operator OperationCode(string value) => new(value);
}
