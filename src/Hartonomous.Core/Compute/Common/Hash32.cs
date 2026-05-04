using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Hartonomous.Core.Compute.Common;

/// <summary>
/// Fixed-size 32-byte hash key wrapper for use in dictionaries and sets.
/// BLAKE3 outputs 32 bytes; this struct gives O(1) equality and hashing
/// without per-comparison byte[] allocation. Use for in-process dedup
/// state (ConcurrentDictionary&lt;Hash32, byte&gt;) where byte[] keys would
/// box and hash slowly.
///
/// Layout matches BLAKE3 output exactly: 32 contiguous bytes, double-aligned
/// for fast 8-byte loads. <see cref="Equals(Hash32)"/> compares as 4 ulongs;
/// <see cref="GetHashCode"/> mixes those 4 ulongs via <see cref="HashCode"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 32, Pack = 8)]
public readonly struct Hash32 : IEquatable<Hash32>
{
    private readonly ulong _a;
    private readonly ulong _b;
    private readonly ulong _c;
    private readonly ulong _d;

    public Hash32(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32)
        {
            throw new ArgumentException($"Hash32 requires exactly 32 bytes (got {bytes.Length}).", nameof(bytes));
        }
        _a = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(0, 8));
        _b = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8, 8));
        _c = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(16, 8));
        _d = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(24, 8));
    }

    public Hash32(byte[] bytes) : this(bytes.AsSpan()) { }

    public bool Equals(Hash32 other)
        => _a == other._a && _b == other._b && _c == other._c && _d == other._d;

    public override bool Equals(object? obj) => obj is Hash32 h && Equals(h);

    public override int GetHashCode() => HashCode.Combine(_a, _b, _c, _d);

    public static bool operator ==(Hash32 left, Hash32 right) => left.Equals(right);
    public static bool operator !=(Hash32 left, Hash32 right) => !left.Equals(right);

    public byte[] ToByteArray()
    {
        byte[] result = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0, 8), _a);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8, 8), _b);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16, 8), _c);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(24, 8), _d);
        return result;
    }
}
