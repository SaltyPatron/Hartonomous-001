#include <gtest/gtest.h>

#include <array>
#include <cmath>
#include <vector>

extern "C" {
#include "hartonomous.h"
}

namespace {

constexpr double kPi  = 3.14159265358979323846;
constexpr double kTol = 1e-10;

/* Helper: build a unit 4-vector from (θ, φ) on the 1st-2nd component great
 * circle of S³, in the (e_0, e_1) plane. Keeps w = z = 0 so we can compare
 * Karcher mean against the known-answer mid-angle. */
static std::array<double, 4> circle_s3(double theta)
{
    return {std::cos(theta), std::sin(theta), 0.0, 0.0};
}

TEST(KarcherS3, SinglePointReturnsItself)
{
    std::array<double, 4> p{0.5, 0.5, 0.5, 0.5};
    std::array<double, 4> out{};
    ASSERT_EQ(hartonomous_karcher_mean_s3(p.data(), 1, 0, 0.0, out.data()), 0);
    for (int i = 0; i < 4; ++i) EXPECT_NEAR(out[i], p[i], kTol);
}

TEST(KarcherS3, TwoEqualPointsReturnThat)
{
    std::array<double, 8> pts{0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5};
    std::array<double, 4> out{};
    ASSERT_EQ(hartonomous_karcher_mean_s3(pts.data(), 2, 0, 0.0, out.data()), 0);
    for (int i = 0; i < 4; ++i) EXPECT_NEAR(out[i], 0.5, kTol);
}

TEST(KarcherS3, SymmetricPairMeanIsMidAngle)
{
    // Two points at angles ±θ on the (e_0, e_1) great circle.
    // Karcher (and chordal) mean must be at angle 0: (1, 0, 0, 0).
    const double theta = 0.7;
    auto a = circle_s3(+theta);
    auto b = circle_s3(-theta);
    std::array<double, 8> pts{
        a[0], a[1], a[2], a[3],
        b[0], b[1], b[2], b[3]
    };
    std::array<double, 4> out{};
    ASSERT_EQ(hartonomous_karcher_mean_s3(pts.data(), 2, 0, 0.0, out.data()), 0);
    EXPECT_NEAR(out[0], 1.0, 1e-10);
    EXPECT_NEAR(out[1], 0.0, 1e-10);
    EXPECT_NEAR(out[2], 0.0, 1e-10);
    EXPECT_NEAR(out[3], 0.0, 1e-10);
}

TEST(KarcherS3, WidelySpreadPairSuperiorToChordal)
{
    // Three points on the great circle at angles 0, +1.1, +1.1 rad.
    // True Fréchet mean is at the *weighted* geodesic midpoint. For the pair
    // (0, 1.1 rad, 1.1 rad), the intrinsic (arc-length) mean is (0 + 1.1 + 1.1)/3
    // = 0.7333... rad. The chordal mean is biased toward the outer mass and
    // lands at a slightly *different* angle. We verify the Karcher answer
    // equals the true intrinsic mean within 1e-10.
    auto p0 = circle_s3(0.0);
    auto p1 = circle_s3(1.1);
    auto p2 = circle_s3(1.1);
    std::array<double, 12> pts{
        p0[0], p0[1], p0[2], p0[3],
        p1[0], p1[1], p1[2], p1[3],
        p2[0], p2[1], p2[2], p2[3]
    };
    std::array<double, 4> out{};
    ASSERT_EQ(hartonomous_karcher_mean_s3(pts.data(), 3, 0, 0.0, out.data()), 0);

    const double expected = (0.0 + 1.1 + 1.1) / 3.0;  // 0.73333...
    auto mu = circle_s3(expected);
    EXPECT_NEAR(out[0], mu[0], 1e-10);
    EXPECT_NEAR(out[1], mu[1], 1e-10);
    EXPECT_NEAR(out[2], mu[2], 1e-10);
    EXPECT_NEAR(out[3], mu[3], 1e-10);
}

TEST(KarcherS3, OutputOnUnitSphere)
{
    // 4 widely-spread points; result must be S³-unit within rounding.
    std::array<double, 16> pts{
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
        0.0, 0.0, 1.0, 0.0,
        0.5, 0.5, 0.5, 0.5
    };
    std::array<double, 4> out{};
    ASSERT_EQ(hartonomous_karcher_mean_s3(pts.data(), 4, 0, 0.0, out.data()), 0);
    double norm = 0.0;
    for (int i = 0; i < 4; ++i) norm += out[i] * out[i];
    EXPECT_NEAR(std::sqrt(norm), 1.0, 1e-12);
}

TEST(KarcherS3, RejectsNullAndZeroCount)
{
    std::array<double, 4> out{};
    std::array<double, 4> p{1.0, 0.0, 0.0, 0.0};
    EXPECT_EQ(hartonomous_karcher_mean_s3(nullptr, 1, 0, 0.0, out.data()), -1);
    EXPECT_EQ(hartonomous_karcher_mean_s3(p.data(),  1, 0, 0.0, nullptr),    -1);
    EXPECT_EQ(hartonomous_karcher_mean_s3(p.data(),  0, 0, 0.0, out.data()), -1);
}

TEST(KarcherS3, AntipodalSeedCancellationReportsError)
{
    // Exactly antipodal pair: chordal seed has zero magnitude, returns -2
    // and Karcher propagates it (the Fréchet mean is not unique).
    std::array<double, 8> pts{1.0, 0.0, 0.0, 0.0, -1.0, 0.0, 0.0, 0.0};
    std::array<double, 4> out{};
    EXPECT_EQ(hartonomous_karcher_mean_s3(pts.data(), 2, 0, 0.0, out.data()), -2);
}

TEST(KarcherS3, DeterministicAcrossRuns)
{
    // Same inputs → bit-identical outputs (Law #6).
    std::array<double, 12> pts{
        1.0, 0.0, 0.0, 0.0,
        0.5, 0.5, 0.5, 0.5,
        0.0, 1.0, 0.0, 0.0
    };
    std::array<double, 4> a{}, b{};
    ASSERT_EQ(hartonomous_karcher_mean_s3(pts.data(), 3, 0, 0.0, a.data()), 0);
    ASSERT_EQ(hartonomous_karcher_mean_s3(pts.data(), 3, 0, 0.0, b.data()), 0);
    for (int i = 0; i < 4; ++i) EXPECT_EQ(a[i], b[i]);
}

TEST(KarcherS3, CustomToleranceHonored)
{
    // Loose tolerance should still return a unit vector; tight tolerance
    // should produce a result within the tight bound of the intrinsic mean.
    auto p0 = circle_s3(0.0);
    auto p1 = circle_s3(0.4);
    std::array<double, 8> pts{
        p0[0], p0[1], p0[2], p0[3],
        p1[0], p1[1], p1[2], p1[3]
    };
    std::array<double, 4> tight{}, loose{};
    ASSERT_EQ(hartonomous_karcher_mean_s3(pts.data(), 2, 128, 1e-14, tight.data()), 0);
    ASSERT_EQ(hartonomous_karcher_mean_s3(pts.data(), 2,   1, 1e-2,  loose.data()), 0);

    auto mu = circle_s3(0.2);
    EXPECT_NEAR(tight[0], mu[0], 1e-13);
    EXPECT_NEAR(tight[1], mu[1], 1e-13);
    // loose result is still S³-unit.
    double nrm = 0.0;
    for (int i = 0; i < 4; ++i) nrm += loose[i] * loose[i];
    EXPECT_NEAR(std::sqrt(nrm), 1.0, 1e-10);
}

}  // namespace
