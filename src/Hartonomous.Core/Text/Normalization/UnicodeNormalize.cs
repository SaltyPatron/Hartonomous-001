using System.Text;

namespace Hartonomous.Core.Text.Normalization;

/// <summary>
/// Unicode Normalization Forms. Delegates to the BCL's ICU-backed
/// <see cref="string.Normalize(System.Text.NormalizationForm)"/>, which is
/// table-driven and Unicode-version-stable per .NET runtime release. The
/// substrate's <c>text_segmentation_profile</c> records the ICU / Unicode
/// version so a future substrate-native implementation backed by UCD data
/// ingested by <c>UcdUcaDecomposer</c> is a drop-in replacement behind the
/// same API. Content is never mutated — every method returns a new byte[].
/// </summary>
public static class UnicodeNormalize
{
    /// <summary>
    /// Produce a normalized copy of <paramref name="utf8"/> in the requested form.
    /// </summary>
    public static byte[] ToForm(ReadOnlySpan<byte> utf8, NormalizationForm form)
    {
        if (utf8.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        string s = Encoding.UTF8.GetString(utf8);
        string normalized = s.Normalize(Map(form));
        return Encoding.UTF8.GetBytes(normalized);
    }

    /// <summary>
    /// Returns true when <paramref name="utf8"/> is already in the requested form.
    /// Short-circuits the allocation when the caller only needs a boolean.
    /// </summary>
    public static bool IsForm(ReadOnlySpan<byte> utf8, NormalizationForm form)
    {
        if (utf8.IsEmpty)
        {
            return true;
        }

        string s = Encoding.UTF8.GetString(utf8);
        return s.IsNormalized(Map(form));
    }

    private static System.Text.NormalizationForm Map(NormalizationForm form) => form switch
    {
        NormalizationForm.Nfc => System.Text.NormalizationForm.FormC,
        NormalizationForm.Nfd => System.Text.NormalizationForm.FormD,
        NormalizationForm.Nfkc => System.Text.NormalizationForm.FormKC,
        NormalizationForm.Nfkd => System.Text.NormalizationForm.FormKD,
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, "Unknown normalization form."),
    };
}
