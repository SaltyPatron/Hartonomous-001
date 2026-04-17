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

        string[] codeArr = new string[codes.Count];
        string[] groupArr = new string[codes.Count];
        string[] descArr = new string[codes.Count];
        int i = 0;
        foreach (string code in codes)
        {
            codeArr[i] = code;
            groupArr[i] = code.Length > 0 ? code[..1] : "C";
            descArr[i] = GetGeneralCategoryDescription(code);
            i++;
        }

        await using NpgsqlCommand cmd = new(
            "INSERT INTO substrate.general_category (code, group_code, description) " +
            "SELECT * FROM unnest($1, $2, $3) ON CONFLICT (code) DO NOTHING", conn);
        cmd.Parameters.AddWithValue(codeArr);
        cmd.Parameters.AddWithValue(groupArr);
        cmd.Parameters.AddWithValue(descArr);
        await cmd.ExecuteNonQueryAsync(ct);

        return await LoadCodeMapAsync(conn, "substrate.general_category", ct);
    }

    public async Task<Dictionary<string, int>> PopulateScriptsAsync(
        IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        string[] codeArr = [.. codes];
        await using NpgsqlCommand cmd = new(
            "INSERT INTO substrate.script (code) SELECT * FROM unnest($1) ON CONFLICT (code) DO NOTHING", conn);
        cmd.Parameters.AddWithValue(codeArr);
        await cmd.ExecuteNonQueryAsync(ct);

        return await LoadCodeMapAsync(conn, "substrate.script", ct);
    }

    public async Task<Dictionary<string, int>> PopulateBlocksAsync(
        IReadOnlyDictionary<string, (int RangeStart, int RangeEnd)> blocks, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        string[] codeArr = new string[blocks.Count];
        int[] startArr = new int[blocks.Count];
        int[] endArr = new int[blocks.Count];
        int i = 0;
        foreach (KeyValuePair<string, (int RangeStart, int RangeEnd)> kv in blocks)
        {
            codeArr[i] = kv.Key;
            startArr[i] = kv.Value.RangeStart;
            endArr[i] = kv.Value.RangeEnd;
            i++;
        }

        await using NpgsqlCommand cmd = new(
            "INSERT INTO substrate.block (code, range_start, range_end) " +
            "SELECT * FROM unnest($1, $2, $3) ON CONFLICT (code) DO NOTHING", conn);
        cmd.Parameters.AddWithValue(codeArr);
        cmd.Parameters.AddWithValue(startArr);
        cmd.Parameters.AddWithValue(endArr);
        await cmd.ExecuteNonQueryAsync(ct);

        return await LoadCodeMapAsync(conn, "substrate.block", ct);
    }

    public async Task<Dictionary<(string Code, string Category), int>> PopulateBreakPropertiesAsync(
        IReadOnlyCollection<(string Code, string Category)> properties, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        string[] codeArr = new string[properties.Count];
        string[] catArr = new string[properties.Count];
        int i = 0;
        foreach ((string code, string category) in properties)
        {
            codeArr[i] = code;
            catArr[i] = category;
            i++;
        }

        await using NpgsqlCommand cmd = new(
            "INSERT INTO substrate.break_property (code, category) " +
            "SELECT * FROM unnest($1, $2) ON CONFLICT (code, category) DO NOTHING", conn);
        cmd.Parameters.AddWithValue(codeArr);
        cmd.Parameters.AddWithValue(catArr);
        await cmd.ExecuteNonQueryAsync(ct);

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

        // Binary COPY — supports per-row variable-length arrays (decomposition_mapping,
        // full_case_fold) which unnest cannot express without rectangular 2D arrays.
        const string copyCommand =
            "COPY substrate.codepoint_property " +
            "(entity_id, general_category_id, script_id, block_id, gcb_id, wb_id, sb_id, lb_id, " +
            " is_extended_pictographic, ccc, decomposition_type, decomposition_mapping, " +
            " simple_case_fold, full_case_fold) " +
            "FROM STDIN (FORMAT binary)";

        await using (NpgsqlBinaryImporter importer = await conn.BeginBinaryImportAsync(copyCommand, ct))
        {
            foreach (CodepointPropertyRow row in rows)
            {
                await importer.StartRowAsync(ct);
                await importer.WriteAsync(row.EntityId, NpgsqlTypes.NpgsqlDbType.Bigint, ct);
                await importer.WriteAsync(row.GeneralCategoryId, NpgsqlTypes.NpgsqlDbType.Integer, ct);
                await importer.WriteAsync(row.ScriptId, NpgsqlTypes.NpgsqlDbType.Integer, ct);
                await importer.WriteAsync(row.BlockId, NpgsqlTypes.NpgsqlDbType.Integer, ct);
                await WriteNullableInt(importer, row.GcbId, ct);
                await WriteNullableInt(importer, row.WbId, ct);
                await WriteNullableInt(importer, row.SbId, ct);
                await WriteNullableInt(importer, row.LbId, ct);
                await importer.WriteAsync(row.IsExtendedPictographic, NpgsqlTypes.NpgsqlDbType.Boolean, ct);
                await importer.WriteAsync(row.Ccc, NpgsqlTypes.NpgsqlDbType.Smallint, ct);
                if (row.DecompositionType is null)
                {
                    await importer.WriteNullAsync(ct);
                }
                else
                {
                    await importer.WriteAsync(row.DecompositionType, NpgsqlTypes.NpgsqlDbType.Text, ct);
                }
                if (row.DecompositionMapping is null)
                {
                    await importer.WriteNullAsync(ct);
                }
                else
                {
                    await importer.WriteAsync(row.DecompositionMapping,
                        NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer, ct);
                }
                await WriteNullableInt(importer, row.SimpleCaseFold, ct);
                if (row.FullCaseFold is null)
                {
                    await importer.WriteNullAsync(ct);
                }
                else
                {
                    await importer.WriteAsync(row.FullCaseFold,
                        NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer, ct);
                }
            }
            await importer.CompleteAsync(ct);
        }
    }

    private static async Task WriteNullableInt(NpgsqlBinaryImporter importer, int? value, CancellationToken ct)
    {
        if (value is null)
        {
            await importer.WriteNullAsync(ct);
        }
        else
        {
            await importer.WriteAsync(value.Value, NpgsqlTypes.NpgsqlDbType.Integer, ct);
        }
    }

    public async Task<HashSet<long>> LoadCodepointPropertyEntityIdsAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        HashSet<long> ids = new();
        await using NpgsqlCommand cmd = new(
            "SELECT entity_id FROM substrate.codepoint_property", conn);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
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

