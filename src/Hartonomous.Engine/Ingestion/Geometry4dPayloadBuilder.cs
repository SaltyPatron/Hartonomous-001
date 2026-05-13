using System;
using System.Buffers.Binary;
using Hartonomous.Core.Geometry;

namespace Hartonomous.Engine.Ingestion;

internal static class Geometry4dPayloadBuilder
{
    private const byte PointTag = 1;
    private const byte LineStringTag = 2;

    public static byte[] Point(Point4D p)
    {
        byte[] buf = new byte[1 + Point4D.SizeBytes];
        buf[0] = PointTag;
        p.WriteLittleEndian(buf.AsSpan(1, Point4D.SizeBytes));
        return buf;
    }

    public static byte[] LineString(ReadOnlySpan<Point4D> vertices)
    {
        if (vertices.Length < 1)
        {
            throw new ArgumentException("LINESTRING4D requires at least one vertex.", nameof(vertices));
        }

        byte[] buf = new byte[1 + 4 + (Point4D.SizeBytes * vertices.Length)];
        buf[0] = LineStringTag;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), (uint)vertices.Length);
        int offset = 5;
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].WriteLittleEndian(buf.AsSpan(offset, Point4D.SizeBytes));
            offset += Point4D.SizeBytes;
        }
        return buf;
    }

    public static bool TryExtractCentroid(ReadOnlySpan<byte> payload, out Point4D centroid)
    {
        centroid = default;
        if (payload.Length == 1 + Point4D.SizeBytes && payload[0] == PointTag)
        {
            centroid = Point4D.FromLittleEndian(payload.Slice(1, Point4D.SizeBytes));
            return true;
        }

        if (payload.Length < 1 + 4 || payload[0] != LineStringTag)
        {
            return false;
        }

        uint n = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));
        if (n == 0 || payload.Length != 1 + 4 + (Point4D.SizeBytes * (int)n))
        {
            return false;
        }

        double sx = 0;
        double sy = 0;
        double sz = 0;
        double sm = 0;
        int offset = 5;
        for (int i = 0; i < n; i++)
        {
            Point4D p = Point4D.FromLittleEndian(payload.Slice(offset, Point4D.SizeBytes));
            sx += p.X;
            sy += p.Y;
            sz += p.Z;
            sm += p.M;
            offset += Point4D.SizeBytes;
        }

        double inv = 1.0 / n;
        centroid = new Point4D(sx * inv, sy * inv, sz * inv, sm * inv);
        return true;
    }
}
