using System;
using System.Buffers.Binary;

namespace Hartonomous.Recomposers;

/// <summary>
/// Packs substrate-synthesized numeric values into safetensors little-endian
/// wire dtypes. This is package-materialization logic shared by recomposers,
/// not a private detail of one export path.
/// </summary>
public static class SafetensorsDtypePacker
{
    public static int BytesPerElement(string dtype) => NormalizeDtype(dtype) switch
    {
        "F64" => 8,
        "F32" => 4,
        "F16" or "BF16" => 2,
        "I64" or "U64" => 8,
        "I32" or "U32" => 4,
        "I16" or "U16" => 2,
        "I8" or "U8" or "BOOL" => 1,
        "F8_E4M3" or "F8_E5M2" => 1,
        string normalized => throw new NotSupportedException($"Unknown safetensors dtype '{normalized}'"),
    };

    public static string NormalizeDtype(string dtype)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dtype);
        string normalized = dtype.Trim().ToUpperInvariant();
        return normalized switch
        {
            "BOOLEAN" or "BOOL_" => "BOOL",
            "F8E4M3" or "F8_E4M3FN" => "F8_E4M3",
            "F8E5M2" => "F8_E5M2",
            _ => normalized,
        };
    }

    public static void PackToWire(ReadOnlySpan<double> values, string dtype, Span<byte> buffer)
    {
        string normalized = NormalizeDtype(dtype);
        int expectedBytes = checked(values.Length * BytesPerElement(normalized));
        if (buffer.Length != expectedBytes)
        {
            throw new ArgumentException(
                $"Buffer length {buffer.Length} does not match {values.Length} {normalized} values ({expectedBytes} bytes).",
                nameof(buffer));
        }

        switch (normalized)
        {
            case "F64":
                for (int i = 0; i < values.Length; i++)
                {
                    BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(i * 8, 8), values[i]);
                }
                break;
            case "F32":
                for (int i = 0; i < values.Length; i++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(i * 4, 4), (float)values[i]);
                }
                break;
            case "F16":
                for (int i = 0; i < values.Length; i++)
                {
                    BinaryPrimitives.WriteHalfLittleEndian(buffer.Slice(i * 2, 2), (Half)(float)values[i]);
                }
                break;
            case "BF16":
                for (int i = 0; i < values.Length; i++)
                {
                    int bits = BitConverter.SingleToInt32Bits((float)values[i]);
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(i * 2, 2), (ushort)(bits >> 16));
                }
                break;
            case "I8":
                for (int i = 0; i < values.Length; i++)
                {
                    buffer[i] = unchecked((byte)ClampRounded(values[i], sbyte.MinValue, sbyte.MaxValue));
                }
                break;
            case "U8":
                for (int i = 0; i < values.Length; i++)
                {
                    buffer[i] = (byte)ClampRounded(values[i], byte.MinValue, byte.MaxValue);
                }
                break;
            case "I16":
                for (int i = 0; i < values.Length; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(
                        buffer.Slice(i * 2, 2),
                        (short)ClampRounded(values[i], short.MinValue, short.MaxValue));
                }
                break;
            case "U16":
                for (int i = 0; i < values.Length; i++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        buffer.Slice(i * 2, 2),
                        (ushort)ClampRounded(values[i], ushort.MinValue, ushort.MaxValue));
                }
                break;
            case "I32":
                for (int i = 0; i < values.Length; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(
                        buffer.Slice(i * 4, 4),
                        (int)ClampRounded(values[i], int.MinValue, int.MaxValue));
                }
                break;
            case "U32":
                for (int i = 0; i < values.Length; i++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        buffer.Slice(i * 4, 4),
                        (uint)ClampRounded(values[i], uint.MinValue, uint.MaxValue));
                }
                break;
            case "I64":
                for (int i = 0; i < values.Length; i++)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(
                        buffer.Slice(i * 8, 8),
                        ClampRoundedInt64(values[i]));
                }
                break;
            case "U64":
                for (int i = 0; i < values.Length; i++)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(
                        buffer.Slice(i * 8, 8),
                        ClampRoundedUInt64(values[i]));
                }
                break;
            case "BOOL":
                for (int i = 0; i < values.Length; i++)
                {
                    buffer[i] = values[i] != 0.0 ? (byte)1 : (byte)0;
                }
                break;
            case "F8_E4M3":
                for (int i = 0; i < values.Length; i++)
                {
                    buffer[i] = F32ToE4M3((float)values[i]);
                }
                break;
            case "F8_E5M2":
                for (int i = 0; i < values.Length; i++)
                {
                    buffer[i] = F32ToE5M2((float)values[i]);
                }
                break;
            default:
                throw new NotSupportedException($"PackToWire: dtype '{normalized}' not implemented.");
        }
    }

    private static double ClampRounded(double value, double min, double max)
    {
        if (double.IsNaN(value))
        {
            return 0.0;
        }
        double rounded = Math.Round(value);
        if (rounded < min)
        {
            return min;
        }
        if (rounded > max)
        {
            return max;
        }
        return rounded;
    }

    private static ulong ClampRoundedUInt64(double value)
    {
        if (double.IsNaN(value) || value <= 0.0)
        {
            return 0UL;
        }
        if (value >= ulong.MaxValue)
        {
            return ulong.MaxValue;
        }
        return (ulong)Math.Round(value);
    }

    private static long ClampRoundedInt64(double value)
    {
        if (double.IsNaN(value))
        {
            return 0L;
        }
        if (value <= long.MinValue)
        {
            return long.MinValue;
        }
        if (value >= long.MaxValue)
        {
            return long.MaxValue;
        }
        return (long)Math.Round(value);
    }

    private static byte F32ToE4M3(float x)
    {
        if (float.IsNaN(x)) { return 0x7F; }
        int bits = BitConverter.SingleToInt32Bits(x);
        int sign = (bits >>> 31) & 1;
        int rawExp = (bits >> 23) & 0xFF;
        int mant23 = bits & 0x7FFFFF;
        if (rawExp == 0 && mant23 == 0) { return (byte)(sign << 7); }
        if (float.IsInfinity(x)) { return (byte)((sign << 7) | 0x7E); }
        int unbiased = rawExp - 127;

        if (unbiased < -9)
        {
            return (byte)(sign << 7);
        }
        if (unbiased < -6)
        {
            int shift = -6 - unbiased;
            int implicit24 = mant23 | 0x800000;
            int dropBits = 20 + shift;
            int roundBit = 1 << (dropBits - 1);
            int lowerMask = roundBit - 1;
            int high = implicit24 >> dropBits;
            int lower = implicit24 & lowerMask;
            if ((implicit24 & roundBit) != 0 && (lower != 0 || (high & 1) != 0)) { high++; }
            if (high > 0x7)
            {
                return (byte)((sign << 7) | (1 << 3) | (high & 0x7));
            }
            return (byte)((sign << 7) | high);
        }

        int biased = unbiased + 7;
        int dropBits2 = 20;
        int roundBit2 = 1 << (dropBits2 - 1);
        int lowerMask2 = roundBit2 - 1;
        int high2 = mant23 >> dropBits2;
        int lower2 = mant23 & lowerMask2;
        if ((mant23 & roundBit2) != 0 && (lower2 != 0 || (high2 & 1) != 0))
        {
            high2++;
            if (high2 == 8) { high2 = 0; biased++; }
        }
        if (biased > 15 || (biased == 15 && high2 >= 7))
        {
            return (byte)((sign << 7) | 0x7E);
        }
        return (byte)((sign << 7) | (biased << 3) | high2);
    }

    private static byte F32ToE5M2(float x)
    {
        if (float.IsNaN(x)) { return 0x7E; }
        int bits = BitConverter.SingleToInt32Bits(x);
        int sign = (bits >>> 31) & 1;
        int rawExp = (bits >> 23) & 0xFF;
        int mant23 = bits & 0x7FFFFF;
        if (rawExp == 0 && mant23 == 0) { return (byte)(sign << 7); }
        if (float.IsInfinity(x)) { return (byte)((sign << 7) | 0x7C); }
        int unbiased = rawExp - 127;

        if (unbiased < -16) { return (byte)(sign << 7); }
        if (unbiased < -14)
        {
            int shift = -14 - unbiased;
            int implicit24 = mant23 | 0x800000;
            int dropBits = 21 + shift;
            int roundBit = 1 << (dropBits - 1);
            int lowerMask = roundBit - 1;
            int high = implicit24 >> dropBits;
            int lower = implicit24 & lowerMask;
            if ((implicit24 & roundBit) != 0 && (lower != 0 || (high & 1) != 0)) { high++; }
            if (high > 0x3)
            {
                return (byte)((sign << 7) | (1 << 2) | (high & 0x3));
            }
            return (byte)((sign << 7) | high);
        }

        int biased = unbiased + 15;
        int dropBits2 = 21;
        int roundBit2 = 1 << (dropBits2 - 1);
        int lowerMask2 = roundBit2 - 1;
        int high2 = mant23 >> dropBits2;
        int lower2 = mant23 & lowerMask2;
        if ((mant23 & roundBit2) != 0 && (lower2 != 0 || (high2 & 1) != 0))
        {
            high2++;
            if (high2 == 4) { high2 = 0; biased++; }
        }
        if (biased >= 31)
        {
            return (byte)((sign << 7) | 0x7C);
        }
        return (byte)((sign << 7) | (biased << 2) | high2);
    }
}
