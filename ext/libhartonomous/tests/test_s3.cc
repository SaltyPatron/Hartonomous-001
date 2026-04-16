#include <gtest/gtest.h>

#include <array>
#include <cmath>
#include <vector>

extern "C" {
#include "hartonomous.h"
}

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kTol = 1e-10;

TEST(S3Distance, SelfIsZero)
{
    std::array<double, 4> p{1.0, 0.0, 0.0, 0.0};
    EXPECT_NEAR(hartonomous_s3_distance(p.data(), p.data()), 0.0, kTol);
}

TEST(S3Distance, Symmetric)
{
    std::array<double, 4> a{0.5, 0.5, 0.5, 0.5};
    std::array<double, 4> b{1.0, 0.0, 0.0, 0.0};
    double d1 = hartonomous_s3_distance(a.data(), b.data());
    double d2 = hartonomous_s3_distance(b.data(), a.data());
    EXPECT_NEAR(d1, d2, kTol);
}

TEST(S3Distance, AntipodalIsPi)
{
    std::array<double, 4> p{1.0, 0.0, 0.0, 0.0};
    std::array<double, 4> q{-1.0, 0.0, 0.0, 0.0};
    EXPECT_NEAR(hartonomous_s3_distance(p.data(), q.data()), kPi, kTol);
}

TEST(S3Distance, OrthogonalIsHalfPi)
{
    std::array<double, 4> p{1.0, 0.0, 0.0, 0.0};
    std::array<double, 4> q{0.0, 1.0, 0.0, 0.0};
    EXPECT_NEAR(hartonomous_s3_distance(p.data(), q.data()), kPi / 2.0, kTol);
}

TEST(S3Centroid, SinglePointReturnsItself)
{
    std::array<double, 4> p{0.5, 0.5, 0.5, 0.5};
    std::array<double, 4> out{};
    ASSERT_EQ(hartonomous_s3_centroid(p.data(), 1, out.data()), 0);
    for (int i = 0; i < 4; ++i) EXPECT_NEAR(out[i], p[i], kTol);
}

TEST(S3Centroid, TwoEqualPointsReturnThat)
{
    std::array<double, 8> pts{0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5};
    std::array<double, 4> out{};
    ASSERT_EQ(hartonomous_s3_centroid(pts.data(), 2, out.data()), 0);
    for (int i = 0; i < 4; ++i) EXPECT_NEAR(out[i], 0.5, kTol);
}

TEST(S3Centroid, AntipodesReturnDegenerateError)
{
    std::array<double, 8> pts{1.0, 0.0, 0.0, 0.0, -1.0, 0.0, 0.0, 0.0};
    std::array<double, 4> out{};
    EXPECT_EQ(hartonomous_s3_centroid(pts.data(), 2, out.data()), -2);
}

TEST(S3Centroid, RejectsNullAndZeroCount)
{
    std::array<double, 4> out{};
    std::array<double, 4> p{1.0, 0.0, 0.0, 0.0};
    EXPECT_EQ(hartonomous_s3_centroid(nullptr, 1, out.data()), -1);
    EXPECT_EQ(hartonomous_s3_centroid(p.data(), 1, nullptr), -1);
    EXPECT_EQ(hartonomous_s3_centroid(p.data(), 0, out.data()), -1);
}

TEST(S3Centroid, OutputOnUnitSphere)
{
    std::array<double, 12> pts{
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
        0.0, 0.0, 1.0, 0.0,
    };
    std::array<double, 4> out{};
    ASSERT_EQ(hartonomous_s3_centroid(pts.data(), 3, out.data()), 0);
    double norm_sq = 0.0;
    for (int i = 0; i < 4; ++i) norm_sq += out[i] * out[i];
    EXPECT_NEAR(norm_sq, 1.0, 1e-12);
}

}  // namespace
