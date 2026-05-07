using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Core.Data;

/// <summary>
/// Abstract base for substrate-function repositories. Owns once: NpgsqlDataSource
/// injection, connection lifetime, parameter binding, reader iteration, structured
/// logging. Concrete repositories add one method per substrate function and call
/// the generic <see cref="ExecuteSingleAsync{TResult}"/>,
/// <see cref="ExecuteSetAsync{TResult}"/>, or <see cref="ExecuteVoidAsync"/>
/// helpers — they never construct SQL strings, never open connections directly,
/// never handle the reader manually.
///
/// SQL is built as <c>SELECT * FROM {function}($1, $2, ...)</c> from a vetted
/// allowlist (<see cref="SubstrateFunctionNames"/>). Result records implement
/// <see cref="IRecordMappable{TSelf}"/> and map columns by ordinal in the order
/// declared by the SQL function's RETURNS TABLE.
/// </summary>
public abstract class BaseSubstrateRepository
{
    private readonly NpgsqlDataSource _dataSource;

    protected BaseSubstrateRepository(NpgsqlDataSource dataSource, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);
        _dataSource = dataSource;
        Logger = logger;
    }

    protected ILogger Logger { get; }

    /// <summary>
    /// Execute an allowlisted substrate function and map the first row to
    /// <typeparamref name="TResult"/>. Returns <c>default</c> if no rows.
    /// </summary>
    protected async Task<TResult?> ExecuteSingleAsync<TResult>(
        string functionName,
        NpgsqlParameter[] parameters,
        CancellationToken ct,
        int? commandTimeoutSeconds = null)
        where TResult : IRecordMappable<TResult>
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(conn, functionName, parameters);
        if (commandTimeoutSeconds.HasValue) { cmd.CommandTimeout = commandTimeoutSeconds.Value; }

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return default;
        }
        return TResult.MapFrom(reader);
    }

    /// <summary>
    /// Execute an allowlisted set-returning substrate function and materialize
    /// the full result set into an <see cref="IReadOnlyList{TResult}"/>.
    /// Matches the codebase's existing repository materialization convention.
    /// </summary>
    protected async Task<IReadOnlyList<TResult>> ExecuteSetAsync<TResult>(
        string functionName,
        NpgsqlParameter[] parameters,
        CancellationToken ct,
        int? commandTimeoutSeconds = null)
        where TResult : IRecordMappable<TResult>
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(conn, functionName, parameters);
        if (commandTimeoutSeconds.HasValue) { cmd.CommandTimeout = commandTimeoutSeconds.Value; }

        List<TResult> results = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(TResult.MapFrom(reader));
        }
        return results;
    }

    /// <summary>
    /// Execute an allowlisted write-effecting substrate function with no
    /// row materialization. Discards any output via ExecuteNonQuery semantics.
    /// </summary>
    protected async Task ExecuteVoidAsync(
        string functionName,
        NpgsqlParameter[] parameters,
        CancellationToken ct,
        int? commandTimeoutSeconds = null)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(conn, functionName, parameters);
        if (commandTimeoutSeconds.HasValue) { cmd.CommandTimeout = commandTimeoutSeconds.Value; }

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
