using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;

namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Populates UCD-specific reference tables (general_category, script, block, break_property)
/// and the wide codepoint_property junction table. Uses direct Npgsql because these
/// operations don't fit the entity ingestion pipeline's batch model; the generic
/// code→id loader and connection management come from <see cref="BaseReferenceTableWriter"/>.
/// </summary>
internal sealed class UcdReferenceTableWriter : BaseReferenceTableWriter
{
    public UcdReferenceTableWriter(IReferenceDataReader reader, IJunctionWriter junctionWriter, IReferenceDataWriter referenceDataWriter)
        : base(reader, junctionWriter, referenceDataWriter)
    {
    }

    public async Task<Dictionary<string, int>> PopulateGeneralCategoriesAsync(
        IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        List<(string Code, string GroupCode, string Description)> categories = new(codes.Count);
        foreach (string code in codes)
        {
            categories.Add((
                code,
                code.Length > 0 ? code[..1] : "C",
                GetGeneralCategoryDescription(code)));
        }

        await PopulateGeneralCategoriesCoreAsync(categories, ct);

        return await LoadCodeMapAsync("substrate.general_category", codes.Count, ct);
    }

    public async Task<Dictionary<string, int>> PopulateScriptsAsync(
        IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        await PopulateScriptsCoreAsync(codes, ct);

        return await LoadCodeMapAsync("substrate.script", codes.Count, ct);
    }

    public async Task<Dictionary<string, int>> PopulateBlocksAsync(
        IReadOnlyDictionary<string, (int RangeStart, int RangeEnd)> blocks, CancellationToken ct)
    {
        List<(string Code, int RangeStart, int RangeEnd)> rows = new(blocks.Count);
        foreach (KeyValuePair<string, (int RangeStart, int RangeEnd)> kv in blocks)
        {
            rows.Add((kv.Key, kv.Value.RangeStart, kv.Value.RangeEnd));
        }

        await PopulateBlocksCoreAsync(rows, ct);

        return await LoadCodeMapAsync("substrate.block", blocks.Count, ct);
    }

    public async Task<Dictionary<(string, string), int>> PopulateBreakPropertiesAsync(
        IReadOnlyCollection<(string Code, string Category)> properties, CancellationToken ct)
    {
        await PopulateBreakPropertiesCoreAsync(properties, ct);

        return await LoadKeyValueMapAsync("substrate.break_property", "code", "category", properties.Count, ct);
    }

    public async Task WriteCodepointPropertiesAsync(
        IReadOnlyList<CodepointPropertyRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        List<(
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
            int[]? FullCaseFold)> copyRows = new(rows.Count);
        foreach (CodepointPropertyRow row in rows)
        {
            copyRows.Add((
                row.EntityHash,
                row.CodepointValue,
                row.GeneralCategoryId,
                row.ScriptId,
                row.BlockId,
                row.GcbId,
                row.WbId,
                row.SbId,
                row.LbId,
                row.IsExtendedPictographic,
                row.Ccc,
                row.DecompositionType,
                row.DecompositionMapping,
                row.SimpleCaseFold,
                row.FullCaseFold));
        }

        await WriteCodepointPropertiesCoreAsync(copyRows, ct);
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
