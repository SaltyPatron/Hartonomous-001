using System.Buffers;
using System.Text;
using Hartonomous.Core.Text.Segmentation;

namespace Hartonomous.Core.Text.Normalization;

/// <summary>
/// Unicode case folding per UCD <c>CaseFolding.txt</c>. All variants are
/// substrate-backed — the caller supplies an <see cref="ICaseFoldingProperties"/>
/// loaded from the seeded codepoint_property junction. Content is never
/// mutated in-place; every method returns a new byte[].
/// </summary>
public static class CaseFold
{
    /// <summary>
    /// Full casefold (status C + F). May expand one codepoint into several
    /// (ß → ss, etc.) — output length is independent of input length.
    /// </summary>
    public static byte[] Full(ReadOnlySpan<byte> utf8, ICaseFoldingProperties properties)
    {
        if (utf8.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        ArrayBufferWriter<byte> writer = new(utf8.Length);
        Span<byte> scratch = stackalloc byte[4];

        int idx = 0;
        while (idx < utf8.Length)
        {
            (int cp, int consumed) = Utf8.DecodeOne(utf8[idx..]);
            if (cp < 0 || consumed == 0)
            {
                break;
            }

            ReadOnlySpan<int> folded = properties.GetFullCaseFold(cp);
            foreach (int outCp in folded)
            {
                int bytesWritten = Utf8Encode(outCp, scratch);
                writer.Write(scratch[..bytesWritten]);
            }
            idx += consumed;
        }

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Simple casefold (status C + S). One codepoint in, one codepoint out —
    /// output grapheme count equals input codepoint count. Used by tokenizer
    /// normalizers that require stable codepoint-to-codepoint mapping.
    /// </summary>
    public static byte[] Simple(ReadOnlySpan<byte> utf8, ICaseFoldingProperties properties)
    {
        if (utf8.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        ArrayBufferWriter<byte> writer = new(utf8.Length);
        Span<byte> scratch = stackalloc byte[4];

        int idx = 0;
        while (idx < utf8.Length)
        {
            (int cp, int consumed) = Utf8.DecodeOne(utf8[idx..]);
            if (cp < 0 || consumed == 0)
            {
                break;
            }

            int folded = properties.GetSimpleCaseFold(cp);
            int bytesWritten = Utf8Encode(folded, scratch);
            writer.Write(scratch[..bytesWritten]);
            idx += consumed;
        }

        return writer.WrittenSpan.ToArray();
    }

    private static int Utf8Encode(int codepoint, Span<byte> dst)
    {
        if ((uint)codepoint < 0x80)
        {
            dst[0] = (byte)codepoint;
            return 1;
        }
        if ((uint)codepoint < 0x800)
        {
            dst[0] = (byte)(0xC0 | (codepoint >> 6));
            dst[1] = (byte)(0x80 | (codepoint & 0x3F));
            return 2;
        }
        if ((uint)codepoint < 0x10000)
        {
            if (codepoint >= 0xD800 && codepoint <= 0xDFFF)
            {
                codepoint = 0xFFFD;
            }
            dst[0] = (byte)(0xE0 | (codepoint >> 12));
            dst[1] = (byte)(0x80 | ((codepoint >> 6) & 0x3F));
            dst[2] = (byte)(0x80 | (codepoint & 0x3F));
            return 3;
        }
        if ((uint)codepoint <= 0x10FFFF)
        {
            dst[0] = (byte)(0xF0 | (codepoint >> 18));
            dst[1] = (byte)(0x80 | ((codepoint >> 12) & 0x3F));
            dst[2] = (byte)(0x80 | ((codepoint >> 6) & 0x3F));
            dst[3] = (byte)(0x80 | (codepoint & 0x3F));
            return 4;
        }
        dst[0] = 0xEF;
        dst[1] = 0xBF;
        dst[2] = 0xBD;
        return 3;
    }
}
