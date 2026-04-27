using System;
using System.Buffers.Binary;

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

    public static byte[] PointZM(double x, double y, double z, double m)
    {
        byte[] buf = new byte[1 + 4 + 32];
        buf[0] = LittleEndian;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), WkbPointZM);
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(5, 8), x);
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(13, 8), y);
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(21, 8), z);
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(29, 8), m);
        return buf;
    }

    public static byte[] LineStringZM(ReadOnlySpan<(double X, double Y, double Z, double M)> vertices)
    {
        if (vertices.Length < 1)
        {
            throw new ArgumentException("LINESTRINGZM requires at least one vertex.", nameof(vertices));
        }
        byte[] buf = new byte[1 + 4 + 4 + (32 * vertices.Length)];
        buf[0] = LittleEndian;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), WkbLineStringZM);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(5, 4), (uint)vertices.Length);
        int offset = 9;
        for (int i = 0; i < vertices.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(offset, 8), vertices[i].X);
            BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(offset + 8, 8), vertices[i].Y);
            BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(offset + 16, 8), vertices[i].Z);
            BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(offset + 24, 8), vertices[i].M);
            offset += 32;
        }
        return buf;
    }
}
