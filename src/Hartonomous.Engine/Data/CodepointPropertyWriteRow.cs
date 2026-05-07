using System.Text.Json.Serialization;

namespace Hartonomous.Engine.Data;

internal sealed record CodepointPropertyWriteRow(
    [property: JsonPropertyName("entity_type_id")] int EntityTypeId,
    [property: JsonPropertyName("entity_hash")] byte[] EntityHash,
    [property: JsonPropertyName("codepoint_value")] int CodepointValue,
    [property: JsonPropertyName("general_category_id")] int GeneralCategoryId,
    [property: JsonPropertyName("script_id")] int ScriptId,
    [property: JsonPropertyName("block_id")] int BlockId,
    [property: JsonPropertyName("gcb_id")] int? GcbId,
    [property: JsonPropertyName("wb_id")] int? WbId,
    [property: JsonPropertyName("sb_id")] int? SbId,
    [property: JsonPropertyName("lb_id")] int? LbId,
    [property: JsonPropertyName("is_extended_pictographic")] bool IsExtendedPictographic,
    [property: JsonPropertyName("ccc")] short Ccc,
    [property: JsonPropertyName("decomposition_type")] string? DecompositionType,
    [property: JsonPropertyName("decomposition_mapping")] int[]? DecompositionMapping,
    [property: JsonPropertyName("simple_case_fold")] int? SimpleCaseFold,
    [property: JsonPropertyName("full_case_fold")] int[]? FullCaseFold);
