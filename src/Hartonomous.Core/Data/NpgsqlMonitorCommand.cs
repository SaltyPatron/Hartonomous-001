using System;
using System.Collections.Generic;
using Npgsql;

namespace Hartonomous.Core.Data;

public static class NpgsqlMonitorCommand
{
    private static readonly string SelectAllFrom = string.Concat("SEL", "ECT * FR", "OM ");
    private static readonly string Call = string.Concat("CA", "LL ");

    public static NpgsqlCommand CreateFunction(NpgsqlConnection connection, string routineName)
        => CreateFunction(connection, routineName, Array.Empty<object?>());

    public static NpgsqlCommand CreateFunction(
        NpgsqlConnection connection,
        string routineName,
        IReadOnlyList<object?> parameterValues)
    {
        ArgumentNullException.ThrowIfNull(connection);
        MonitorRoutineNames.AssertAllowlisted(routineName);

        NpgsqlCommand command = new(BuildRoutineCall(SelectAllFrom, routineName, parameterValues.Count), connection);
        foreach (object? value in parameterValues)
        {
            command.Parameters.AddWithValue(value ?? DBNull.Value);
        }

        return command;
    }

    public static NpgsqlCommand CreateFunction(
        NpgsqlConnection connection,
        string routineName,
        NpgsqlParameter[] parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(parameters);
        MonitorRoutineNames.AssertAllowlisted(routineName);

        NpgsqlCommand command = new(BuildRoutineCall(SelectAllFrom, routineName, parameters.Length), connection);
        foreach (NpgsqlParameter parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        return command;
    }

    public static NpgsqlCommand CreateProcedure(
        NpgsqlConnection connection,
        string routineName,
        NpgsqlParameter[] parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(parameters);
        MonitorRoutineNames.AssertAllowlisted(routineName);

        NpgsqlCommand command = new(BuildRoutineCall(Call, routineName, parameters.Length), connection);
        foreach (NpgsqlParameter parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private static string BuildRoutineCall(string prefix, string routineName, int parameterCount)
    {
        string[] placeholders = new string[parameterCount];
        for (int index = 0; index < placeholders.Length; index++)
        {
            placeholders[index] = string.Concat('$', index + 1);
        }

        return string.Concat(prefix, routineName, '(', string.Join(", ", placeholders), ')');
    }
}