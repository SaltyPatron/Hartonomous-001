/*
 * hartonomous_pg.h — shared private declarations for the hartonomous
 * PostgreSQL extension. Defines the on-disk and in-memory layout for
 * point4d / box4d / linestring4d, plus the PG_GETARG/PG_RETURN macros
 * the per-source files use to keep their bodies short.
 *
 * Layouts mirror those documented in docs/specs/native/4d-type-and-index.md
 * §"Type: point4d", §"Type: box4d".
 */
#ifndef HARTONOMOUS_PG_H
#define HARTONOMOUS_PG_H

#include "postgres.h"
#include "fmgr.h"

/* point4d: 32-byte fixed-size, pass-by-reference, alignment=double. */
typedef struct Point4D
{
    double x[4];
} Point4D;

/* box4d: 64-byte fixed-size, pass-by-reference, alignment=double.
 * Layout: min[4] then max[4]. Matches `double box[8]` used by the
 * libhartonomous bbox helpers, so we can pass `&box->min[0]` straight in. */
typedef struct Box4D
{
    double min[4];
    double max[4];
} Box4D;

/* linestring4d: varlena. Header is `vl_len_` (PG-managed) followed by
 * `npoints` and a flexible array of `npoints` Point4D values. */
typedef struct LineString4D
{
    int32   vl_len_;     /* varlena header — set via SET_VARSIZE */
    int32   npoints;
    Point4D points[FLEXIBLE_ARRAY_MEMBER];
} LineString4D;

#define LS4D_HDRSZ offsetof(LineString4D, points)
#define LS4D_SIZE(npts) (LS4D_HDRSZ + (size_t)(npts) * sizeof(Point4D))

/* Datum macros — kept consistent with the geometric extension idiom. */
#define DatumGetPoint4DP(X)      ((Point4D *) DatumGetPointer(X))
#define PG_GETARG_POINT4D_P(n)   DatumGetPoint4DP(PG_GETARG_DATUM(n))
#define PG_RETURN_POINT4D_P(x)   PG_RETURN_POINTER(x)

#define DatumGetBox4DP(X)        ((Box4D *) DatumGetPointer(X))
#define PG_GETARG_BOX4D_P(n)     DatumGetBox4DP(PG_GETARG_DATUM(n))
#define PG_RETURN_BOX4D_P(x)     PG_RETURN_POINTER(x)

#define DatumGetLineString4DP(X) ((LineString4D *) PG_DETOAST_DATUM(X))
#define PG_GETARG_LINESTRING4D_P(n) DatumGetLineString4DP(PG_GETARG_DATUM(n))
#define PG_RETURN_LINESTRING4D_P(x) PG_RETURN_POINTER(x)

/* ── geometry4d umbrella ──────────────────────────────────────────── */
/* Tag values — 1 .. 10. Stable across releases (never renumber). */
#define G4D_TAG_POINT               1u
#define G4D_TAG_LINESTRING          2u
#define G4D_TAG_POLYGON             3u
#define G4D_TAG_MULTIPOINT          4u
#define G4D_TAG_MULTILINESTRING     5u
#define G4D_TAG_MULTIPOLYGON        6u
#define G4D_TAG_TRIANGLE            7u
#define G4D_TAG_TIN                 8u
#define G4D_TAG_POLYHEDRALSURFACE   9u
#define G4D_TAG_GEOMETRYCOLLECTION 10u

/* Varlena layout (after the PG 4-byte length header):
 *   [u8 endian=1][u32 tag][u32 srid=0][payload...]
 *
 * Payload shapes:
 *   POINT               : 4×f64                                     (32 B)
 *   LINESTRING          : u32 npoints, npoints×(4×f64)
 *   POLYGON             : u32 nrings, for each ring: u32 npoints, npoints×(4×f64)
 *   MULTIPOINT          : u32 ngeoms, ngeoms×(4×f64)
 *   MULTILINESTRING     : u32 ngeoms, ngeoms×LINESTRING-payload
 *   MULTIPOLYGON        : u32 ngeoms, ngeoms×POLYGON-payload
 *   TRIANGLE            : u32 nrings=1, u32 npoints=4, 4×(4×f64) (ring closed: p[0]==p[3])
 *   TIN                 : u32 ntri,  ntri × TRIANGLE-payload (each with nrings/npoints)
 *   POLYHEDRALSURFACE   : u32 npoly, npoly × POLYGON-payload
 *   GEOMETRYCOLLECTION  : u32 ngeoms, each item = [u32 tag][payload]
 *
 * SRID is always 0. Endian is always little-endian on disk (value 1).
 * Caller accessors detoast via PG_DETOAST_DATUM.
 */
typedef struct Geometry4D
{
    int32   vl_len_;     /* varlena header — set via SET_VARSIZE */
    uint8   endian;      /* always 1 */
    uint32  tag;         /* G4D_TAG_* */
    uint32  srid;        /* always 0 */
    /* payload follows; declared separately to keep alignment simple */
} Geometry4D;

#define G4D_HDR_SIZE (offsetof(Geometry4D, srid) + sizeof(uint32))
#define G4D_PAYLOAD(g) ((char *) (g) + G4D_HDR_SIZE)
#define G4D_PAYLOAD_SIZE(g) (VARSIZE_ANY_EXHDR(g) - (G4D_HDR_SIZE - VARHDRSZ))

#define DatumGetGeometry4DP(X)   ((Geometry4D *) PG_DETOAST_DATUM(X))
#define PG_GETARG_GEOMETRY4D_P(n) DatumGetGeometry4DP(PG_GETARG_DATUM(n))
#define PG_RETURN_GEOMETRY4D_P(x) PG_RETURN_POINTER(x)

/* Helpers implemented in pg_geometry4d.c. */
extern Geometry4D *g4d_new(uint32 tag, size_t payload_bytes);
extern void        g4d_compute_bbox(const Geometry4D *g, Box4D *out);
extern bool        g4d_validate(const Geometry4D *g);

/* Allocator helpers used across multiple .c files. */
static inline Point4D *
point4d_alloc(void)
{
    return (Point4D *) palloc(sizeof(Point4D));
}

static inline Box4D *
box4d_alloc(void)
{
    return (Box4D *) palloc(sizeof(Box4D));
}

static inline Box4D *
box4d_from_point(const Point4D *p)
{
    Box4D *b = box4d_alloc();
    b->min[0] = b->max[0] = p->x[0];
    b->min[1] = b->max[1] = p->x[1];
    b->min[2] = b->max[2] = p->x[2];
    b->min[3] = b->max[3] = p->x[3];
    return b;
}

#endif /* HARTONOMOUS_PG_H */
