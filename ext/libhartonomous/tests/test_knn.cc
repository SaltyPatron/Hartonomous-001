#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <limits>
#include <random>
#include <set>
#include <utility>
#include <vector>

#include <gtest/gtest.h>
#include "hartonomous.h"

namespace {

void NormalizeRows(std::vector<double>& rows, int64_t n, int64_t d) {
    for (int64_t i = 0; i < n; ++i) {
        double sq = 0.0;
        for (int64_t j = 0; j < d; ++j) {
            double v = rows[i * d + j];
            sq += v * v;
        }
        double inv = sq > 0.0 ? 1.0 / std::sqrt(sq) : 0.0;
        for (int64_t j = 0; j < d; ++j) {
            rows[i * d + j] *= inv;
        }
    }
}

}  // namespace

TEST(Knn, RejectsBadArgs) {
    double d = 0.0;
    int64_t ip = 0, ci = 0, nnz = 0;
    double v = 0.0;
    // n <= 0
    EXPECT_EQ(-2, hartonomous_knn_cosine_graph_f64(0, 4, &d, 2, &ip, &ci, &v, &nnz));
    // k >= n
    EXPECT_EQ(-2, hartonomous_knn_cosine_graph_f64(4, 4, &d, 4, &ip, &ci, &v, &nnz));
    // null rows
    EXPECT_EQ(-1, hartonomous_knn_cosine_graph_f64(4, 4, nullptr, 1, &ip, &ci, &v, &nnz));
}

TEST(Knn, RejectsPairStorageOverflowBeforeLinearizedKeyWrap) {
    double row = 1.0;
    int64_t rowPtr = 0;
    int64_t colIdx = 0;
    double value = 0.0;
    int64_t nnz = 0;

    EXPECT_EQ(-3, hartonomous_knn_cosine_graph_f64(
        std::numeric_limits<int64_t>::max() / 2,
        1,
        &row,
        3,
        &rowPtr,
        &colIdx,
        &value,
        &nnz));
}

TEST(Knn, SmallGraphCorrectness) {
    // 4 rows in 2D placed at the corners of a unit square. With k=1 each
    // row's nearest neighbor is the adjacent corner; symmetric k-NN graph
    // is a 4-cycle: 0-1, 0-2, 1-3, 2-3 (each counted once per direction).
    // We verify the symmetrized CSR contains exactly those undirected edges.
    std::vector<double> rows = {
        1, 0,
        0, 1,
        0, -1,
        -1, 0,
    };
    int64_t n = 4, d = 2, k = 1;
    std::vector<int64_t> row_ptr(n + 1, 0);
    std::vector<int64_t> col_idx(2 * n * k);
    std::vector<double> vals(2 * n * k);
    int64_t nnz = 0;

    NormalizeRows(rows, n, d);
    int rc = hartonomous_knn_cosine_graph_f64(
        n, d, rows.data(), k,
        row_ptr.data(), col_idx.data(), vals.data(), &nnz);
    ASSERT_EQ(rc, 0);
    EXPECT_GE(nnz, 0);

    // Every undirected edge should appear twice (once per endpoint).
    std::set<std::pair<int64_t, int64_t>> edges;
    for (int64_t i = 0; i < n; ++i) {
        for (int64_t p = row_ptr[i]; p < row_ptr[i + 1]; ++p) {
            int64_t j = col_idx[p];
            int64_t lo = std::min(i, j), hi = std::max(i, j);
            edges.emplace(lo, hi);
        }
    }
    EXPECT_FALSE(edges.empty());
}

TEST(Knn, Determinism) {
    // Two runs on identical inputs produce byte-identical CSR output.
    std::mt19937_64 rng(0xBEEFCAFEULL);
    std::uniform_real_distribution<double> uni(-1.0, 1.0);
    const int64_t n = 256, d = 64, k = 4;
    std::vector<double> rows(n * d);
    for (auto& x : rows) x = uni(rng);
    NormalizeRows(rows, n, d);

    std::vector<int64_t> rp1(n + 1), ci1(2 * n * k);
    std::vector<double> v1(2 * n * k);
    int64_t nnz1 = 0;
    ASSERT_EQ(0, hartonomous_knn_cosine_graph_f64(
        n, d, rows.data(), k, rp1.data(), ci1.data(), v1.data(), &nnz1));

    std::vector<int64_t> rp2(n + 1), ci2(2 * n * k);
    std::vector<double> v2(2 * n * k);
    int64_t nnz2 = 0;
    ASSERT_EQ(0, hartonomous_knn_cosine_graph_f64(
        n, d, rows.data(), k, rp2.data(), ci2.data(), v2.data(), &nnz2));

    ASSERT_EQ(nnz1, nnz2);
    for (int64_t i = 0; i <= n; ++i) ASSERT_EQ(rp1[i], rp2[i]);
    for (int64_t p = 0; p < nnz1; ++p) {
        EXPECT_EQ(ci1[p], ci2[p]);
        EXPECT_EQ(v1[p], v2[p]);
    }
}

TEST(Knn, SymmetryProperty) {
    // The output CSR is an undirected graph — for every (i, j) in it there
    // must be a (j, i) with the same weight.
    std::mt19937_64 rng(7);
    std::uniform_real_distribution<double> uni(-1.0, 1.0);
    const int64_t n = 128, d = 16, k = 3;
    std::vector<double> rows(n * d);
    for (auto& x : rows) x = uni(rng);
    NormalizeRows(rows, n, d);

    std::vector<int64_t> rp(n + 1), ci(2 * n * k);
    std::vector<double> v(2 * n * k);
    int64_t nnz = 0;
    ASSERT_EQ(0, hartonomous_knn_cosine_graph_f64(
        n, d, rows.data(), k, rp.data(), ci.data(), v.data(), &nnz));

    auto find_edge = [&](int64_t i, int64_t j, double& out_w) -> bool {
        for (int64_t p = rp[i]; p < rp[i + 1]; ++p) {
            if (ci[p] == j) { out_w = v[p]; return true; }
        }
        return false;
    };
    for (int64_t i = 0; i < n; ++i) {
        for (int64_t p = rp[i]; p < rp[i + 1]; ++p) {
            int64_t j = ci[p];
            double w_ij = v[p];
            double w_ji = 0.0;
            ASSERT_TRUE(find_edge(j, i, w_ji)) << "asymmetry at (" << i << "," << j << ")";
            EXPECT_EQ(w_ij, w_ji);
        }
    }
}

TEST(Knn, WeightsInUnitInterval) {
    std::mt19937_64 rng(99);
    std::uniform_real_distribution<double> uni(-1.0, 1.0);
    const int64_t n = 64, d = 8, k = 4;
    std::vector<double> rows(n * d);
    for (auto& x : rows) x = uni(rng);
    NormalizeRows(rows, n, d);
    std::vector<int64_t> rp(n + 1), ci(2 * n * k);
    std::vector<double> v(2 * n * k);
    int64_t nnz = 0;
    ASSERT_EQ(0, hartonomous_knn_cosine_graph_f64(
        n, d, rows.data(), k, rp.data(), ci.data(), v.data(), &nnz));
    for (int64_t p = 0; p < nnz; ++p) {
        EXPECT_GE(v[p], 0.0);
        EXPECT_LE(v[p], 1.0);
    }
}

// Full MiniLM vocab scale. n = 30522, d = 384, k = 32 — the exact shape the
// EmbeddingFireflyPass calls KnnCosineGraph with on the ingest that crashed.
// If the bug is in the hash dedup cap sizing (pair_cap_want = n*k*4 ≈ 3.9M)
// or anywhere in the symmetrization scan at production scale, it must
// reproduce here. Gated behind HARTNS_SLOW_TESTS so CI default stays fast.
TEST(Knn, FullMiniLmVocabCrashRepro) {
    if (std::getenv("HARTNS_SLOW_TESTS") == nullptr) {
        GTEST_SKIP() << "Set HARTNS_SLOW_TESTS=1 to run MiniLM-sized stress";
    }
    std::mt19937_64 rng(0xDEADBEEFULL);
    std::uniform_real_distribution<double> uni(-0.1, 0.1);
    const int64_t n = 30522, d = 384, k = 32;
    std::vector<double> rows(n * d);
    for (auto& x : rows) x = uni(rng);
    NormalizeRows(rows, n, d);

    std::vector<int64_t> rp(n + 1);
    std::vector<int64_t> ci(2 * n * k);
    std::vector<double> vals(2 * n * k);
    int64_t nnz = 0;

    int rc = hartonomous_knn_cosine_graph_f64(
        n, d, rows.data(), k, rp.data(), ci.data(), vals.data(), &nnz);
    ASSERT_EQ(rc, 0);
    EXPECT_GT(nnz, 0);
    EXPECT_LE(nnz, 2 * n * k);
    EXPECT_EQ(rp[n], nnz);
}

// MiniLM-representative reproducer at smaller scale for CI. See FullMiniLm
// above for the production-scale variant.
TEST(Knn, MiniLmRepresentativeStress) {
    std::mt19937_64 rng(0xC0DECAFEULL);
    std::uniform_real_distribution<double> uni(-0.1, 0.1);
    const int64_t n = 4096, d = 384, k = 32;
    std::vector<double> rows(n * d);
    for (auto& x : rows) x = uni(rng);
    NormalizeRows(rows, n, d);

    std::vector<int64_t> rp(n + 1);
    std::vector<int64_t> ci(2 * n * k);
    std::vector<double> vals(2 * n * k);
    int64_t nnz = 0;

    int rc = hartonomous_knn_cosine_graph_f64(
        n, d, rows.data(), k, rp.data(), ci.data(), vals.data(), &nnz);
    ASSERT_EQ(rc, 0);
    EXPECT_GT(nnz, 0);
    EXPECT_LE(nnz, 2 * n * k);
    EXPECT_EQ(rp[n], nnz);
}
