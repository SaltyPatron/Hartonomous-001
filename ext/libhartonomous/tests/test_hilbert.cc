#include <gtest/gtest.h>

#include <array>
#include <cmath>
#include <set>

extern "C" {
#include "hartonomous.h"
}

namespace {

TEST(Hilbert, RoundTripLowOrder)
{
    const int order = 4;
    const uint64_t N = (uint64_t)1 << order;
    uint64_t max_val = N - 1;

    for (uint64_t i = 0; i < N; ++i)
    for (uint64_t j = 0; j < N; ++j)
    for (uint64_t k = 0; k < N; ++k)
    for (uint64_t l = 0; l < N; ++l) {
        std::array<double, 4> pt{
            (double)i / max_val,
            (double)j / max_val,
            (double)k / max_val,
            (double)l / max_val};
        uint64_t idx = hartonomous_hilbert_index(pt.data(), order);
        std::array<double, 4> back{};
        ASSERT_EQ(hartonomous_hilbert_inverse(idx, order, back.data()), 0);
        for (int d = 0; d < 4; ++d) {
            EXPECT_NEAR(back[d], pt[d], 1.0 / max_val) << "d=" << d;
        }
    }
}

TEST(Hilbert, IndexInRange)
{
    const int order = 8;
    uint64_t max_idx = ((uint64_t)1 << (order * 4)) - 1;
    std::array<double, 4> pt{0.0, 0.0, 0.0, 0.0};
    EXPECT_LE(hartonomous_hilbert_index(pt.data(), order), max_idx);
    std::array<double, 4> pt2{1.0, 1.0, 1.0, 1.0};
    EXPECT_LE(hartonomous_hilbert_index(pt2.data(), order), max_idx);
}

TEST(Hilbert, InjectiveOnLattice)
{
    const int order = 3;
    const uint64_t N = (uint64_t)1 << order;
    uint64_t max_val = N - 1;
    std::set<uint64_t> seen;
    for (uint64_t i = 0; i < N; ++i)
    for (uint64_t j = 0; j < N; ++j)
    for (uint64_t k = 0; k < N; ++k)
    for (uint64_t l = 0; l < N; ++l) {
        std::array<double, 4> pt{
            (double)i / max_val,
            (double)j / max_val,
            (double)k / max_val,
            (double)l / max_val};
        uint64_t idx = hartonomous_hilbert_index(pt.data(), order);
        ASSERT_TRUE(seen.insert(idx).second) << "duplicate idx=" << idx;
    }
    EXPECT_EQ(seen.size(), (uint64_t)(N * N * N * N));
}

TEST(Hilbert, OriginMapsToZero)
{
    std::array<double, 4> origin{0.0, 0.0, 0.0, 0.0};
    EXPECT_EQ(hartonomous_hilbert_index(origin.data(), 8), 0u);
}

TEST(Hilbert, RejectsBadOrder)
{
    std::array<double, 4> out{};
    EXPECT_EQ(hartonomous_hilbert_inverse(0, 0, out.data()), -2);
    EXPECT_EQ(hartonomous_hilbert_inverse(0, 17, out.data()), -2);
    EXPECT_EQ(hartonomous_hilbert_inverse(0, 8, nullptr), -1);
}

}  // namespace
