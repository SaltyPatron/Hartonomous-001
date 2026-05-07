using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace Hartonomous.Api.Endpoints;

internal static class ApiJson
{
    internal static JsonElement Read(NpgsqlDataReader reader, int ordinal) =>
        Read(reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal));

    internal static JsonElement Read(object? value)
    {
        string json = value is null or DBNull
            ? "[]"
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "[]";
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
