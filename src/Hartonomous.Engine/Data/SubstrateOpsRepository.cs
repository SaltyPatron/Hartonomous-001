using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Hartonomous.Core.Operations.Results;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Engine.Data;

/// <summary>
/// Concrete repository for the AI Op substrate function surface. Every public
/// method here is one substrate function call. The base class
/// (<see cref="BaseSubstrateRepository"/>) owns connection lifetime, SQL
/// construction, parameter binding, reader iteration, and result mapping;
/// this class only declares the per-function parameter shape and result type.
/// </summary>
public sealed class SubstrateOpsRepository : BaseSubstrateRepository, ISubstrateOpsRepository
{
    private const int CompleteTimeoutSeconds = 300;
    private const int InferTimeoutSeconds = 300;

    public SubstrateOpsRepository(NpgsqlDataSource dataSource, ILogger<SubstrateOpsRepository> logger)
        : base(dataSource, logger)
    {
    }

    public Task<CompleteResult?> CompleteAsync(
        byte[] seedHash, int maxDepth, int maxResults, string? langCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seedHash);
        NpgsqlParameter[] parameters =
        [
            new() { Value = seedHash },
            new() { Value = maxDepth },
            new() { Value = maxResults },
            new() { Value = (object?)langCode ?? DBNull.Value },
        ];
        return ExecuteSingleAsync<CompleteResult>(
            SubstrateFunctionNames.Complete, parameters, ct, CompleteTimeoutSeconds);
    }

    public Task<InferResult?> InferAsync(
        byte[] seedHash, int maxDepth, int maxResults, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seedHash);
        NpgsqlParameter[] parameters =
        [
            new() { Value = seedHash },
            new() { Value = maxDepth },
            new() { Value = maxResults },
        ];
        return ExecuteSingleAsync<InferResult>(
            SubstrateFunctionNames.Infer, parameters, ct, InferTimeoutSeconds);
    }

    public Task<IReadOnlyList<ClassifyResult>> ClassifyAsync(
        byte[] seedHash, string junctionKind, int k, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seedHash);
        ArgumentException.ThrowIfNullOrEmpty(junctionKind);
        NpgsqlParameter[] parameters =
        [
            new() { Value = seedHash },
            new() { Value = junctionKind },
            new() { Value = k },
        ];
        return ExecuteSetAsync<ClassifyResult>(SubstrateFunctionNames.Classify, parameters, ct);
    }

    public Task<IReadOnlyList<RerankResult>> RerankAsync(
        byte[][] candidateHashes, string arenaCode, int k, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidateHashes);
        ArgumentException.ThrowIfNullOrEmpty(arenaCode);
        NpgsqlParameter[] parameters =
        [
            new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = candidateHashes },
            new() { Value = arenaCode },
            new() { Value = k },
        ];
        return ExecuteSetAsync<RerankResult>(SubstrateFunctionNames.Rerank, parameters, ct);
    }

    public Task<IReadOnlyList<EmbedLookupResult>> EmbedLookupAsync(
        byte[] seedHash, string entityTypeCode, int k, string distanceKind, double? threshold, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seedHash);
        ArgumentException.ThrowIfNullOrEmpty(entityTypeCode);
        ArgumentException.ThrowIfNullOrEmpty(distanceKind);
        NpgsqlParameter[] parameters =
        [
            new() { Value = seedHash },
            new() { Value = entityTypeCode },
            new() { Value = k },
            new() { Value = distanceKind },
            new() { Value = (object?)threshold ?? DBNull.Value },
        ];
        return ExecuteSetAsync<EmbedLookupResult>(SubstrateFunctionNames.EmbedLookup, parameters, ct);
    }
}
