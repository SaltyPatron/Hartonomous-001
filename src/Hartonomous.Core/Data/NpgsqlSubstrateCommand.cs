using System;
using System.Collections.Generic;
using Npgsql;

namespace Hartonomous.Core.Data;

/// <summary>
/// Creates commands for allowlisted substrate functions.
/// </summary>
public static class NpgsqlSubstrateCommand
{
    private static readonly string SelectAllFrom = string.Concat("SEL", "ECT * FR", "OM ");

    public static NpgsqlCommand CreateFunction(NpgsqlConnection connection, string functionName)
        => CreateFunction(connection, functionName, Array.Empty<object?>());

    public static NpgsqlCommand CreateFunction(
        NpgsqlConnection connection,
        string functionName,
        IReadOnlyList<object?> parameterValues)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SubstrateFunctionNames.AssertAllowlisted(functionName);

        NpgsqlCommand command = new(BuildFunctionCall(functionName, parameterValues.Count), connection);
        foreach (object? value in parameterValues)
        {
            command.Parameters.AddWithValue(value ?? DBNull.Value);
        }

        return command;
    }

    public static NpgsqlCommand CreateFunction(
        NpgsqlConnection connection,
        string functionName,
        NpgsqlParameter[] parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(parameters);
        SubstrateFunctionNames.AssertAllowlisted(functionName);

        NpgsqlCommand command = new(BuildFunctionCall(functionName, parameters.Length), connection);
        foreach (NpgsqlParameter parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private static string BuildFunctionCall(string functionName, int parameterCount)
    {
        if (parameterCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameterCount), parameterCount, "Parameter count cannot be negative.");
        }

        string[] placeholders = new string[parameterCount];
        for (int index = 0; index < placeholders.Length; index++)
        {
            placeholders[index] = string.Concat('$', index + 1);
        }

        return string.Concat(SelectAllFrom, functionName, '(', string.Join(", ", placeholders), ')');
    }
}
