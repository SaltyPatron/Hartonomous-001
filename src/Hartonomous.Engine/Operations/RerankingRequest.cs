using Hartonomous.Core.Operations;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Engine.Operations;

public sealed record RerankingRequest : OperationRequest
{
    public required IReadOnlyList<Hash32> Candidates { get; init; }
}
