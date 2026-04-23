#include <gtest/gtest.h>

#include <cmath>
#include <vector>

extern "C" {
#include "hartonomous.h"
}

namespace {

constexpr double kTol = 1e-10;

TEST(Frechet4d, IdenticalCurves)
{
    std::vector<double> a{
        0, 0, 0, 0,
        1, 0, 0, 0,
        2, 0, 0, 0
    };
    std::vector<double> ws(3 * 3);
    EXPECT_NEAR(hartonomous_frechet_4d(a.data(), 3, a.data(), 3, ws.data()), 0.0, kTol);
}

TEST(Frechet4d, ParallelOffsetCurves)
{
    /* Two parallel lines offset by 1 unit on the y axis: Fréchet distance = 1. */
    std::vector<double> a{
        0, 0, 0, 0,
        1, 0, 0, 0,
        2, 0, 0, 0
    };
    std::vector<double> b{
        0, 1, 0, 0,
        1, 1, 0, 0,
        2, 1, 0, 0
    };
    std::vector<double> ws(3 * 3);
    EXPECT_NEAR(hartonomous_frechet_4d(a.data(), 3, b.data(), 3, ws.data()), 1.0, kTol);
}

TEST(Frechet4d, SinglePoints)
{
    std::vector<double> a{1, 2, 3, 4};
    std::vector<double> b{4, 6, 6, 8};  /* delta=(3,4,3,4) → norm = sqrt(9+16+9+16)=sqrt(50) */
    std::vector<double> ws(1);
    EXPECT_NEAR(hartonomous_frechet_4d(a.data(), 1, b.data(), 1, ws.data()),
                std::sqrt(50.0), kTol);
}

TEST(Frechet4d, NullArgsReturnNaN)
{
    std::vector<double> a{0, 0, 0, 0};
    std::vector<double> ws(1);
    EXPECT_TRUE(std::isnan(hartonomous_frechet_4d(nullptr, 1, a.data(), 1, ws.data())));
    EXPECT_TRUE(std::isnan(hartonomous_frechet_4d(a.data(), 1, nullptr, 1, ws.data())));
    EXPECT_TRUE(std::isnan(hartonomous_frechet_4d(a.data(), 0, a.data(), 1, ws.data())));
}

TEST(Hausdorff4d, IdenticalCurves)
{
    std::vector<double> a{
        0, 0, 0, 0,
        1, 0, 0, 0,
        2, 0, 0, 0
    };
    EXPECT_NEAR(hartonomous_hausdorff_4d(a.data(), 3, a.data(), 3), 0.0, kTol);
}

TEST(Hausdorff4d, ContainedSubset)
{
    /* b is a subset of a; directed(a→b) max = distance from a's furthest
     * point to nearest in b. */
    std::vector<double> a{
        0, 0, 0, 0,
        1, 0, 0, 0,
        5, 0, 0, 0
    };
    std::vector<double> b{
        0, 0, 0, 0,
        1, 0, 0, 0
    };
    /* directed(a→b): 5 → nearest is 1, distance 4.
     * directed(b→a): every point in b is in a, distance 0.
     * Hausdorff = max(4, 0) = 4. */
    EXPECT_NEAR(hartonomous_hausdorff_4d(a.data(), 3, b.data(), 2), 4.0, kTol);
}

TEST(Hausdorff4d, Symmetric)
{
    std::vector<double> a{0, 0, 0, 0,  1, 0, 0, 0};
    std::vector<double> b{2, 0, 0, 0,  3, 0, 0, 0};
    double h1 = hartonomous_hausdorff_4d(a.data(), 2, b.data(), 2);
    double h2 = hartonomous_hausdorff_4d(b.data(), 2, a.data(), 2);
    EXPECT_NEAR(h1, h2, kTol);
}

}  // namespace
