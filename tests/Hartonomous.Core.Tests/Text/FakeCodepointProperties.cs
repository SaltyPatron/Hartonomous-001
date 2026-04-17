using Hartonomous.Core.Text.Normalization;
using Hartonomous.Core.Text.Segmentation;

namespace Hartonomous.Core.Tests.Text;

/// <summary>
/// Minimal in-memory codepoint-property provider that implements the subset
/// of UCD properties the segmentation / casefold tests need. Mirrors the
/// shape of <c>NpgsqlCodepointPropertiesCache</c> without the database
/// dependency so Core tests stay hermetic.
/// </summary>
internal sealed class FakeCodepointProperties : ICodepointProperties, ICaseFoldingProperties
{
    private readonly Dictionary<int, GraphemeBreak> _gcb = new();
    private readonly Dictionary<int, WordBreak> _wb = new();
    private readonly Dictionary<int, SentenceBreak> _sb = new();
    private readonly Dictionary<int, LineBreak> _lb = new();
    private readonly HashSet<int> _extPict = new();
    private readonly Dictionary<int, int> _simpleFold = new();
    private readonly Dictionary<int, int[]> _fullFold = new();

    public FakeCodepointProperties WithGcb(int cp, GraphemeBreak b) { _gcb[cp] = b; return this; }
    public FakeCodepointProperties WithWb(int cp, WordBreak b) { _wb[cp] = b; return this; }
    public FakeCodepointProperties WithSb(int cp, SentenceBreak b) { _sb[cp] = b; return this; }
    public FakeCodepointProperties WithLb(int cp, LineBreak b) { _lb[cp] = b; return this; }
    public FakeCodepointProperties WithExtPict(int cp) { _extPict.Add(cp); return this; }
    public FakeCodepointProperties WithSimpleFold(int cp, int target) { _simpleFold[cp] = target; return this; }
    public FakeCodepointProperties WithFullFold(int cp, params int[] targets) { _fullFold[cp] = targets; return this; }

    public GraphemeBreak GetGraphemeBreak(int codepoint) =>
        _gcb.TryGetValue(codepoint, out GraphemeBreak v) ? v : GraphemeBreak.Other;

    public bool IsExtendedPictographic(int codepoint) => _extPict.Contains(codepoint);

    public WordBreak GetWordBreak(int codepoint) =>
        _wb.TryGetValue(codepoint, out WordBreak v) ? v : WordBreak.Other;

    public SentenceBreak GetSentenceBreak(int codepoint) =>
        _sb.TryGetValue(codepoint, out SentenceBreak v) ? v : SentenceBreak.Other;

    public LineBreak GetLineBreak(int codepoint) =>
        _lb.TryGetValue(codepoint, out LineBreak v) ? v : LineBreak.XX;

    public int GetSimpleCaseFold(int codepoint) =>
        _simpleFold.TryGetValue(codepoint, out int v) ? v : codepoint;

    public ReadOnlySpan<int> GetFullCaseFold(int codepoint)
    {
        if (_fullFold.TryGetValue(codepoint, out int[]? v))
        {
            return v;
        }
        return new[] { codepoint };
    }
}
