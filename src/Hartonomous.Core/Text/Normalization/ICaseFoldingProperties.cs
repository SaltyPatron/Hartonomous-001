namespace Hartonomous.Core.Text.Normalization;

/// <summary>
/// Substrate-backed case-folding lookup. Populated from UCD <c>CaseFolding.txt</c>
/// at UCD seed time; status C + S rows drive simple folding, status C + F rows
/// drive full folding.
/// </summary>
public interface ICaseFoldingProperties
{
    /// <summary>
    /// Simple case folding (status C + S) for a codepoint. One codepoint in,
    /// exactly one codepoint out. Returns <paramref name="codepoint"/> when
    /// no folding applies.
    /// </summary>
    int GetSimpleCaseFold(int codepoint);

    /// <summary>
    /// Full case folding (status C + F) for a codepoint. One codepoint in,
    /// one or more codepoints out (e.g., U+00DF ß → {0x0073, 0x0073} "ss").
    /// Returns a span with length 1 containing <paramref name="codepoint"/>
    /// when no folding applies. Implementations back the span with an interned
    /// array so repeated lookups do not allocate.
    /// </summary>
    ReadOnlySpan<int> GetFullCaseFold(int codepoint);
}
