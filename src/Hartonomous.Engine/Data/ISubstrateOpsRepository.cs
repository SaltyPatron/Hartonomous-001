using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Operations.Results;

namespace Hartonomous.Engine.Data;

/// <summary>
/// Single seam between AI Ops (<c>Hartonomous.Engine.Operations</c>) and the
/// substrate function call surface. Every method maps to one allowlisted
/// substrate function (<see cref="Hartonomous.Core.Data.SubstrateFunctionNames"/>).
/// Op classes consume this interface; they do not open NpgsqlConnections,
/// construct SQL, or read NpgsqlDataReaders.
/// </summary>
public interface ISubstrateOpsRepository
{
    /// <summary>Calls <c>substrate.complete($1, $2, $3, $4)</c>.</summary>
    Task<CompleteResult?> CompleteAsync(
        byte[] seedHash, int maxDepth, int maxResults, string? langCode, CancellationToken ct);

    /// <summary>Calls <c>substrate.infer($1, $2, $3)</c>.</summary>
    Task<InferResult?> InferAsync(
        byte[] seedHash, int maxDepth, int maxResults, CancellationToken ct);

    /// <summary>Calls <c>substrate.classify($1, $2, $3)</c>; returns all rows.</summary>
    Task<IReadOnlyList<ClassifyResult>> ClassifyAsync(
        byte[] seedHash, string junctionKind, int k, CancellationToken ct);

    /// <summary>Calls <c>substrate.rerank(candidates BYTEA[], arena_code TEXT, k INT)</c>; returns all rows.</summary>
    Task<IReadOnlyList<RerankResult>> RerankAsync(
        byte[][] candidateHashes, string arenaCode, int k, CancellationToken ct);

    /// <summary>Calls <c>substrate.embed_lookup($1, $2, $3, $4, $5)</c>; returns all rows.</summary>
    Task<IReadOnlyList<EmbedLookupResult>> EmbedLookupAsync(
        byte[] seedHash, string entityTypeCode, int k, string distanceKind, double? threshold, CancellationToken ct);
}
