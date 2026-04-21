namespace Hartonomous.Core.Text.Segmentation;

public static class Utf8
{
    /// <summary>
    /// Decode one codepoint from <paramref name="bytes"/> starting at index 0.
    /// Returns the Unicode scalar value (U+FFFD for ill-formed sequences, per
    /// Unicode 15 §3.9 "Best Practice for U+FFFD Substitution") and the number
    /// of bytes consumed (always ≥ 1 so the caller can advance).
    /// </summary>
    public static (int Codepoint, int BytesConsumed) DecodeOne(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return (-1, 0);
        }

        byte b0 = bytes[0];
        if (b0 < 0x80)
        {
            return (b0, 1);
        }

        if (b0 < 0xC2 || b0 > 0xF4)
        {
            return (0xFFFD, 1);
        }

        if (b0 < 0xE0)
        {
            if (bytes.Length < 2 || !IsContinuation(bytes[1]))
            {
                return (0xFFFD, 1);
            }
            int cp = ((b0 & 0x1F) << 6) | (bytes[1] & 0x3F);
            return (cp, 2);
        }

        if (b0 < 0xF0)
        {
            if (bytes.Length < 2)
            {
                return (0xFFFD, 1);
            }
            byte b1 = bytes[1];
            byte lo = b0 == 0xE0 ? (byte)0xA0 : (byte)0x80;
            byte hi = b0 == 0xED ? (byte)0x9F : (byte)0xBF;
            if (b1 < lo || b1 > hi)
            {
                return (0xFFFD, 1);
            }
            if (bytes.Length < 3 || !IsContinuation(bytes[2]))
            {
                return (0xFFFD, 2);
            }
            int cp = ((b0 & 0x0F) << 12) | ((b1 & 0x3F) << 6) | (bytes[2] & 0x3F);
            return (cp, 3);
        }

        {
            if (bytes.Length < 2)
            {
                return (0xFFFD, 1);
            }
            byte b1 = bytes[1];
            byte lo = b0 == 0xF0 ? (byte)0x90 : (byte)0x80;
            byte hi = b0 == 0xF4 ? (byte)0x8F : (byte)0xBF;
            if (b1 < lo || b1 > hi)
            {
                return (0xFFFD, 1);
            }
            if (bytes.Length < 3 || !IsContinuation(bytes[2]))
            {
                return (0xFFFD, 2);
            }
            if (bytes.Length < 4 || !IsContinuation(bytes[3]))
            {
                return (0xFFFD, 3);
            }
            int cp = ((b0 & 0x07) << 18) | ((b1 & 0x3F) << 12) | ((bytes[2] & 0x3F) << 6) | (bytes[3] & 0x3F);
            return (cp, 4);
        }
    }

    private static bool IsContinuation(byte b) => (b & 0xC0) == 0x80;
}
