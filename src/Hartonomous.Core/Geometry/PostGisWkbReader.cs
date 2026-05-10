using System;
using System.Buffers.Binary;

namespace Hartonomous.Core.Geometry;

/// <summary>
/// Inverse of the Engine-side PostGisWkbBuilder for the path that needs a
/// single 4D representative <see cref="Point4D"/> out of any GeometryZM
/// WKB. Used on call paths where the WKB came from a producer that didn't
/// keep the centroid alongside (notably the native text_decompose ABI,
/// which returns WKB only). Decomposers that already have the centroid in
/// hand should pass it directly via PhysicalityRecord/PhysicalityEntry
/// instead of round-tripping through this reader.
///
/// <para>
/// Supported subtypes: POINTZM (3001), LINESTRINGZM (3002), POLYGONZM
/// (3003), MULTILINESTRINGZM (3005). For non-POINT subtypes the
/// representative point is the unweighted 4D mean of every vertex.
/// POLYGONZM averages the outer ring only. MULTILINESTRINGZM averages
/// across every component linestring.
/// </para>
/// </summary>
public static class PostGisWkbReader
{
    private const uint WkbPointZM = 3001u;
    private const uint WkbLineStringZM = 3002u;
    private const uint WkbPolygonZM = 3003u;
    private const uint WkbMultiLineStringZM = 3005u;

    public static bool TryExtractCentroid(ReadOnlySpan<byte> wkb, out Point4D centroid)
    {
        centroid = default;
        if (wkb.Length < 5)
        {
            return false;
        }
        if (wkb[0] != 0x01)
        {
            return false; // little-endian only
        }

        uint typeWord = BinaryPrimitives.ReadUInt32LittleEndian(wkb.Slice(1, 4));

        const uint EwkbSridFlag = 0x20000000u;
        const uint EwkbZFlag    = 0x80000000u;
        const uint EwkbMFlag    = 0x40000000u;
        bool ewkbHasSrid = (typeWord & EwkbSridFlag) != 0;
        uint baseType = typeWord & ~(EwkbSridFlag | EwkbZFlag | EwkbMFlag);

        uint isoType = typeWord;
        if ((typeWord & (EwkbZFlag | EwkbMFlag)) != 0)
        {
            uint kind = baseType;
            if (kind == 1u)
            {
                isoType = WkbPointZM;
            }
            else if (kind == 2u)
            {
                isoType = WkbLineStringZM;
            }
            else if (kind == 3u)
            {
                isoType = WkbPolygonZM;
            }
            else if (kind == 5u)
            {
                isoType = WkbMultiLineStringZM;
            }
        }

        int offset = 5 + (ewkbHasSrid ? 4 : 0);

        switch (isoType)
        {
            case WkbPointZM:
                if (wkb.Length < offset + Point4D.SizeBytes)
                {
                    return false;
                }
                centroid = Point4D.FromLittleEndian(wkb.Slice(offset, Point4D.SizeBytes));
                return true;

            case WkbLineStringZM:
                return TryMeanOfPointStream(wkb, offset, out centroid);

            case WkbPolygonZM:
                if (wkb.Length < offset + 4)
                {
                    return false;
                }
                uint nRings = BinaryPrimitives.ReadUInt32LittleEndian(wkb.Slice(offset, 4));
                if (nRings == 0)
                {
                    return false;
                }
                return TryMeanOfPointStream(wkb, offset + 4, out centroid);

            case WkbMultiLineStringZM:
                if (wkb.Length < offset + 4)
                {
                    return false;
                }
                uint nLines = BinaryPrimitives.ReadUInt32LittleEndian(wkb.Slice(offset, 4));
                if (nLines == 0)
                {
                    return false;
                }
                int cur = offset + 4;
                double sx = 0, sy = 0, sz = 0, sm = 0;
                long total = 0;
                for (uint i = 0; i < nLines; i++)
                {
                    if (wkb.Length < cur + 9)
                    {
                        return false;
                    }
                    cur += 5;
                    uint nP = BinaryPrimitives.ReadUInt32LittleEndian(wkb.Slice(cur, 4));
                    cur += 4;
                    int needed = (int)nP * Point4D.SizeBytes;
                    if (wkb.Length < cur + needed)
                    {
                        return false;
                    }
                    for (uint j = 0; j < nP; j++)
                    {
                        Point4D pt = Point4D.FromLittleEndian(wkb.Slice(cur, Point4D.SizeBytes));
                        sx += pt.X;
                        sy += pt.Y;
                        sz += pt.Z;
                        sm += pt.M;
                        cur += Point4D.SizeBytes;
                        total++;
                    }
                }
                if (total == 0)
                {
                    return false;
                }
                double inv = 1.0 / total;
                centroid = new Point4D(sx * inv, sy * inv, sz * inv, sm * inv);
                return true;

            default:
                return false;
        }
    }

    private static bool TryMeanOfPointStream(
        ReadOnlySpan<byte> wkb,
        int offset,
        out Point4D centroid)
    {
        centroid = default;
        if (wkb.Length < offset + 4)
        {
            return false;
        }
        uint nPoints = BinaryPrimitives.ReadUInt32LittleEndian(wkb.Slice(offset, 4));
        if (nPoints == 0)
        {
            return false;
        }
        int cur = offset + 4;
        int needed = (int)nPoints * Point4D.SizeBytes;
        if (wkb.Length < cur + needed)
        {
            return false;
        }
        double sx = 0, sy = 0, sz = 0, sm = 0;
        for (uint i = 0; i < nPoints; i++)
        {
            Point4D pt = Point4D.FromLittleEndian(wkb.Slice(cur, Point4D.SizeBytes));
            sx += pt.X;
            sy += pt.Y;
            sz += pt.Z;
            sm += pt.M;
            cur += Point4D.SizeBytes;
        }
        double inv = 1.0 / nPoints;
        centroid = new Point4D(sx * inv, sy * inv, sz * inv, sm * inv);
        return true;
    }
}
