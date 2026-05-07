using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Engine.Data;

/// <summary>
/// Writes reference/classification data (edge types, morph features) into
/// substrate tables. Consolidates the inline SQL from
/// <c>BaseReferenceTableWriter.UpsertEdgeTypeAsync</c> and
/// <c>BaseReferenceTableWriter.PopulateMorphFeaturesAsync</c>.
/// </summary>
public sealed class NpgsqlReferenceDataWriter : IReferenceDataWriter
{
    private const int ChunkSize = 50_000;
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlReferenceDataWriter(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task UpsertEdgeTypeAsync(
        string code, string category,
        string sourceEntityType, string targetEntityType,
        CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.UpsertReferenceEdgeType,
            new object?[] { code, category, sourceEntityType, targetEntityType });
        _ = await cmd.ExecuteScalarAsync(ct);
    }

    public async Task PopulateMorphFeaturesAsync(
        IReadOnlyCollection<(string Key, string Value)> features,
        CancellationToken ct)
    {
        if (features.Count == 0)
        {
            return;
        }

        (string Key, string Value)[] arr = new (string, string)[features.Count];
        int idx = 0;
        foreach ((string k, string v) in features)
        {
            arr[idx++] = (k, v);
        }
        Array.Sort(arr, (a, b) =>
        {
            int kc = string.CompareOrdinal(a.Key, b.Key);
            return kc != 0 ? kc : string.CompareOrdinal(a.Value, b.Value);
        });

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        for (int offset = 0; offset < arr.Length; offset += ChunkSize)
        {
            int count = Math.Min(ChunkSize, arr.Length - offset);
            string[] keys = new string[count];
            string[] values = new string[count];
            for (int j = 0; j < count; j++)
            {
                keys[j] = arr[offset + j].Key;
                values[j] = arr[offset + j].Value;
            }

            await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
                conn,
                SubstrateFunctionNames.PopulateMorphFeatures,
                new object?[] { keys, values });
            _ = await cmd.ExecuteScalarAsync(ct);
        }
    }

    public async Task PopulateDeprelsAsync(
        IReadOnlyCollection<string> deprels,
        CancellationToken ct)
    {
        if (deprels.Count == 0)
        {
            return;
        }

        string[] codes = new string[deprels.Count];
        int index = 0;
        foreach (string deprel in deprels)
        {
            codes[index++] = deprel;
        }
        Array.Sort(codes, StringComparer.Ordinal);

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        for (int offset = 0; offset < codes.Length; offset += ChunkSize)
        {
            int count = Math.Min(ChunkSize, codes.Length - offset);
            string[] chunk = new string[count];
            Array.Copy(codes, offset, chunk, 0, count);

            await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
                conn,
                SubstrateFunctionNames.PopulateDeprels,
                new object?[] { chunk });
            _ = await cmd.ExecuteScalarAsync(ct);
        }
    }

    public async Task PopulateLanguagesAsync(
        IReadOnlyList<(
            string Code,
            string Name,
            string Scope,
            string Type,
            string? Part1,
            string? Part2B,
            string? Part2T)> records,
        CancellationToken ct)
    {
        if (records.Count == 0)
        {
            return;
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        string[] codes = new string[records.Count];
        string[] names = new string[records.Count];
        string[] scopes = new string[records.Count];
        string[] types = new string[records.Count];
        string?[] part1s = new string?[records.Count];
        string?[] part2bs = new string?[records.Count];
        string?[] part2ts = new string?[records.Count];

        for (int i = 0; i < records.Count; i++)
        {
            codes[i] = records[i].Code;
            names[i] = records[i].Name;
            scopes[i] = records[i].Scope;
            types[i] = records[i].Type;
            part1s[i] = records[i].Part1;
            part2bs[i] = records[i].Part2B;
            part2ts[i] = records[i].Part2T;
        }

        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.PopulateLanguages,
            new[]
            {
                new NpgsqlParameter { Value = codes },
                new NpgsqlParameter { Value = names },
                new NpgsqlParameter { Value = scopes },
                new NpgsqlParameter { Value = types },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = part1s },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = part2bs },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = part2ts },
            });
        _ = await cmd.ExecuteScalarAsync(ct);
    }

    public Task UpdateLanguageNameEntityIdsAsync(
        IReadOnlyList<(string Code, byte[] NameHash)> updates,
        CancellationToken ct)
    {
        // No-op in the hash-as-PK substrate. The substrate.language reference
        // table doesn't carry a back-pointer to its name entity any more —
        // entity_language junctions are the canonical link, and the language's
        // name entity is reachable directly by hash. Decomposers still call
        // this for symmetry with the prior schema; it intentionally does
        // nothing.
        _ = updates; _ = ct;
        return Task.CompletedTask;
    }

    public async Task<int> EnsureArchitectureClassAsync(
        string code,
        CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.UpsertArchitectureClass,
            new[]
            {
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = code },
            });
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<int> EnsureModelRegistryAsync(
        string code,
        string displayName,
        CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.UpsertModelRegistry,
            new[]
            {
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = code },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = displayName },
            });
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<int> EnsureModelPublisherAsync(
        int registryId,
        string slug,
        string? displayName,
        CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.UpsertModelPublisher,
            new[]
            {
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = registryId },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = slug },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = (object?)displayName ?? DBNull.Value },
            });
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<long> EnsureModelSourceAsync(
        int registryId,
        int publisherId,
        string modelSlug,
        byte[] revision,
        CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.UpsertModelSource,
            new[]
            {
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = registryId },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = publisherId },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = modelSlug },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = revision },
            });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task PopulateGeneralCategoriesAsync(
        IReadOnlyCollection<(string Code, string GroupCode, string Description)> categories,
        CancellationToken ct)
    {
        if (categories.Count == 0)
        {
            return;
        }

        string[] codes = new string[categories.Count];
        string[] groupCodes = new string[categories.Count];
        string[] descriptions = new string[categories.Count];
        int index = 0;
        foreach ((string code, string groupCode, string description) in categories)
        {
            codes[index] = code;
            groupCodes[index] = groupCode;
            descriptions[index] = description;
            index++;
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.PopulateGeneralCategories,
            new object?[] { codes, groupCodes, descriptions });
        _ = await cmd.ExecuteScalarAsync(ct);
    }

    public async Task PopulateScriptsAsync(
        IReadOnlyCollection<string> codes,
        CancellationToken ct)
    {
        if (codes.Count == 0)
        {
            return;
        }

        string[] codeArray = [.. codes];
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.PopulateScripts,
            new object?[] { codeArray });
        _ = await cmd.ExecuteScalarAsync(ct);
    }

    public async Task PopulateBlocksAsync(
        IReadOnlyList<(string Code, int RangeStart, int RangeEnd)> blocks,
        CancellationToken ct)
    {
        if (blocks.Count == 0)
        {
            return;
        }

        string[] codes = new string[blocks.Count];
        int[] starts = new int[blocks.Count];
        int[] ends = new int[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            codes[i] = blocks[i].Code;
            starts[i] = blocks[i].RangeStart;
            ends[i] = blocks[i].RangeEnd;
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.PopulateBlocks,
            new object?[] { codes, starts, ends });
        _ = await cmd.ExecuteScalarAsync(ct);
    }

    public async Task PopulateBreakPropertiesAsync(
        IReadOnlyCollection<(string Code, string Category)> properties,
        CancellationToken ct)
    {
        if (properties.Count == 0)
        {
            return;
        }

        string[] codes = new string[properties.Count];
        string[] categories = new string[properties.Count];
        int index = 0;
        foreach ((string code, string category) in properties)
        {
            codes[index] = code;
            categories[index] = category;
            index++;
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.PopulateBreakProperties,
            new object?[] { codes, categories });
        _ = await cmd.ExecuteScalarAsync(ct);
    }

    public async Task WriteCodepointPropertiesAsync(
        IReadOnlyList<(
            byte[] EntityHash,
            int CodepointValue,
            int GeneralCategoryId,
            int ScriptId,
            int BlockId,
            int? GcbId,
            int? WbId,
            int? SbId,
            int? LbId,
            bool IsExtendedPictographic,
            short Ccc,
            string? DecompositionType,
            int[]? DecompositionMapping,
            int? SimpleCaseFold,
            int[]? FullCaseFold)> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        for (int offset = 0; offset < rows.Count; offset += ChunkSize)
        {
            int count = Math.Min(ChunkSize, rows.Count - offset);
            string payload = SerializeCodepointPropertyPayload(rows, offset, count);
            await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateProcedure(
                conn,
                SubstrateProcedureNames.WriteCodepointProperties,
                [new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = payload }]);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task UpsertHomogeneousEdgeTypesAsync(
        IReadOnlyCollection<string> codes,
        string category,
        string entityTypeCode,
        CancellationToken ct)
    {
        if (codes.Count == 0)
        {
            return;
        }

        string[] sortedCodes = new string[codes.Count];
        int index = 0;
        foreach (string code in codes)
        {
            sortedCodes[index++] = code;
        }
        Array.Sort(sortedCodes, StringComparer.Ordinal);

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        for (int offset = 0; offset < sortedCodes.Length; offset += ChunkSize)
        {
            int count = Math.Min(ChunkSize, sortedCodes.Length - offset);
            string[] chunk = new string[count];
            for (int i = 0; i < count; i++)
            {
                chunk[i] = sortedCodes[offset + i];
            }

            await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
                conn,
                SubstrateFunctionNames.UpsertHomogeneousEdgeTypes,
                new object?[] { chunk, category, entityTypeCode });
            _ = await cmd.ExecuteScalarAsync(ct);
        }
    }

    public async Task PopulateSensesAsync(
        IReadOnlyList<(string Code, string Gloss, int LexnameId, int PosId)> senses,
        CancellationToken ct)
    {
        if (senses.Count == 0)
        {
            return;
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        for (int offset = 0; offset < senses.Count; offset += ChunkSize)
        {
            int count = Math.Min(ChunkSize, senses.Count - offset);
            string[] codes = new string[count];
            string[] glosses = new string[count];
            int[] lexnameIds = new int[count];
            int[] posIds = new int[count];

            for (int i = 0; i < count; i++)
            {
                (codes[i], glosses[i], lexnameIds[i], posIds[i]) = senses[offset + i];
            }

            await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
                conn,
                SubstrateFunctionNames.PopulateSenses,
                new object?[] { codes, glosses, lexnameIds, posIds });
            _ = await cmd.ExecuteScalarAsync(ct);
        }
    }

    private static string SerializeCodepointPropertyPayload(
        IReadOnlyList<(
            byte[] EntityHash,
            int CodepointValue,
            int GeneralCategoryId,
            int ScriptId,
            int BlockId,
            int? GcbId,
            int? WbId,
            int? SbId,
            int? LbId,
            bool IsExtendedPictographic,
            short Ccc,
            string? DecompositionType,
            int[]? DecompositionMapping,
            int? SimpleCaseFold,
            int[]? FullCaseFold)> rows,
        int offset,
        int count)
    {
        ArrayBufferWriter<byte> buffer = new();
        using Utf8JsonWriter writer = new(buffer);

        writer.WriteStartArray();
        for (int rowIndex = offset; rowIndex < offset + count; rowIndex++)
        {
            var row = rows[rowIndex];
            writer.WriteStartObject();
            writer.WriteString("entity_hash_hex", Convert.ToHexString(row.EntityHash));
            writer.WriteNumber("codepoint_value", row.CodepointValue);
            writer.WriteNumber("general_category_id", row.GeneralCategoryId);
            writer.WriteNumber("script_id", row.ScriptId);
            writer.WriteNumber("block_id", row.BlockId);
            WriteNullableNumber(writer, "gcb_id", row.GcbId);
            WriteNullableNumber(writer, "wb_id", row.WbId);
            WriteNullableNumber(writer, "sb_id", row.SbId);
            WriteNullableNumber(writer, "lb_id", row.LbId);
            writer.WriteBoolean("is_extended_pictographic", row.IsExtendedPictographic);
            writer.WriteNumber("ccc", row.Ccc);
            WriteNullableString(writer, "decomposition_type", row.DecompositionType);
            WriteNullableNumberArray(writer, "decomposition_mapping", row.DecompositionMapping);
            WriteNullableNumber(writer, "simple_case_fold", row.SimpleCaseFold);
            WriteNullableNumberArray(writer, "full_case_fold", row.FullCaseFold);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string propertyName, int? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
            return;
        }

        writer.WriteNull(propertyName);
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(propertyName, value);
            return;
        }

        writer.WriteNull(propertyName);
    }

    private static void WriteNullableNumberArray(Utf8JsonWriter writer, string propertyName, int[]? values)
    {
        if (values is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteStartArray(propertyName);
        foreach (int value in values)
        {
            writer.WriteNumberValue(value);
        }

        writer.WriteEndArray();
    }
}
