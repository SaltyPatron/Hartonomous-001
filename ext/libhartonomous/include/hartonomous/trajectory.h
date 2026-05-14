/* libhartonomous — trajectory.h
 *
 * PostGIS WKB trajectory walker. Parses LINESTRINGZM / MULTILINESTRINGZM
 * EWKB byte streams from substrate.physicality.geom / substrate.edge.geom
 * and fires a per-vertex callback. Pure C kernel — no PG dependency, no
 * SPI, no allocation. Callable from PG SRFs (pg_trajectory_walk.c) and
 * from C# managed code (via P/Invoke through
 * Hartonomous.Core.Native.TrajectoryNative).
 *
 * Vertex coordinates are returned raw. The caller chooses how to
 * interpret them:
 *   * Atom physicality (POINTZM): vertex coordinates are real metric
 *     positions (codepoint S^3 Super-Fibonacci, audio frame, image
 *     pixel, etc.).
 *   * Composition physicality (LINESTRINGZM ingestion trajectory):
 *     vertex coordinates are mantissa-packed identity bits per the
 *     substrate.bb_* contract — X = child hash bits 0..51 (via
 *     bb_pack_hash_lo), Y = ordinal + RLE bit-banged (bb_pack_ordinal_rle),
 *     Z = child hash bits 52..103 (bb_pack_hash_hi), M = metadata
 *     (bb_pack_metadata). Inverse: `(int64_t)(d - 2^52)`.
 *   * Edge geom (LINESTRINGZM through participant centroid_4d values):
 *     vertex coordinates are real metric positions for shape similarity
 *     queries.
 */

#ifndef HARTONOMOUS_TRAJECTORY_H
#define HARTONOMOUS_TRAJECTORY_H

#include <stddef.h>
#include <stdint.h>

#include "hartonomous/version.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Per-vertex callback. Return 0 to continue; non-zero aborts the walk and
 * is propagated as the unpack function's return code.
 *   sub_idx     — 0 for LINESTRINGZM; per-sub-linestring index for
 *                 MULTILINESTRINGZM (0, 1, ..., n_lines - 1)
 *   vertex_idx  — 0-based vertex position within the sub-linestring
 *   x, y, z, m  — vertex coordinates, raw (not unpacked from any encoding)
 */
typedef int (*lh_traj_vertex_cb)(
    void* ctx,
    int sub_idx,
    int vertex_idx,
    double x,
    double y,
    double z,
    double m);

/*
 * Walk a PostGIS WKB / EWKB LINESTRINGZM or MULTILINESTRINGZM and fire
 * <cb> once per vertex in linestring vertex order, sub-linestring order
 * for multi-segment shapes.
 *
 * Supported types: LINESTRINGZM, MULTILINESTRINGZM. Other geometry types
 * (POINT, POLYGON, MULTIPOINT, MULTIPOLYGON, GEOMETRYCOLLECTION) return
 * -1 — substrate composition / edge trajectories are always
 * LINESTRINGZM / MULTILINESTRINGZM.
 *
 * Returns:
 *   0   on success (entire WKB walked, every vertex visited)
 *   N   the callback's non-zero return code if the walk aborted early
 *   -1  on parse error (truncated input, wrong dimensionality, unsupported
 *       geometry type, endianness byte outside {0, 1})
 */
HARTONOMOUS_API int hartonomous_trajectory_unpack(
    const uint8_t* wkb,
    size_t wkb_len,
    lh_traj_vertex_cb cb,
    void* ctx);

#ifdef __cplusplus
}
#endif

#endif /* HARTONOMOUS_TRAJECTORY_H */
