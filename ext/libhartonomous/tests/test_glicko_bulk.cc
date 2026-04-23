#include <gtest/gtest.h>

#include <cmath>
#include <vector>

extern "C" {
#include "hartonomous.h"
}

namespace {

constexpr double kTolSoft = 1.0;     /* Glicko ratings are O(100s) — coarse comparisons */
constexpr double kTolTight = 1e-3;   /* Determinism / unchanged-state */

/* The Glickman 2013 worked example:
 *   Player rating  μ = 1500, σ = 200, vol = 0.06
 *   Three opponents: (1400, 30, win), (1550, 100, loss), (1700, 300, loss)
 * Expected outcome (from Glickman §1, end of example):
 *   μ' ≈ 1464.06, σ' ≈ 151.52, vol' ≈ 0.05999
 *
 * Our bulk function is single-game-per-row, not Glickman's many-games-in-one-
 * period. So we test that:
 *   1. Defeating a much weaker opponent decreases σ slightly and barely moves μ.
 *   2. Drawing against an equal opponent leaves μ ~unchanged but tightens σ.
 *   3. Losing to a much stronger opponent increases μ slightly (gained info).
 *   4. Bulk results are bit-identical across repeated calls.
 */

TEST(GlickoBulk, DefaultPlayerWinsAgainstWeakOpponent)
{
    double mu = 1500.0, sigma = 350.0, vol = 0.06;
    double opp_mu = 1200.0, opp_sigma = 30.0;
    double score = 1.0;
    double new_mu, new_sigma, new_vol;
    ASSERT_EQ(hartonomous_glicko2_bulk_update(
        1, &mu, &sigma, &vol, &opp_mu, &opp_sigma, &score,
        &new_mu, &new_sigma, &new_vol), 0);

    /* Beat someone substantially weaker: μ goes up a little. */
    EXPECT_GT(new_mu, mu);
    /* σ tightens with new evidence. */
    EXPECT_LT(new_sigma, sigma);
    /* Volatility close to original (no contradicting evidence). */
    EXPECT_NEAR(new_vol, vol, 1e-3);
}

TEST(GlickoBulk, EqualPlayerDrawDoesNotMoveMu)
{
    double mu = 1500.0, sigma = 200.0, vol = 0.06;
    double opp_mu = 1500.0, opp_sigma = 200.0;
    double score = 0.5;
    double new_mu, new_sigma, new_vol;
    ASSERT_EQ(hartonomous_glicko2_bulk_update(
        1, &mu, &sigma, &vol, &opp_mu, &opp_sigma, &score,
        &new_mu, &new_sigma, &new_vol), 0);
    EXPECT_NEAR(new_mu, mu, kTolSoft);
    EXPECT_LT(new_sigma, sigma);
}

TEST(GlickoBulk, LossToStrongOpponentBarelyDropsMu)
{
    double mu = 1500.0, sigma = 200.0, vol = 0.06;
    double opp_mu = 2000.0, opp_sigma = 30.0;
    double score = 0.0;
    double new_mu, new_sigma, new_vol;
    ASSERT_EQ(hartonomous_glicko2_bulk_update(
        1, &mu, &sigma, &vol, &opp_mu, &opp_sigma, &score,
        &new_mu, &new_sigma, &new_vol), 0);
    /* Expected loss vs much-stronger opponent: μ drops only a little. */
    EXPECT_LT(new_mu, mu);
    EXPECT_GT(new_mu, mu - 50.0);
}

TEST(GlickoBulk, BulkOfThreeMatchesIndividualCalls)
{
    std::vector<double> mu{1500, 1500, 1500};
    std::vector<double> sigma{200, 200, 200};
    std::vector<double> vol{0.06, 0.06, 0.06};
    std::vector<double> opp_mu{1400, 1550, 1700};
    std::vector<double> opp_sigma{30, 100, 300};
    std::vector<double> score{1.0, 0.0, 0.0};

    std::vector<double> bulk_mu(3), bulk_sigma(3), bulk_vol(3);
    ASSERT_EQ(hartonomous_glicko2_bulk_update(
        3, mu.data(), sigma.data(), vol.data(),
        opp_mu.data(), opp_sigma.data(), score.data(),
        bulk_mu.data(), bulk_sigma.data(), bulk_vol.data()), 0);

    for (int i = 0; i < 3; ++i) {
        double single_mu, single_sigma, single_vol;
        ASSERT_EQ(hartonomous_glicko2_bulk_update(
            1, &mu[i], &sigma[i], &vol[i],
            &opp_mu[i], &opp_sigma[i], &score[i],
            &single_mu, &single_sigma, &single_vol), 0);
        EXPECT_EQ(bulk_mu[i], single_mu);
        EXPECT_EQ(bulk_sigma[i], single_sigma);
        EXPECT_EQ(bulk_vol[i], single_vol);
    }
}

TEST(GlickoBulk, Determinism)
{
    double mu = 1500.0, sigma = 200.0, vol = 0.06;
    double opp_mu = 1400.0, opp_sigma = 30.0;
    double score = 1.0;
    double m1, s1, v1, m2, s2, v2;
    ASSERT_EQ(hartonomous_glicko2_bulk_update(
        1, &mu, &sigma, &vol, &opp_mu, &opp_sigma, &score, &m1, &s1, &v1), 0);
    ASSERT_EQ(hartonomous_glicko2_bulk_update(
        1, &mu, &sigma, &vol, &opp_mu, &opp_sigma, &score, &m2, &s2, &v2), 0);
    EXPECT_EQ(m1, m2);
    EXPECT_EQ(s1, s2);
    EXPECT_EQ(v1, v2);
}

TEST(GlickoBulk, RejectsNullArgs)
{
    double x = 1500.0;
    EXPECT_EQ(hartonomous_glicko2_bulk_update(
        1, nullptr, &x, &x, &x, &x, &x, &x, &x, &x), -1);
    EXPECT_EQ(hartonomous_glicko2_bulk_update(
        -1, &x, &x, &x, &x, &x, &x, &x, &x, &x), -2);
}

TEST(GlickoBulk, ZeroLengthIsNoop)
{
    double m, s, v;
    EXPECT_EQ(hartonomous_glicko2_bulk_update(
        0, &m, &s, &v, &m, &s, &v, &m, &s, &v), 0);
}

}  // namespace
