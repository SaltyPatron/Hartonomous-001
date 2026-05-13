using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hartonomous.Core.Geometry;

/// <summary>
/// Substrate's 4D representative point — the C# mirror of the
/// <c>public.point4d</c> PostgreSQL type registered by the hartonomous
/// extension. Same memory layout (4 × float8 = 32 bytes, in X/Y/Z/M order)
/// so the same buffer can serve native geometry payload encoding and
/// PostgreSQL binary protocol encoding without per-axis
/// shuffling.
///
/// <para>
/// Per-partition axis convention is declared by the substrate's
/// physicality_type CHECK constraint — for some partitions the axes are
/// (S³.x, S³.y, S³.z, S³.w); for others (time, frequency, magnitude, phase);
/// for others (lat, lon, altitude, time). This struct is interpretation-free
/// at the type level; it carries 4 doubles and the math operations that work
/// uniformly across every interpretation (equality, addition, scaling,
/// distance, mean).
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly struct Point4D : IEquatable<Point4D>, IComparable<Point4D>
{
    public readonly double X;
    public readonly double Y;
    public readonly double Z;
    public readonly double M;

    /// <summary>Number of bytes one Point4D occupies in payload or PG binary form.</summary>
    public const int SizeBytes = 32;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Point4D(double x, double y, double z, double m)
    {
        X = x; Y = y; Z = z; M = m;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Point4D FromTuple((double X, double Y, double Z, double M) t) =>
        new(t.X, t.Y, t.Z, t.M);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (double X, double Y, double Z, double M) ToTuple() => (X, Y, Z, M);

    public bool Equals(Point4D other) =>
        X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z) && M.Equals(other.M);

    public override bool Equals(object? obj) => obj is Point4D p && Equals(p);

    public override int GetHashCode() => HashCode.Combine(X, Y, Z, M);

    public static bool operator ==(Point4D a, Point4D b) => a.Equals(b);
    public static bool operator !=(Point4D a, Point4D b) => !a.Equals(b);
    public static bool operator <(Point4D a, Point4D b) => a.CompareTo(b) < 0;
    public static bool operator <=(Point4D a, Point4D b) => a.CompareTo(b) <= 0;
    public static bool operator >(Point4D a, Point4D b) => a.CompareTo(b) > 0;
    public static bool operator >=(Point4D a, Point4D b) => a.CompareTo(b) >= 0;

    public int CompareTo(Point4D other)
    {
        int cx = X.CompareTo(other.X);
        if (cx != 0)
        {
            return cx;
        }
        int cy = Y.CompareTo(other.Y);
        if (cy != 0)
        {
            return cy;
        }
        int cz = Z.CompareTo(other.Z);
        if (cz != 0)
        {
            return cz;
        }
        return M.CompareTo(other.M);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X},{Y},{Z},{M})");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Point4D operator +(Point4D a, Point4D b) =>
        new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.M + b.M);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Point4D operator -(Point4D a, Point4D b) =>
        new(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.M - b.M);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Point4D operator *(Point4D a, double s) =>
        new(a.X * s, a.Y * s, a.Z * s, a.M * s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Point4D operator /(Point4D a, double s) =>
        new(a.X / s, a.Y / s, a.Z / s, a.M / s);

    /// <summary>Unweighted 4D mean of a vertex stream. Same value
    /// substrate.geometry4d_centroid computes for non-Point geometries.
    /// Returns false on empty input.</summary>
    public static bool TryMean(ReadOnlySpan<Point4D> points, out Point4D mean)
    {
        mean = default;
        if (points.Length == 0)
        {
            return false;
        }
        double sx = 0, sy = 0, sz = 0, sm = 0;
        for (int i = 0; i < points.Length; i++)
        {
            sx += points[i].X;
            sy += points[i].Y;
            sz += points[i].Z;
            sm += points[i].M;
        }
        double inv = 1.0 / points.Length;
        mean = new Point4D(sx * inv, sy * inv, sz * inv, sm * inv);
        return true;
    }

    /// <summary>Squared Euclidean distance in 4D. Cheaper than
    /// <see cref="Distance"/> when only a relative ordering is needed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double DistanceSquared(Point4D a, Point4D b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z, dm = a.M - b.M;
        return dx * dx + dy * dy + dz * dz + dm * dm;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Distance(Point4D a, Point4D b) => Math.Sqrt(DistanceSquared(a, b));

    /// <summary>Read 4 little-endian float8s into a Point4D.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Point4D FromLittleEndian(ReadOnlySpan<byte> source)
    {
        if (source.Length < SizeBytes)
        {
            throw new ArgumentException(
                $"Point4D.FromLittleEndian requires at least {SizeBytes} bytes; got {source.Length}.",
                nameof(source));
        }
        return new Point4D(
            BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(0, 8)),
            BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(8, 8)),
            BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(16, 8)),
            BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(24, 8)));
    }

    /// <summary>Write the four axes little-endian into the destination span.
    /// Used by the native payload build path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLittleEndian(Span<byte> destination)
    {
        if (destination.Length < SizeBytes)
        {
            throw new ArgumentException(
                $"Point4D.WriteLittleEndian requires at least {SizeBytes} bytes; got {destination.Length}.",
                nameof(destination));
        }
        BinaryPrimitives.WriteDoubleLittleEndian(destination.Slice(0, 8), X);
        BinaryPrimitives.WriteDoubleLittleEndian(destination.Slice(8, 8), Y);
        BinaryPrimitives.WriteDoubleLittleEndian(destination.Slice(16, 8), Z);
        BinaryPrimitives.WriteDoubleLittleEndian(destination.Slice(24, 8), M);
    }

    /// <summary>Read 4 big-endian float8s into a Point4D. Used when decoding
    /// the PG binary protocol (point4d_send / receive uses pq_sendfloat8 =
    /// network byte order).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Point4D FromBigEndian(ReadOnlySpan<byte> source)
    {
        if (source.Length < SizeBytes)
        {
            throw new ArgumentException(
                $"Point4D.FromBigEndian requires at least {SizeBytes} bytes; got {source.Length}.",
                nameof(source));
        }
        return new Point4D(
            BinaryPrimitives.ReadDoubleBigEndian(source.Slice(0, 8)),
            BinaryPrimitives.ReadDoubleBigEndian(source.Slice(8, 8)),
            BinaryPrimitives.ReadDoubleBigEndian(source.Slice(16, 8)),
            BinaryPrimitives.ReadDoubleBigEndian(source.Slice(24, 8)));
    }

    /// <summary>Write the four axes big-endian. PG binary protocol form.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBigEndian(Span<byte> destination)
    {
        if (destination.Length < SizeBytes)
        {
            throw new ArgumentException(
                $"Point4D.WriteBigEndian requires at least {SizeBytes} bytes; got {destination.Length}.",
                nameof(destination));
        }
        BinaryPrimitives.WriteDoubleBigEndian(destination.Slice(0, 8), X);
        BinaryPrimitives.WriteDoubleBigEndian(destination.Slice(8, 8), Y);
        BinaryPrimitives.WriteDoubleBigEndian(destination.Slice(16, 8), Z);
        BinaryPrimitives.WriteDoubleBigEndian(destination.Slice(24, 8), M);
    }
}
