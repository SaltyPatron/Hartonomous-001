using System;
using System.Buffers.Binary;
using Hartonomous.Core.Geometry;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// Build PostGIS-native EWKB payloads for POINTZM and LINESTRINGZM. The
/// resulting bytea round-trips through the substrate's
/// <c>geometry(GeometryZM)</c> storage type via <c>ST_GeomFromEWKB</c> or
/// PostGIS's implicit bytea-to-geometry cast — no custom geometry4d type,
/// no bridge function, no parallel binary format.
///
/// EWKB POINTZM (37 bytes, no SRID):
///   [0]      = 0x01            (little-endian byte order)
///   [1..4]   = 0xC0000001 LE   (WKB type = POINT, Z_FLAG | M_FLAG, no SRID)
///   [5..12]  = double X
///   [13..20] = double Y
///   [21..28] = double Z
///   [29..36] = double M
///
/// EWKB LINESTRINGZM (9 + N*32 bytes, no SRID):
///   [0]      = 0x01
///   [1..4]   = 0xC0000002 LE   (WKB type = LINESTRING, Z_FLAG | M_FLAG)
///   [5..8]   = N (uint32 LE)
///   [9..]    = N × (double X, Y, Z, M)
/// </summary>
internal static class Geometry4dPayloadBuilder
{
    private const byte ByteOrderLittleEndian = 0x01;
    private const uint WkbZFlag      = 0x80000000;
    private const uint WkbMFlag      = 0x40000000;
    private const uint WkbPoint      = 1u;
    private const uint WkbLineString = 2u;
    private const uint EwkbPointZM      = WkbPoint      | WkbZFlag | WkbMFlag; // 0xC0000001
    private const uint EwkbLineStringZM = WkbLineString | WkbZFlag | WkbMFlag; // 0xC0000002

    private const int PointZmSize      = 1 + 4 + (Point4D.SizeBytes);                 // 37 bytes
    private const int LineStringZmBase = 1 + 4 + 4;                                   // 9 bytes header

    public static byte[] Point(Point4D p)
    {
        byte[] buf = new byte[PointZmSize];
        buf[0] = ByteOrderLittleEndian;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), EwkbPointZM);
        p.WriteLittleEndian(buf.AsSpan(5, Point4D.SizeBytes));
        return buf;
    }

    public static byte[] LineString(ReadOnlySpan<Point4D> vertices)
    {
        if (vertices.Length < 1)
        {
            throw new ArgumentException("LINESTRINGZM requires at least one vertex.", nameof(vertices));
        }

        // PostGIS rejects single-vertex LINESTRINGs. Singleton compositions
        // get a doubled-vertex layout so walkers (which dedupe by
        // (ordinal, child_hash)) still reverse-resolve to one child.
        int nEmit = vertices.Length == 1 ? 2 : vertices.Length;
        byte[] buf = new byte[LineStringZmBase + (Point4D.SizeBytes * nEmit)];
        buf[0] = ByteOrderLittleEndian;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), EwkbLineStringZM);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(5, 4), (uint)nEmit);
        int offset = LineStringZmBase;
        for (int i = 0; i < nEmit; i++)
        {
            int src = i < vertices.Length ? i : 0;
            vertices[src].WriteLittleEndian(buf.AsSpan(offset, Point4D.SizeBytes));
            offset += Point4D.SizeBytes;
        }
        return buf;
    }

    public static bool TryExtractCentroid(ReadOnlySpan<byte> payload, out Point4D centroid)
    {
        centroid = default;
        if (payload.Length < 5)
        {
            return false;
        }
        if (payload[0] != ByteOrderLittleEndian)
        {
            return false;
        }
        uint typeCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));

        if (typeCode == EwkbPointZM && payload.Length == PointZmSize)
        {
            centroid = Point4D.FromLittleEndian(payload.Slice(5, Point4D.SizeBytes));
            return true;
        }

        if (typeCode == EwkbLineStringZM && payload.Length >= LineStringZmBase)
        {
            uint n = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(5, 4));
            if (n == 0 || payload.Length != LineStringZmBase + (Point4D.SizeBytes * (int)n))
            {
                return false;
            }
            double sx = 0, sy = 0, sz = 0, sm = 0;
            int offset = LineStringZmBase;
            for (int i = 0; i < n; i++)
            {
                Point4D p = Point4D.FromLittleEndian(payload.Slice(offset, Point4D.SizeBytes));
                sx += p.X; sy += p.Y; sz += p.Z; sm += p.M;
                offset += Point4D.SizeBytes;
            }
            double inv = 1.0 / n;
            centroid = new Point4D(sx * inv, sy * inv, sz * inv, sm * inv);
            return true;
        }

        return false;
    }
}
