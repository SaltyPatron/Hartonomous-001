/*
 * delaunay_4d.cpp — incremental Bowyer-Watson 4D Delaunay tetrahedralization.
 *
 * A 4-simplex in R⁴ has 5 vertices, 5 tetrahedral facets (each with 4
 * vertices), 10 triangular ridges, 10 edges, 5 faces. Bowyer-Watson in
 * any dimension follows the same recipe:
 *
 *   1. Build a super-simplex enclosing all input points.
 *   2. For each point p:
 *        a. Find the set S of existing simplices whose circumsphere
 *           strictly contains p.
 *        b. Remove S. The boundary of S (its "cavity") consists of
 *           tetrahedral facets that appeared only once across S.
 *        c. For every boundary facet F, form a new 4-simplex (F ∪ {p}).
 *   3. After all points are inserted, discard simplices that use any
 *      super-simplex vertex.
 *
 * Circumsphere for a 4-simplex {v0..v4}: the center c satisfies
 * |c - v_i|² = R². Subtract equation i=0 from i=1..4 to get a 4×4 linear
 * system  2·(v_i - v_0)·c = |v_i|² - |v_0|²  (i=1..4), solved via Eigen
 * LU decomposition. Then R² = |v_0 - c|². A query point p is strictly
 * inside the circumsphere iff |p - c|² < R². Degenerate (coplanar) simplex
 * → singular system → treated as "does not contain" (p gets attached to
 * a neighbouring simplex instead).
 *
 * Determinism: Eigen partial-pivot LU with MKL CBWR=AUTO,STRICT is
 * deterministic. Boundary-facet extraction sorts facets lexicographically
 * and pairs duplicates — iteration order is purely index-driven.
 */

#include "hartonomous.h"

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <cfloat>
#include <vector>
#include <algorithm>
#include <array>
#include <cmath>
#include <limits>

#include <Eigen/Dense>

namespace {

struct Simplex {
    std::array<int64_t, 5> v;   // vertex indices (into the extended point set)
    bool alive;
};

// Build the circumcenter of a 4-simplex defined by 5 vertices in R⁴.
// Returns false on singular system (degenerate simplex).
bool circumcenter_4d(
    const double* v0, const double* v1, const double* v2,
    const double* v3, const double* v4,
    double* out_center, double* out_r2
) {
    Eigen::Matrix4d A;
    Eigen::Vector4d b;
    const double* vs[5] = {v0, v1, v2, v3, v4};
    double sq0 = 0.0;
    for (int i = 0; i < 4; ++i) sq0 += v0[i] * v0[i];
    for (int r = 0; r < 4; ++r) {
        const double* vi = vs[r + 1];
        double sqi = 0.0;
        for (int j = 0; j < 4; ++j) {
            A(r, j) = 2.0 * (vi[j] - v0[j]);
            sqi += vi[j] * vi[j];
        }
        b(r) = sqi - sq0;
    }
    Eigen::PartialPivLU<Eigen::Matrix4d> lu(A);
    // Determinant magnitude filter — small = degenerate.
    double det = lu.determinant();
    if (!std::isfinite(det) || std::abs(det) < 1e-18) return false;
    Eigen::Vector4d c = lu.solve(b);
    if (!c.allFinite()) return false;
    double r2 = 0.0;
    for (int j = 0; j < 4; ++j) {
        double t = v0[j] - c(j);
        r2 += t * t;
    }
    for (int j = 0; j < 4; ++j) out_center[j] = c(j);
    *out_r2 = r2;
    return true;
}

bool contains_point(
    const double* p,
    const double* center,
    double r2
) {
    double dd = 0.0;
    for (int j = 0; j < 4; ++j) {
        double t = p[j] - center[j];
        dd += t * t;
    }
    // Strict inequality with an epsilon tolerance; ties (cospherical 6
    // points) default to "not contained" — gives a valid but not unique
    // triangulation for degenerate inputs. Spec #Law6 determinism is
    // preserved by the constant epsilon.
    return dd < r2 - 1e-12;
}

// Represent a tetrahedral facet as a sorted 4-tuple of vertex indices.
struct Facet {
    std::array<int64_t, 4> v;
    int64_t owner;  // index of the simplex that contributed this facet

    bool operator<(const Facet& o) const { return v < o.v; }
    bool operator==(const Facet& o) const { return v == o.v; }
};

Facet make_facet(const Simplex& s, int exclude, int64_t owner) {
    Facet f; int p = 0;
    for (int i = 0; i < 5; ++i) {
        if (i != exclude) f.v[p++] = s.v[i];
    }
    std::sort(f.v.begin(), f.v.end());
    f.owner = owner;
    return f;
}

}  // namespace

extern "C" int hartonomous_delaunay_4d_f64(
    int64_t n,
    const double* points,
    int64_t* out_simplex_count,
    int64_t* out_simplices,   // nullable: if null, only reports count
    int64_t  out_capacity
) {
    if (points == nullptr || out_simplex_count == nullptr) return -1;
    if (n < 5) return -2;

    // --- 1. Super-simplex ---
    // Axis-aligned bounding box, expanded by a huge factor so super-vertices
    // never lie on a natural circumsphere of input points.
    double mn[4] = { DBL_MAX,  DBL_MAX,  DBL_MAX,  DBL_MAX};
    double mx[4] = {-DBL_MAX, -DBL_MAX, -DBL_MAX, -DBL_MAX};
    for (int64_t i = 0; i < n; ++i) {
        for (int j = 0; j < 4; ++j) {
            double v = points[i * 4 + j];
            if (v < mn[j]) mn[j] = v;
            if (v > mx[j]) mx[j] = v;
        }
    }
    double span = 0.0;
    for (int j = 0; j < 4; ++j) {
        double s = mx[j] - mn[j];
        if (s > span) span = s;
    }
    if (span <= 0.0) span = 1.0;
    double M = 1000.0 * span + 1.0;  // margin multiplier
    double cx[4];
    for (int j = 0; j < 4; ++j) cx[j] = 0.5 * (mn[j] + mx[j]);

    // 5 super-vertices: one pushed along each axis, one "neg-diagonal".
    // Place them far enough out that their circumscribed 4-sphere
    // encloses every real point with huge margin.
    std::vector<double> ext(static_cast<size_t>(n + 5) * 4);
    std::memcpy(ext.data(), points, static_cast<size_t>(n) * 4 * sizeof(double));
    double* S = ext.data() + n * 4;
    // Simplex as 5 points: center + M * e_j  (j=0..3), and center - M * (1,1,1,1)
    for (int k = 0; k < 4; ++k) {
        for (int j = 0; j < 4; ++j) S[k * 4 + j] = cx[j];
        S[k * 4 + k] += M;
    }
    for (int j = 0; j < 4; ++j) S[4 * 4 + j] = cx[j] - M;

    std::vector<Simplex> T;
    T.reserve(static_cast<size_t>(n) * 10 + 1);
    Simplex s0;
    s0.v = { n, n + 1, n + 2, n + 3, n + 4 };
    s0.alive = true;
    T.push_back(s0);

    // --- 2. Insert each real point ---
    for (int64_t ip = 0; ip < n; ++ip) {
        const double* p = ext.data() + ip * 4;

        std::vector<int64_t> bad;
        for (int64_t ti = 0; ti < static_cast<int64_t>(T.size()); ++ti) {
            if (!T[ti].alive) continue;
            const Simplex& s = T[ti];
            double cc[4], r2;
            if (!circumcenter_4d(
                    ext.data() + s.v[0] * 4, ext.data() + s.v[1] * 4,
                    ext.data() + s.v[2] * 4, ext.data() + s.v[3] * 4,
                    ext.data() + s.v[4] * 4, cc, &r2)) {
                continue;
            }
            if (contains_point(p, cc, r2)) bad.push_back(ti);
        }
        if (bad.empty()) {
            // No simplex contains p — numerical degeneracy. Fall back to the
            // simplex whose centroid is nearest (guarantees progress).
            double best = DBL_MAX;
            int64_t best_i = -1;
            for (int64_t ti = 0; ti < static_cast<int64_t>(T.size()); ++ti) {
                if (!T[ti].alive) continue;
                double cent[4] = {0,0,0,0};
                for (int k = 0; k < 5; ++k) {
                    const double* vp = ext.data() + T[ti].v[k] * 4;
                    for (int j = 0; j < 4; ++j) cent[j] += vp[j];
                }
                double d = 0.0;
                for (int j = 0; j < 4; ++j) {
                    double t = p[j] - 0.2 * cent[j];
                    d += t * t;
                }
                if (d < best) { best = d; best_i = ti; }
            }
            if (best_i < 0) return -6;
            bad.push_back(best_i);
        }

        // Gather cavity facets. A facet appearing in exactly one bad simplex
        // is on the boundary; a facet appearing twice is interior and dies.
        std::vector<Facet> facets;
        facets.reserve(bad.size() * 5);
        for (int64_t ti : bad) {
            for (int excl = 0; excl < 5; ++excl) {
                facets.push_back(make_facet(T[ti], excl, ti));
            }
        }
        std::sort(facets.begin(), facets.end());
        std::vector<Facet> boundary;
        boundary.reserve(facets.size());
        for (size_t i = 0; i < facets.size(); ) {
            size_t j = i + 1;
            while (j < facets.size() && facets[j].v == facets[i].v) ++j;
            if (j - i == 1) boundary.push_back(facets[i]);
            i = j;
        }

        // Kill bad simplices.
        for (int64_t ti : bad) T[ti].alive = false;

        // Create new simplices from boundary facets and p.
        for (const Facet& f : boundary) {
            Simplex ns;
            ns.v = { f.v[0], f.v[1], f.v[2], f.v[3], ip };
            // Canonicalize: sort ascending by vertex index for deterministic output.
            std::sort(ns.v.begin(), ns.v.end());
            ns.alive = true;
            T.push_back(ns);
        }
    }

    // --- 3. Discard simplices touching super-vertices. ---
    int64_t count = 0;
    for (const Simplex& s : T) {
        if (!s.alive) continue;
        bool touches_super = false;
        for (int k = 0; k < 5; ++k) {
            if (s.v[k] >= n) { touches_super = true; break; }
        }
        if (!touches_super) ++count;
    }
    *out_simplex_count = count;
    if (out_simplices == nullptr) return 0;
    if (out_capacity < count) return -2;

    // Canonical deterministic ordering: sort surviving simplices
    // lexicographically by their 5-tuple.
    std::vector<std::array<int64_t, 5>> survivors;
    survivors.reserve(static_cast<size_t>(count));
    for (const Simplex& s : T) {
        if (!s.alive) continue;
        bool touches_super = false;
        for (int k = 0; k < 5; ++k) if (s.v[k] >= n) { touches_super = true; break; }
        if (!touches_super) survivors.push_back(s.v);
    }
    std::sort(survivors.begin(), survivors.end());
    for (int64_t i = 0; i < count; ++i) {
        for (int k = 0; k < 5; ++k) out_simplices[i * 5 + k] = survivors[static_cast<size_t>(i)][k];
    }
    return 0;
}
