using Hartonomous.Core.Operations;

namespace Hartonomous.Engine.Operations;

public sealed record RerankingRequest : OperationRequest
{
    public required IReadOnlyList<byte[]> Candidates { get; init; }
}
