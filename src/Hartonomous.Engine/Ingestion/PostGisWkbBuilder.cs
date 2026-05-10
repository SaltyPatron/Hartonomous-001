using System;
using System.Buffers.Binary;
using Hartonomous.Core.Geometry;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// Constructs PostGIS Well-Known Binary (WKB) byte buffers for the geometry
/// subtypes the substrate writes. Uses the OGC ISO WKB encoding with the
/// "ZM" type codes (1001/1002 for Z-only, 2001/2002 for M-only, 3001/3002
/// for ZM) that PostGIS accepts via <c>ST_GeomFromWKB</c>.
///
/// Endianness: little-endian throughout (byte 0 = 0x01) — matches the host
/// byte order on every supported deployment platform and is what
/// <c>NpgsqlBinaryImporter</c> ships most efficiently.
///
/// Type codes:
///   POINT          = 1
///   LINESTRING     = 2
///   POINTZM        = 1 + 1000 + 2000 = 3001  (Z and M flags)
///   LINESTRINGZM   = 2 + 1000 + 2000 = 3002
///
/// Layout for POINTZM (37 bytes):
///   [byte order:1][type:4][x:8][y:8][z:8][m:8]
///
/// Layout for LINESTRINGZM (9 + 32*npoints bytes):
///   [byte order:1][type:4][npoints:4][x1:8][y1:8][z1:8][m1:8] ...
/// </summary>
internal static class PostGisWkbBuilder
{
    private const byte LittleEndian = 0x01;
    private const uint WkbPointZM = 3001u;
    private const uint WkbLineStringZM = 3002u;

    public static byte[] PointZM(Point4D p)
    {
        byte[] buf = new byte[1 + 4 + Point4D.SizeBytes];
        buf[0] = LittleEndian;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), WkbPointZM);
        p.WriteLittleEndian(buf.AsSpan(5, Point4D.SizeBytes));
        return buf;
    }

    public static byte[] PointZM(double x, double y, double z, double m) =>
        PointZM(new Point4D(x, y, z, m));

    public static byte[] LineStringZM(ReadOnlySpan<Point4D> vertices)
    {
        if (vertices.Length < 1)
        {
            throw new ArgumentException("LINESTRINGZM requires at least one vertex.", nameof(vertices));
        }
        byte[] buf = new byte[1 + 4 + 4 + (Point4D.SizeBytes * vertices.Length)];
        buf[0] = LittleEndian;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), WkbLineStringZM);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(5, 4), (uint)vertices.Length);
        int offset = 9;
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].WriteLittleEndian(buf.AsSpan(offset, Point4D.SizeBytes));
            offset += Point4D.SizeBytes;
        }
        return buf;
    }

    /// <summary>Tuple-accepting compatibility overload. Internally normalizes
    /// every vertex to <see cref="Point4D"/>; new call sites should pass
    /// Point4D directly.</summary>
    public static byte[] LineStringZM(ReadOnlySpan<(double X, double Y, double Z, double M)> vertices)
    {
        if (vertices.Length < 1)
        {
            throw new ArgumentException("LINESTRINGZM requires at least one vertex.", nameof(vertices));
        }
        Span<Point4D> typed = vertices.Length <= 64
            ? stackalloc Point4D[vertices.Length]
            : new Point4D[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            typed[i] = new Point4D(vertices[i].X, vertices[i].Y, vertices[i].Z, vertices[i].M);
        }
        return LineStringZM((ReadOnlySpan<Point4D>)typed);
    }
}
