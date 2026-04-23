// test_procrustes.cc — orthogonal Procrustes alignment.
//
// Covers: identity recovery, known-angle 2D rotation, 3D rotation,
// reflection correction (guarantees det(R) = +1), residual correctness,
// deterministic output across runs, rejection of null/zero args.

#include <gtest/gtest.h>
#include "hartonomous.h"
#include <cmath>
#include <vector>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

namespace {

// det of a d×d row-major matrix via expansion on small cases only.
double det2(const double* m) {
    return m[0] * m[3] - m[1] * m[2];
}
double det3(const double* m) {
    return m[0] * (m[4] * m[8] - m[5] * m[7])
         - m[1] * (m[3] * m[8] - m[5] * m[6])
         + m[2] * (m[3] * m[7] - m[4] * m[6]);
}

// Multiply R (d×d) by X (d×n), row-major.
void apply_rotation(int64_t d, int64_t n, const double* r, const double* x, double* y) {
    for (int64_t i = 0; i < d; ++i) {
        for (int64_t j = 0; j < n; ++j) {
            double s = 0.0;
            for (int64_t k = 0; k < d; ++k) {
                s += r[i * d + k] * x[k * n + j];
            }
            y[i * n + j] = s;
        }
    }
}

} // namespace

TEST(Procrustes, IdentityAlignsToIdentity) {
    // X = Y → R = I
    std::vector<double> x = {1, 2, 3, 4, 5, 6};  // 2×3
    std::vector<double> y = x;
    double r[4];
    double resid = -1.0;
    int rc = hartonomous_procrustes_f64(2, 3, x.data(), y.data(), r, &resid);
    ASSERT_EQ(rc, 0);
    EXPECT_NEAR(r[0], 1.0, 1e-12);
    EXPECT_NEAR(r[1], 0.0, 1e-12);
    EXPECT_NEAR(r[2], 0.0, 1e-12);
    EXPECT_NEAR(r[3], 1.0, 1e-12);
    EXPECT_NEAR(resid, 0.0, 1e-12);
}

TEST(Procrustes, Recovers2DRotation) {
    // Rotate X by 30° → Y; Procrustes should recover that rotation exactly.
    const double theta = M_PI / 6.0;
    const double c = std::cos(theta);
    const double s = std::sin(theta);
    std::vector<double> x = {1, 2, 3, 0, 1, 2};  // 2×3, three points
    std::vector<double> y(6);
    // Apply rotation [[c -s][s c]]
    for (int j = 0; j < 3; ++j) {
        y[0 * 3 + j] = c * x[0 * 3 + j] - s * x[1 * 3 + j];
        y[1 * 3 + j] = s * x[0 * 3 + j] + c * x[1 * 3 + j];
    }
    double r[4];
    int rc = hartonomous_procrustes_f64(2, 3, x.data(), y.data(), r, nullptr);
    ASSERT_EQ(rc, 0);
    EXPECT_NEAR(r[0],  c, 1e-10);
    EXPECT_NEAR(r[1], -s, 1e-10);
    EXPECT_NEAR(r[2],  s, 1e-10);
    EXPECT_NEAR(r[3],  c, 1e-10);
    EXPECT_NEAR(det2(r), 1.0, 1e-12);
}

TEST(Procrustes, Recovers3DRotation) {
    // Z-axis rotation by 45°.
    const double a = M_PI / 4.0;
    const double c = std::cos(a);
    const double s = std::sin(a);
    std::vector<double> x = {
        1, 0, 0, 2,
        0, 1, 0, 1,
        0, 0, 1, 3,
    };  // 3×4
    std::vector<double> y(12);
    for (int j = 0; j < 4; ++j) {
        y[0 * 4 + j] = c * x[0 * 4 + j] - s * x[1 * 4 + j];
        y[1 * 4 + j] = s * x[0 * 4 + j] + c * x[1 * 4 + j];
        y[2 * 4 + j] = x[2 * 4 + j];
    }
    double r[9];
    double resid = -1.0;
    int rc = hartonomous_procrustes_f64(3, 4, x.data(), y.data(), r, &resid);
    ASSERT_EQ(rc, 0);
    EXPECT_NEAR(det3(r), 1.0, 1e-10);
    EXPECT_NEAR(resid, 0.0, 1e-10);

    // Verify R matches the applied rotation element-wise.
    EXPECT_NEAR(r[0],  c, 1e-10);
    EXPECT_NEAR(r[1], -s, 1e-10);
    EXPECT_NEAR(r[2],  0.0, 1e-10);
    EXPECT_NEAR(r[3],  s, 1e-10);
    EXPECT_NEAR(r[4],  c, 1e-10);
    EXPECT_NEAR(r[5],  0.0, 1e-10);
    EXPECT_NEAR(r[8],  1.0, 1e-10);
}

TEST(Procrustes, CorrectsReflectionToProperRotation) {
    // Y is X with last coordinate reflected. The "best" orthogonal map is a
    // reflection (det = -1), but Kabsch must return a proper rotation.
    std::vector<double> x = {1, 2, 3, 0, 1, 2, 4, 5, 6};  // 3×3
    std::vector<double> y = x;
    for (int j = 0; j < 3; ++j) {
        y[2 * 3 + j] = -x[2 * 3 + j];
    }
    double r[9];
    int rc = hartonomous_procrustes_f64(3, 3, x.data(), y.data(), r, nullptr);
    ASSERT_EQ(rc, 0);
    EXPECT_NEAR(det3(r), 1.0, 1e-8);
}

TEST(Procrustes, ResidualMatchesManualComputation) {
    // Two arbitrary clouds; Procrustes returns some R and its residual.
    // We recompute ||R·X - Y||_F ourselves and compare.
    std::vector<double> x = {1.1, 2.2, 3.3, 4.4, 0.5, -0.5, 1.5, -1.5};  // 2×4
    std::vector<double> y = {2.0, 1.0, 4.0, 5.0, -0.2, 0.7, 0.9, -1.1};  // 2×4
    double r[4];
    double resid = -1.0;
    int rc = hartonomous_procrustes_f64(2, 4, x.data(), y.data(), r, &resid);
    ASSERT_EQ(rc, 0);

    double rx[8];
    apply_rotation(2, 4, r, x.data(), rx);
    double expect_sq = 0.0;
    for (int i = 0; i < 8; ++i) {
        double dd = rx[i] - y[i];
        expect_sq += dd * dd;
    }
    EXPECT_NEAR(resid, std::sqrt(expect_sq), 1e-10);
}

TEST(Procrustes, DeterministicAcrossRuns) {
    std::vector<double> x = {1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 14};  // 3×4
    std::vector<double> y = {2, 1, 4, 3, 6, 5, 8, 7, 11, 10, 13, 12};  // 3×4
    double r1[9], r2[9];
    double resid1 = 0.0, resid2 = 0.0;
    ASSERT_EQ(hartonomous_procrustes_f64(3, 4, x.data(), y.data(), r1, &resid1), 0);
    ASSERT_EQ(hartonomous_procrustes_f64(3, 4, x.data(), y.data(), r2, &resid2), 0);
    for (int i = 0; i < 9; ++i) EXPECT_EQ(r1[i], r2[i]);
    EXPECT_EQ(resid1, resid2);
}

TEST(Procrustes, RejectsNullArgs) {
    std::vector<double> x(6), y(6);
    double r[4];
    EXPECT_EQ(hartonomous_procrustes_f64(2, 3, nullptr, y.data(), r, nullptr), -1);
    EXPECT_EQ(hartonomous_procrustes_f64(2, 3, x.data(), nullptr, r, nullptr), -1);
    EXPECT_EQ(hartonomous_procrustes_f64(2, 3, x.data(), y.data(), nullptr, nullptr), -1);
}

TEST(Procrustes, RejectsBadShape) {
    std::vector<double> x(6), y(6);
    double r[4];
    EXPECT_EQ(hartonomous_procrustes_f64(0, 3, x.data(), y.data(), r, nullptr), -2);
    EXPECT_EQ(hartonomous_procrustes_f64(2, 0, x.data(), y.data(), r, nullptr), -2);
    EXPECT_EQ(hartonomous_procrustes_f64(-1, 3, x.data(), y.data(), r, nullptr), -2);
}
