#include <gtest/gtest.h>

#include <array>
#include <vector>

extern "C" {
#include "hartonomous.h"
}

namespace {

constexpr double kTol = 1e-12;

TEST(Centroid4d, SinglePointReturnsItself)
{
    std::array<double, 4> p{1.0, 2.0, 3.0, 4.0};
    std::array<double, 4> out{};
    ASSERT_EQ(hartonomous_centroid_4d(p.data(), 1, out.data()), 0);
    for (int i = 0; i < 4; ++i) EXPECT_NEAR(out[i], p[i], kTol);
}

TEST(Centroid4d, MeanOfFourCorners)
{
    /* Tetrahedron-like points; mean is (0.25, 0.25, 0.25, 0.25). */
    std::vector<double> pts{
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
        0.0, 0.0, 1.0, 0.0,
        0.0, 0.0, 0.0, 1.0
    };
    std::array<double, 4> out{};
    ASSERT_EQ(hartonomous_centroid_4d(pts.data(), 4, out.data()), 0);
    EXPECT_NEAR(out[0], 0.25, kTol);
    EXPECT_NEAR(out[1], 0.25, kTol);
    EXPECT_NEAR(out[2], 0.25, kTol);
    EXPECT_NEAR(out[3], 0.25, kTol);
}

TEST(Centroid4d, MeanOfAntipodes)
{
    /* Antipodal pair averages to origin (no renormalization, unlike s3). */
    std::vector<double> pts{
        1.0, 0.0, 0.0, 0.0,
        -1.0, 0.0, 0.0, 0.0
    };
    std::array<double, 4> out{};
    ASSERT_EQ(hartonomous_centroid_4d(pts.data(), 2, out.data()), 0);
    EXPECT_NEAR(out[0], 0.0, kTol);
    EXPECT_NEAR(out[1], 0.0, kTol);
    EXPECT_NEAR(out[2], 0.0, kTol);
    EXPECT_NEAR(out[3], 0.0, kTol);
}

TEST(Centroid4d, RejectsNullAndZeroCount)
{
    std::array<double, 4> p{1, 0, 0, 0};
    std::array<double, 4> out{};
    EXPECT_EQ(hartonomous_centroid_4d(nullptr, 1, out.data()), -1);
    EXPECT_EQ(hartonomous_centroid_4d(p.data(), 1, nullptr), -1);
    EXPECT_EQ(hartonomous_centroid_4d(p.data(), 0, out.data()), -1);
}

TEST(Centroid4d, Determinism)
{
    std::vector<double> pts{
        0.123, 0.456, 0.789, 0.234,
        -0.111, 0.222, -0.333, 0.444,
        0.5, -0.5, 0.5, -0.5
    };
    std::array<double, 4> out1{}, out2{};
    ASSERT_EQ(hartonomous_centroid_4d(pts.data(), 3, out1.data()), 0);
    ASSERT_EQ(hartonomous_centroid_4d(pts.data(), 3, out2.data()), 0);
    for (int i = 0; i < 4; ++i) EXPECT_EQ(out1[i], out2[i]);
}

}  // namespace
