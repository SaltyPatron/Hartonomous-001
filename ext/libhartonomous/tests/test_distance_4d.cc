#include <gtest/gtest.h>

#include <array>
#include <cmath>

extern "C" {
#include "hartonomous.h"
}

namespace {

constexpr double kTol = 1e-12;
constexpr double kSlerpTol = 1e-10;

TEST(Distance4d, ZeroForEqualPoints)
{
    std::array<double, 4> p{1.0, 2.0, 3.0, 4.0};
    EXPECT_NEAR(hartonomous_distance_4d(p.data(), p.data()), 0.0, kTol);
}

TEST(Distance4d, AxisAligned)
{
    std::array<double, 4> a{0.0, 0.0, 0.0, 0.0};
    std::array<double, 4> b{3.0, 4.0, 0.0, 0.0};
    EXPECT_NEAR(hartonomous_distance_4d(a.data(), b.data()), 5.0, kTol);
}

TEST(Distance4d, FullDimensional)
{
    std::array<double, 4> a{1.0, 2.0, 3.0, 4.0};
    std::array<double, 4> b{5.0, 6.0, 7.0, 8.0};
    /* Each delta = 4; sum of squares = 4 * 16 = 64; sqrt = 8. */
    EXPECT_NEAR(hartonomous_distance_4d(a.data(), b.data()), 8.0, kTol);
}

TEST(Dot4d, ParallelEqualsProductOfNorms)
{
    std::array<double, 4> p{0.5, 0.5, 0.5, 0.5};
    EXPECT_NEAR(hartonomous_dot_4d(p.data(), p.data()), 1.0, kTol);
}

TEST(Dot4d, OrthogonalIsZero)
{
    std::array<double, 4> a{1.0, 0.0, 0.0, 0.0};
    std::array<double, 4> b{0.0, 1.0, 0.0, 0.0};
    EXPECT_NEAR(hartonomous_dot_4d(a.data(), b.data()), 0.0, kTol);
}

TEST(Norm4d, UnitVector)
{
    std::array<double, 4> p{0.5, 0.5, 0.5, 0.5};
    EXPECT_NEAR(hartonomous_norm_4d(p.data()), 1.0, kTol);
}

TEST(Normalize4d, ProducesUnitVector)
{
    std::array<double, 4> p{2.0, 0.0, 0.0, 0.0};
    std::array<double, 4> out{};
    ASSERT_EQ(hartonomous_normalize_4d(p.data(), out.data()), 0);
    EXPECT_NEAR(out[0], 1.0, kTol);
    EXPECT_NEAR(hartonomous_norm_4d(out.data()), 1.0, kTol);
}

TEST(Normalize4d, RejectsZeroVector)
{
    std::array<double, 4> p{0.0, 0.0, 0.0, 0.0};
    std::array<double, 4> out{};
    EXPECT_EQ(hartonomous_normalize_4d(p.data(), out.data()), -2);
}

TEST(Slerp, EndpointAtTZero)
{
    std::array<double, 4> a{1.0, 0.0, 0.0, 0.0};
    std::array<double, 4> b{0.0, 1.0, 0.0, 0.0};
    std::array<double, 4> out{};
    ASSERT_EQ(hartonomous_slerp(a.data(), b.data(), 0.0, out.data()), 0);
    for (int i = 0; i < 4; ++i) EXPECT_NEAR(out[i], a[i], kSlerpTol);
}

TEST(Slerp, EndpointAtTOne)
{
    std::array<double, 4> a{1.0, 0.0, 0.0, 0.0};
    std::array<double, 4> b{0.0, 1.0, 0.0, 0.0};
    std::array<double, 4> out{};
    ASSERT_EQ(hartonomous_slerp(a.data(), b.data(), 1.0, out.data()), 0);
    for (int i = 0; i < 4; ++i) EXPECT_NEAR(out[i], b[i], kSlerpTol);
}

TEST(Slerp, MidpointStaysOnSphere)
{
    std::array<double, 4> a{1.0, 0.0, 0.0, 0.0};
    std::array<double, 4> b{0.0, 1.0, 0.0, 0.0};
    std::array<double, 4> out{};
    ASSERT_EQ(hartonomous_slerp(a.data(), b.data(), 0.5, out.data()), 0);
    EXPECT_NEAR(hartonomous_norm_4d(out.data()), 1.0, kSlerpTol);
    /* At midpoint of orthogonal pair on S^3, components should be ~ √2/2 each. */
    EXPECT_NEAR(out[0], std::sqrt(0.5), kSlerpTol);
    EXPECT_NEAR(out[1], std::sqrt(0.5), kSlerpTol);
}

TEST(Slerp, RejectsNonUnitInput)
{
    std::array<double, 4> a{2.0, 0.0, 0.0, 0.0};
    std::array<double, 4> b{0.0, 1.0, 0.0, 0.0};
    std::array<double, 4> out{};
    EXPECT_EQ(hartonomous_slerp(a.data(), b.data(), 0.5, out.data()), -2);
}

TEST(Slerp, ShortestArcAcrossAntipodal)
{
    /* a = (1,0,0,0), b ≈ -a + tiny perturbation. Slerp should choose the
     * short arc by negating b internally. Output norm must remain 1. */
    std::array<double, 4> a{1.0, 0.0, 0.0, 0.0};
    std::array<double, 4> b{-0.9998, 0.02, 0.0, 0.0};
    /* normalize b */
    std::array<double, 4> bn{};
    ASSERT_EQ(hartonomous_normalize_4d(b.data(), bn.data()), 0);
    std::array<double, 4> out{};
    ASSERT_EQ(hartonomous_slerp(a.data(), bn.data(), 0.5, out.data()), 0);
    EXPECT_NEAR(hartonomous_norm_4d(out.data()), 1.0, kSlerpTol);
}

TEST(Antipode, NegatesAllAxes)
{
    std::array<double, 4> p{0.1, -0.2, 0.3, -0.4};
    std::array<double, 4> out{};
    ASSERT_EQ(hartonomous_antipode(p.data(), out.data()), 0);
    for (int i = 0; i < 4; ++i) EXPECT_NEAR(out[i], -p[i], kTol);
}

TEST(Antipode, RejectsNull)
{
    std::array<double, 4> p{1.0, 0.0, 0.0, 0.0};
    EXPECT_EQ(hartonomous_antipode(nullptr, p.data()), -1);
    EXPECT_EQ(hartonomous_antipode(p.data(), nullptr), -1);
}

TEST(Determinism, RepeatedDistanceCallsAreBitIdentical)
{
    std::array<double, 4> a{0.123, 0.456, 0.789, 0.234};
    std::array<double, 4> b{-0.111, 0.222, -0.333, 0.444};
    double d1 = hartonomous_distance_4d(a.data(), b.data());
    double d2 = hartonomous_distance_4d(a.data(), b.data());
    /* Bit-exact equality, not just near. */
    EXPECT_EQ(d1, d2);
}

}  // namespace
