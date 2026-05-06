using Hartonomous.Core.Text.Normalization;
using Hartonomous.Core.Text.Segmentation;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Engine.Text;

/// <summary>
/// In-memory cache of UCD properties loaded from
/// <c>substrate.codepoint_property</c>. Populated once at startup; then every
/// <see cref="ICodepointProperties"/> / <see cref="ICaseFoldingProperties"/>
/// lookup is an O(1) array index.
///
/// LEGACY SURFACE (post-W3B). The hot ingestion path (WordNet, Wiktionary,
/// Safetensors text artifacts) now goes through
/// <see cref="Hartonomous.Core.Text.SubstrateTextDecomposer"/> which
/// hands UTF-8 to <c>substrate.text_decompose</c> — properties come from the
/// embedded UCD blob baked into the C extension at build time. This cache
/// is retained ONLY for cold paths still calling
/// <see cref="Hartonomous.Core.Text.CanonicalTextDecomposer.Emit"/> directly:
/// Iso639 / UD / OMW / Tatoeba / Text decomposers, plus the inference-side
/// label-rendering surfaces (GodelEngine, SubQuestionDecomposer,
/// SubstrateInferenceEngine, Api/Program.cs). When those paths migrate to
/// <c>substrate.cp_*</c> SQL functions or
/// <c>substrate.recompose_text(entity_hash)</c>, this class plus the entire
/// <c>Hartonomous.Core.Text.Segmentation</c> + <c>ICaseFoldingProperties</c>
/// surface can be deleted (~1330 LOC of UAX #29 in C#).
///
/// Per AP-7: callers that only need a small working set MUST use
/// <see cref="LoadForCodepointsAsync"/>; the eager <see cref="LoadAsync"/>
/// is reserved for seed phases that genuinely need every codepoint and is
/// being phased out.
/// </summary>
public sealed partial class NpgsqlCodepointPropertiesCache : ICodepointProperties, ICaseFoldingProperties
{
    private const int MaxCodepoint = 0x10FFFF;
    private const int Size = MaxCodepoint + 1;

    private readonly GraphemeBreak[] _gcb = new GraphemeBreak[Size];
    private readonly WordBreak[] _wb = new WordBreak[Size];
    private readonly SentenceBreak[] _sb = new SentenceBreak[Size];
    private readonly LineBreak[] _lb = new LineBreak[Size];
    private readonly byte[] _extPict = new byte[(Size + 7) / 8];
    private readonly int[] _simpleFold = new int[Size];
    private readonly int[]?[] _fullFold = new int[]?[Size];

    public static async Task<NpgsqlCodepointPropertiesCache> LoadAsync(
        string connectionString,
        ILogger<NpgsqlCodepointPropertiesCache> logger,
        CancellationToken ct)
    {
        NpgsqlCodepointPropertiesCache cache = CreateEmpty();

        Log.Loading(logger);

        await using NpgsqlDataSource ds = NpgsqlDataSource.Create(connectionString);
        await using NpgsqlConnection conn = await ds.OpenConnectionAsync(ct);

        Dictionary<int, string> breakCodeByEntityIsolate = await LoadBreakPropertyNamesAsync(conn, ct);

        int loaded = await LoadRowsAsync(cache, conn, breakCodeByEntityIsolate, codepoints: null, ct);

        Log.Loaded(logger, loaded);
        return cache;
    }

    public static async Task<NpgsqlCodepointPropertiesCache> LoadForCodepointsAsync(
        string connectionString,
        IReadOnlyCollection<int> codepoints,
        ILogger<NpgsqlCodepointPropertiesCache> logger,
        CancellationToken ct)
    {
        NpgsqlCodepointPropertiesCache cache = CreateEmpty();
        if (codepoints.Count == 0)
        {
            Log.LoadedSubset(logger, 0, 0);
            return cache;
        }

        Log.LoadingSubset(logger, codepoints.Count);

        await using NpgsqlDataSource ds = NpgsqlDataSource.Create(connectionString);
        await using NpgsqlConnection conn = await ds.OpenConnectionAsync(ct);

        Dictionary<int, string> breakCodeByEntityIsolate = await LoadBreakPropertyNamesAsync(conn, ct);
        int loaded = await LoadRowsAsync(cache, conn, breakCodeByEntityIsolate, codepoints, ct);

        Log.LoadedSubset(logger, loaded, codepoints.Count);
        return cache;
    }

    private static NpgsqlCodepointPropertiesCache CreateEmpty()
    {
        NpgsqlCodepointPropertiesCache cache = new();
        for (int i = 0; i < Size; i++)
        {
            cache._simpleFold[i] = i;
        }

        return cache;
    }

    private static async Task<int> LoadRowsAsync(
        NpgsqlCodepointPropertiesCache cache,
        NpgsqlConnection conn,
        Dictionary<int, string> breakCodeByEntityIsolate,
        IReadOnlyCollection<int>? codepoints,
        CancellationToken ct)
    {
        string sql =
            "SELECT cp.codepoint_value, cp.gcb_id, cp.wb_id, cp.sb_id, cp.lb_id, " +
            "       cp.is_extended_pictographic, cp.simple_case_fold, cp.full_case_fold " +
            "FROM substrate.codepoint_property cp " +
            "WHERE cp.codepoint_value IS NOT NULL";

        await using NpgsqlCommand cmd = new(codepoints is null ? sql : sql + " AND cp.codepoint_value = ANY($1)", conn);
        if (codepoints is not null)
        {
            int[] requested = [.. codepoints];
            cmd.Parameters.AddWithValue(requested);
        }
        cmd.CommandTimeout = 300;

        int loaded = 0;
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            int cp = reader.GetInt32(0);
            if ((uint)cp > MaxCodepoint)
            {
                continue;
            }

            cache._gcb[cp] = ResolveGraphemeBreak(reader, 1, breakCodeByEntityIsolate);
            cache._wb[cp] = ResolveWordBreak(reader, 2, breakCodeByEntityIsolate);
            cache._sb[cp] = ResolveSentenceBreak(reader, 3, breakCodeByEntityIsolate);
            cache._lb[cp] = ResolveLineBreak(reader, 4, breakCodeByEntityIsolate);

            bool isExtPict = !reader.IsDBNull(5) && reader.GetBoolean(5);
            if (isExtPict)
            {
                cache._extPict[cp >> 3] |= (byte)(1 << (cp & 7));
            }

            if (!reader.IsDBNull(6))
            {
                cache._simpleFold[cp] = reader.GetInt32(6);
            }
            if (!reader.IsDBNull(7))
            {
                cache._fullFold[cp] = (int[])reader.GetValue(7);
            }

            loaded++;
        }

        return loaded;
    }

    private static async Task<Dictionary<int, string>> LoadBreakPropertyNamesAsync(
        NpgsqlConnection conn, CancellationToken ct)
    {
        Dictionary<int, string> map = new();
        await using NpgsqlCommand cmd = new("SELECT id, code FROM substrate.break_property", conn);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            map[reader.GetInt32(0)] = reader.GetString(1);
        }
        return map;
    }

    public GraphemeBreak GetGraphemeBreak(int codepoint)
    {
        if ((uint)codepoint > MaxCodepoint)
        {
            return GraphemeBreak.Other;
        }
        return _gcb[codepoint];
    }

    public bool IsExtendedPictographic(int codepoint)
    {
        if ((uint)codepoint > MaxCodepoint)
        {
            return false;
        }
        return (_extPict[codepoint >> 3] & (1 << (codepoint & 7))) != 0;
    }

    public WordBreak GetWordBreak(int codepoint)
    {
        if ((uint)codepoint > MaxCodepoint)
        {
            return WordBreak.Other;
        }
        return _wb[codepoint];
    }

    public SentenceBreak GetSentenceBreak(int codepoint)
    {
        if ((uint)codepoint > MaxCodepoint)
        {
            return SentenceBreak.Other;
        }
        return _sb[codepoint];
    }

    public LineBreak GetLineBreak(int codepoint)
    {
        if ((uint)codepoint > MaxCodepoint)
        {
            return LineBreak.XX;
        }
        return _lb[codepoint];
    }

    public int GetSimpleCaseFold(int codepoint)
    {
        if ((uint)codepoint > MaxCodepoint)
        {
            return codepoint;
        }
        return _simpleFold[codepoint];
    }

    public ReadOnlySpan<int> GetFullCaseFold(int codepoint)
    {
        if ((uint)codepoint > MaxCodepoint)
        {
            return ReadOnlySpan<int>.Empty;
        }
        int[]? full = _fullFold[codepoint];
        if (full is not null)
        {
            return full;
        }
        // Lazily memoize identity folds as 1-element arrays so the returned
        // span lifetime extends beyond this method.
        int[] identity = new[] { codepoint };
        _fullFold[codepoint] = identity;
        return identity;
    }

    private static GraphemeBreak ResolveGraphemeBreak(
        NpgsqlDataReader reader, int ordinal, Dictionary<int, string> codes)
    {
        if (reader.IsDBNull(ordinal))
        {
            return GraphemeBreak.Other;
        }
        if (codes is null)
        {
            return GraphemeBreak.Other;
        }
        return codes.TryGetValue(reader.GetInt32(ordinal), out string? code) && code is not null
            ? MapGcb(code)
            : GraphemeBreak.Other;
    }

    private static WordBreak ResolveWordBreak(
        NpgsqlDataReader reader, int ordinal, Dictionary<int, string> codes)
    {
        if (reader.IsDBNull(ordinal))
        {
            return WordBreak.Other;
        }
        if (codes is null)
        {
            return WordBreak.Other;
        }
        return codes.TryGetValue(reader.GetInt32(ordinal), out string? code) && code is not null
            ? MapWb(code)
            : WordBreak.Other;
    }

    private static SentenceBreak ResolveSentenceBreak(
        NpgsqlDataReader reader, int ordinal, Dictionary<int, string> codes)
    {
        if (reader.IsDBNull(ordinal))
        {
            return SentenceBreak.Other;
        }
        if (codes is null)
        {
            return SentenceBreak.Other;
        }
        return codes.TryGetValue(reader.GetInt32(ordinal), out string? code) && code is not null
            ? MapSb(code)
            : SentenceBreak.Other;
    }

    private static LineBreak ResolveLineBreak(
        NpgsqlDataReader reader, int ordinal, Dictionary<int, string> codes)
    {
        if (reader.IsDBNull(ordinal))
        {
            return LineBreak.XX;
        }
        if (codes is null)
        {
            return LineBreak.XX;
        }
        return codes.TryGetValue(reader.GetInt32(ordinal), out string? code) && code is not null
            ? MapLb(code)
            : LineBreak.XX;
    }

    // Both long forms (UCD GraphemeBreakProperty.txt) and short codes
    // (UCD PropertyValueAliases.txt). The substrate seeds short codes for
    // most rows; long forms are accepted for forward-compat with re-seeds.
    private static GraphemeBreak MapGcb(string code) => code switch
    {
        "CR" => GraphemeBreak.CR,
        "LF" => GraphemeBreak.LF,
        "Control" or "CN" => GraphemeBreak.Control,
        "Extend" or "EX" => GraphemeBreak.Extend,
        "ZWJ" => GraphemeBreak.ZWJ,
        "Regional_Indicator" or "RI" => GraphemeBreak.RegionalIndicator,
        "Prepend" or "PP" => GraphemeBreak.Prepend,
        "SpacingMark" or "SM" => GraphemeBreak.SpacingMark,
        "L" => GraphemeBreak.L,
        "V" => GraphemeBreak.V,
        "T" => GraphemeBreak.T,
        "LV" => GraphemeBreak.LV,
        "LVT" => GraphemeBreak.LVT,
        _ => GraphemeBreak.Other,
    };

    private static WordBreak MapWb(string code) => code switch
    {
        "CR" => WordBreak.CR,
        "LF" => WordBreak.LF,
        "Newline" => WordBreak.Newline,
        "NL" => WordBreak.Newline,
        "Extend" => WordBreak.Extend,
        "ZWJ" => WordBreak.ZWJ,
        "Regional_Indicator" => WordBreak.RegionalIndicator,
        "RI" => WordBreak.RegionalIndicator,
        "Format" => WordBreak.Format,
        "FO" => WordBreak.Format,
        "Katakana" => WordBreak.Katakana,
        "KA" => WordBreak.Katakana,
        "Hebrew_Letter" => WordBreak.HebrewLetter,
        "HL" => WordBreak.HebrewLetter,
        "ALetter" => WordBreak.ALetter,
        "LE" => WordBreak.ALetter,
        "Single_Quote" => WordBreak.SingleQuote,
        "SQ" => WordBreak.SingleQuote,
        "Double_Quote" => WordBreak.DoubleQuote,
        "DQ" => WordBreak.DoubleQuote,
        "MidNumLet" => WordBreak.MidNumLet,
        "MB" => WordBreak.MidNumLet,
        "MidLetter" => WordBreak.MidLetter,
        "ML" => WordBreak.MidLetter,
        "MidNum" => WordBreak.MidNum,
        "MN" => WordBreak.MidNum,
        "Numeric" => WordBreak.Numeric,
        "NU" => WordBreak.Numeric,
        "ExtendNumLet" => WordBreak.ExtendNumLet,
        "EX" => WordBreak.ExtendNumLet,
        "WSegSpace" => WordBreak.WSegSpace,
        _ => WordBreak.Other,
    };

    // Both long forms (UCD SentenceBreakProperty.txt) and short codes
    // (UCD PropertyValueAliases.txt: AT, CL, EX, FO, LE/OL, LO, NU, SC, SE,
    // SP, ST, UP, XX). The substrate seeds short codes for most rows.
    private static SentenceBreak MapSb(string code) => code switch
    {
        "CR" => SentenceBreak.CR,
        "LF" => SentenceBreak.LF,
        "Extend" or "EX" => SentenceBreak.Extend,
        "Format" or "FO" => SentenceBreak.Format,
        "Sep" or "SE" => SentenceBreak.Sep,
        "Sp" or "SP" => SentenceBreak.Sp,
        "Lower" or "LO" => SentenceBreak.Lower,
        "Upper" or "UP" => SentenceBreak.Upper,
        "OLetter" or "LE" or "OL" => SentenceBreak.OLetter,
        "Numeric" or "NU" => SentenceBreak.Numeric,
        "ATerm" or "AT" => SentenceBreak.ATerm,
        "STerm" or "ST" => SentenceBreak.STerm,
        "Close" or "CL" => SentenceBreak.Close,
        "SContinue" or "SC" => SentenceBreak.SContinue,
        _ => SentenceBreak.Other,
    };

    private static LineBreak MapLb(string code) => code switch
    {
        "BK" => LineBreak.BK,
        "CR" => LineBreak.CR,
        "LF" => LineBreak.LF,
        "CM" => LineBreak.CM,
        "NL" => LineBreak.NL,
        "SG" => LineBreak.SG,
        "WJ" => LineBreak.WJ,
        "ZW" => LineBreak.ZW,
        "GL" => LineBreak.GL,
        "SP" => LineBreak.SP,
        "ZWJ" => LineBreak.ZWJ,
        "B2" => LineBreak.B2,
        "BA" => LineBreak.BA,
        "BB" => LineBreak.BB,
        "HY" => LineBreak.HY,
        "CB" => LineBreak.CB,
        "CL" => LineBreak.CL,
        "CP" => LineBreak.CP,
        "EX" => LineBreak.EX,
        "IN" => LineBreak.IN,
        "NS" => LineBreak.NS,
        "OP" => LineBreak.OP,
        "QU" => LineBreak.QU,
        "IS" => LineBreak.IS,
        "NU" => LineBreak.NU,
        "PO" => LineBreak.PO,
        "PR" => LineBreak.PR,
        "SY" => LineBreak.SY,
        "AI" => LineBreak.AI,
        "AL" => LineBreak.AL,
        "CJ" => LineBreak.CJ,
        "EB" => LineBreak.EB,
        "EM" => LineBreak.EM,
        "H2" => LineBreak.H2,
        "H3" => LineBreak.H3,
        "HL" => LineBreak.HL,
        "ID" => LineBreak.ID,
        "JL" => LineBreak.JL,
        "JV" => LineBreak.JV,
        "JT" => LineBreak.JT,
        "RI" => LineBreak.RI,
        "AK" => LineBreak.AK,
        "AP" => LineBreak.AP,
        "AS" => LineBreak.AS,
        "VF" => LineBreak.VF,
        "VI" => LineBreak.VI,
        _ => LineBreak.XX,
    };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Loading codepoint properties from substrate…")]
        public static partial void Loading(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Loading codepoint properties for {RequestedCount} distinct codepoints…")]
        public static partial void LoadingSubset(ILogger logger, int requestedCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Count} codepoint property rows into cache")]
        public static partial void Loaded(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Count} codepoint property rows into subset cache from {RequestedCount} requested codepoints")]
        public static partial void LoadedSubset(ILogger logger, int count, int requestedCount);
    }
}
