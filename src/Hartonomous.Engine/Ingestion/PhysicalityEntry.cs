using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// Coordinate surface this physicality row populates. The pipeline routes the
/// row to the corresponding column on substrate.physicality:
/// <list type="bullet">
///   <item><see cref="PostGisGeom"/> → <c>geom</c> via <c>ST_GeomFromWKB</c>.</item>
///   <item><see cref="Point4D"/>     → <c>pt4d</c> via <c>public.point4d(x1,x2,x3,x4)</c>.</item>
///   <item><see cref="LineString4D"/>→ <c>ls4d</c> via <c>public.array_to_linestring4d(double precision[])</c>.</item>
/// </list>
/// </summary>
internal enum PhysicalitySurface : byte
{
    PostGisGeom = 0,
    Point4D = 1,
    LineString4D = 2,
}

/// <summary>
/// One row queued for the substrate.physicality table. Carries exactly one
/// payload: <see cref="PostGisWkb"/> (when <see cref="Surface"/> is
/// <see cref="PhysicalitySurface.PostGisGeom"/>),
/// <see cref="Point4DCoords"/> (when <see cref="Surface"/> is
/// <see cref="PhysicalitySurface.Point4D"/>), or
/// <see cref="LineString4DCoords"/> (a flat float8[] of length 4n, when
/// <see cref="Surface"/> is <see cref="PhysicalitySurface.LineString4D"/>).
/// </summary>
internal readonly record struct PhysicalityEntry(
    EntityHandle Entity,
    string PhysicalityTypeCode,
    PhysicalitySurface Surface,
    byte[]? PostGisWkb,
    double[]? Point4DCoords,
    double[]? LineString4DCoords);
