using Hartonomous.Core.Text.Normalization;
using Hartonomous.Core.Text.Segmentation;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Engine.Text;

/// <summary>
/// In-memory cache of UCD properties loaded from
/// <c>substrate.codepoint_property</c>. Populated once at startup; then every
/// <see cref="ICodepointProperties"/> / <see cref="ICaseFoldingProperties"/>
/// lookup is an O(1) array index. The substrate is the source of truth — when
/// migration 0022 re-seeds are applied, the cache is reloaded.
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
        NpgsqlCodepointPropertiesCache cache = new();
        for (int i = 0; i < Size; i++)
        {
            cache._simpleFold[i] = i;
        }

        Log.Loading(logger);

        await using NpgsqlDataSource ds = NpgsqlDataSource.Create(connectionString);
        await using NpgsqlConnection conn = await ds.OpenConnectionAsync(ct);

        Dictionary<int, string> breakCodeByEntityIsolate = await LoadBreakPropertyNamesAsync(conn, ct);

        await using NpgsqlCommand cmd = new(
            "SELECT e.signature[17:20], cp.gcb_id, cp.wb_id, cp.sb_id, cp.lb_id, " +
            "       cp.is_extended_pictographic, cp.simple_case_fold, cp.full_case_fold " +
            "FROM substrate.codepoint_property cp " +
            "JOIN substrate.entity e ON e.id = cp.entity_id " +
            "WHERE e.kind_code = 'codepoint'", conn);
        cmd.CommandTimeout = 300;

        int loaded = 0;
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            byte[] last4 = (byte[])reader.GetValue(0);
            if (last4.Length < 4)
            {
                continue;
            }
            int cp = (last4[0] << 24) | (last4[1] << 16) | (last4[2] << 8) | last4[3];
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

        Log.Loaded(logger, loaded);
        return cache;
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
        return codes.TryGetValue(reader.GetInt32(ordinal), out string? code)
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
        return codes.TryGetValue(reader.GetInt32(ordinal), out string? code)
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
        return codes.TryGetValue(reader.GetInt32(ordinal), out string? code)
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
        return codes.TryGetValue(reader.GetInt32(ordinal), out string? code)
            ? MapLb(code)
            : LineBreak.XX;
    }

    private static GraphemeBreak MapGcb(string code) => code switch
    {
        "CR" => GraphemeBreak.CR,
        "LF" => GraphemeBreak.LF,
        "Control" => GraphemeBreak.Control,
        "Extend" => GraphemeBreak.Extend,
        "ZWJ" => GraphemeBreak.ZWJ,
        "Regional_Indicator" => GraphemeBreak.RegionalIndicator,
        "Prepend" => GraphemeBreak.Prepend,
        "SpacingMark" => GraphemeBreak.SpacingMark,
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
        "Extend" => WordBreak.Extend,
        "ZWJ" => WordBreak.ZWJ,
        "Regional_Indicator" => WordBreak.RegionalIndicator,
        "Format" => WordBreak.Format,
        "Katakana" => WordBreak.Katakana,
        "Hebrew_Letter" => WordBreak.HebrewLetter,
        "ALetter" => WordBreak.ALetter,
        "Single_Quote" => WordBreak.SingleQuote,
        "Double_Quote" => WordBreak.DoubleQuote,
        "MidNumLet" => WordBreak.MidNumLet,
        "MidLetter" => WordBreak.MidLetter,
        "MidNum" => WordBreak.MidNum,
        "Numeric" => WordBreak.Numeric,
        "ExtendNumLet" => WordBreak.ExtendNumLet,
        "WSegSpace" => WordBreak.WSegSpace,
        _ => WordBreak.Other,
    };

    private static SentenceBreak MapSb(string code) => code switch
    {
        "CR" => SentenceBreak.CR,
        "LF" => SentenceBreak.LF,
        "Extend" => SentenceBreak.Extend,
        "Format" => SentenceBreak.Format,
        "Sep" => SentenceBreak.Sep,
        "Sp" => SentenceBreak.Sp,
        "Lower" => SentenceBreak.Lower,
        "Upper" => SentenceBreak.Upper,
        "OLetter" => SentenceBreak.OLetter,
        "Numeric" => SentenceBreak.Numeric,
        "ATerm" => SentenceBreak.ATerm,
        "STerm" => SentenceBreak.STerm,
        "Close" => SentenceBreak.Close,
        "SContinue" => SentenceBreak.SContinue,
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

        [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Count} codepoint property rows into cache")]
        public static partial void Loaded(ILogger logger, int count);
    }
}
