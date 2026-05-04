using System.Collections.Generic;

namespace Hartonomous.Core.Operations;

public abstract record OperationRequest
{
    public byte[]? SeedHash { get; init; }

    public string? PromptText { get; init; }

    public int? MaxDepth { get; init; }

    public int? MaxResults { get; init; }

    public string? ArenaCode { get; init; }

    public double? SignificanceFloor { get; init; }

    public string? SourceLanguageCode { get; init; }

    public string? TargetLanguageCode { get; init; }

    public string? OutputModalityCode { get; init; }

    public IReadOnlyDictionary<string, string>? ExtraOptions { get; init; }
}
