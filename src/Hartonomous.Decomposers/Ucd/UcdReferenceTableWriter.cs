using Npgsql;

namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Populates UCD-specific reference tables (general_category, script, block, break_property)
/// and the wide codepoint_property junction table. Uses direct Npgsql because these operations
/// don't fit the entity ingestion pipeline's batch model.
/// </summary>
internal sealed class UcdReferenceTableWriter
{
    private readonly NpgsqlDataSource _dataSource;

    public UcdReferenceTableWriter(string connectionString)
    {
        NpgsqlDataSourceBuilder builder = new(connectionString);
        _dataSource = builder.Build();
    }

    public async Task<Dictionary<string, int>> PopulateGeneralCategoriesAsync(
        IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        // General categories have group_code and description that we derive from the code.
        foreach (string code in codes)
        {
            string groupCode = code.Length > 0 ? code[..1] : "C";
            string description = GetGeneralCategoryDescription(code);

            await using NpgsqlCommand cmd = new(
                "INSERT INTO substrate.general_category (code, group_code, description) " +
                "VALUES ($1, $2, $3) ON CONFLICT (code) DO NOTHING", conn);
            cmd.Parameters.AddWithValue(code);
            cmd.Parameters.AddWithValue(groupCode);
            cmd.Parameters.AddWithValue(description);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return await LoadCodeMapAsync(conn, "substrate.general_category", ct);
    }

    public async Task<Dictionary<string, int>> PopulateScriptsAsync(
        IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        foreach (string code in codes)
        {
            await using NpgsqlCommand cmd = new(
                "INSERT INTO substrate.script (code) VALUES ($1) ON CONFLICT (code) DO NOTHING", conn);
            cmd.Parameters.AddWithValue(code);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return await LoadCodeMapAsync(conn, "substrate.script", ct);
    }

    public async Task<Dictionary<string, int>> PopulateBlocksAsync(
        IReadOnlyDictionary<string, (int RangeStart, int RangeEnd)> blocks, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        foreach (KeyValuePair<string, (int RangeStart, int RangeEnd)> kv in blocks)
        {
            await using NpgsqlCommand cmd = new(
                "INSERT INTO substrate.block (code, range_start, range_end) " +
                "VALUES ($1, $2, $3) ON CONFLICT (code) DO NOTHING", conn);
            cmd.Parameters.AddWithValue(kv.Key);
            cmd.Parameters.AddWithValue(kv.Value.RangeStart);
            cmd.Parameters.AddWithValue(kv.Value.RangeEnd);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return await LoadCodeMapAsync(conn, "substrate.block", ct);
    }

    public async Task<Dictionary<(string Code, string Category), int>> PopulateBreakPropertiesAsync(
        IReadOnlyCollection<(string Code, string Category)> properties, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        foreach ((string code, string category) in properties)
        {
            await using NpgsqlCommand cmd = new(
                "INSERT INTO substrate.break_property (code, category) " +
                "VALUES ($1, $2) ON CONFLICT (code, category) DO NOTHING", conn);
            cmd.Parameters.AddWithValue(code);
            cmd.Parameters.AddWithValue(category);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        Dictionary<(string, string), int> result = new();
        await using NpgsqlCommand loadCmd = new(
            "SELECT id, code, category FROM substrate.break_property", conn);
        await using NpgsqlDataReader reader = await loadCmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[(reader.GetString(1), reader.GetString(2))] = reader.GetInt32(0);
        }
        return result;
    }

    public async Task WriteCodepointPropertiesAsync(
        IReadOnlyList<CodepointPropertyRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        // Batch insert using unnest for performance.
        long[] entityIds = new long[rows.Count];
        int[] gcIds = new int[rows.Count];
        int[] scriptIds = new int[rows.Count];
        int[] blockIds = new int[rows.Count];
        int?[] gcbIds = new int?[rows.Count];
        int?[] wbIds = new int?[rows.Count];
        int?[] sbIds = new int?[rows.Count];
        int?[] lbIds = new int?[rows.Count];

        for (int i = 0; i < rows.Count; i++)
        {
            entityIds[i] = rows[i].EntityId;
            gcIds[i] = rows[i].GeneralCategoryId;
            scriptIds[i] = rows[i].ScriptId;
            blockIds[i] = rows[i].BlockId;
            gcbIds[i] = rows[i].GcbId;
            wbIds[i] = rows[i].WbId;
            sbIds[i] = rows[i].SbId;
            lbIds[i] = rows[i].LbId;
        }

        await using NpgsqlCommand cmd = new(
            "INSERT INTO substrate.codepoint_property " +
            "(entity_id, general_category_id, script_id, block_id, gcb_id, wb_id, sb_id, lb_id) " +
            "SELECT * FROM unnest($1, $2, $3, $4, $5, $6, $7, $8) " +
            "ON CONFLICT (entity_id) DO NOTHING", conn);

        cmd.Parameters.AddWithValue(entityIds);
        cmd.Parameters.AddWithValue(gcIds);
        cmd.Parameters.AddWithValue(scriptIds);
        cmd.Parameters.AddWithValue(blockIds);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer, gcbIds);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer, wbIds);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer, sbIds);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer, lbIds);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    private static async Task<Dictionary<string, int>> LoadCodeMapAsync(
        NpgsqlConnection conn, string table, CancellationToken ct)
    {
        Dictionary<string, int> map = new(StringComparer.Ordinal);
        await using NpgsqlCommand cmd = new($"SELECT id, code FROM {table}", conn);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            map[reader.GetString(1)] = reader.GetInt32(0);
        }
        return map;
    }

    private static string GetGeneralCategoryDescription(string code)
    {
        return code switch
        {
            "Lu" => "Letter, uppercase",
            "Ll" => "Letter, lowercase",
            "Lt" => "Letter, titlecase",
            "Lm" => "Letter, modifier",
            "Lo" => "Letter, other",
            "Mn" => "Mark, nonspacing",
            "Mc" => "Mark, spacing combining",
            "Me" => "Mark, enclosing",
            "Nd" => "Number, decimal digit",
            "Nl" => "Number, letter",
            "No" => "Number, other",
            "Pc" => "Punctuation, connector",
            "Pd" => "Punctuation, dash",
            "Ps" => "Punctuation, open",
            "Pe" => "Punctuation, close",
            "Pi" => "Punctuation, initial quote",
            "Pf" => "Punctuation, final quote",
            "Po" => "Punctuation, other",
            "Sm" => "Symbol, math",
            "Sc" => "Symbol, currency",
            "Sk" => "Symbol, modifier",
            "So" => "Symbol, other",
            "Zs" => "Separator, space",
            "Zl" => "Separator, line",
            "Zp" => "Separator, paragraph",
            "Cc" => "Other, control",
            "Cf" => "Other, format",
            "Cs" => "Other, surrogate",
            "Co" => "Other, private use",
            "Cn" => "Other, not assigned",
            _ => code
        };
    }
}

internal readonly record struct CodepointPropertyRow(
    long EntityId,
    int GeneralCategoryId,
    int ScriptId,
    int BlockId,
    int? GcbId,
    int? WbId,
    int? SbId,
    int? LbId);
