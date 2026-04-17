#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <random>
#include <vector>

#include <gtest/gtest.h>
#include "hartonomous.h"

namespace {

// Build a symmetric CSR matrix from dense upper triangle (MKL expects symmetric
// fill mode = upper, so only upper-triangular entries + diagonal are stored).
void BuildCsrFromDense(
    const std::vector<std::vector<double>>& A,
    std::vector<int64_t>& row_ptr,
    std::vector<int64_t>& col_idx,
    std::vector<double>& vals
) {
    const int64_t n = static_cast<int64_t>(A.size());
    row_ptr.assign(n + 1, 0);
    col_idx.clear();
    vals.clear();
    for (int64_t i = 0; i < n; ++i) {
        for (int64_t j = i; j < n; ++j) {
            if (A[i][j] != 0.0) {
                col_idx.push_back(j);
                vals.push_back(A[i][j]);
            }
        }
        row_ptr[i + 1] = static_cast<int64_t>(col_idx.size());
    }
}

}  // namespace

TEST(SparseSymEigs, RejectsBadArgs) {
    int64_t rp = 0, ci = 0;
    double v = 0.0, ev = 0.0, evec = 0.0;
    int64_t iters = 0;
    EXPECT_EQ(-2, hartonomous_sparse_sym_eigs_f64(0, 0, &rp, &ci, &v, 1, 16, 1, &ev, &evec, &iters));
    EXPECT_EQ(-2, hartonomous_sparse_sym_eigs_f64(4, 0, &rp, &ci, &v, 4, 16, 1, &ev, &evec, &iters));
    EXPECT_EQ(-2, hartonomous_sparse_sym_eigs_f64(4, 0, &rp, &ci, &v, 1, 2, 1, &ev, &evec, &iters));
    EXPECT_EQ(-1, hartonomous_sparse_sym_eigs_f64(4, 0, nullptr, &ci, &v, 1, 16, 1, &ev, &evec, &iters));
}

TEST(SparseSymEigs, DiagonalMatrix) {
    // diag(5, 3, 1) — eigenvalues are the diagonal entries. Top-2 descending
    // should be {5, 3}.
    std::vector<std::vector<double>> A = {
        {5, 0, 0},
        {0, 3, 0},
        {0, 0, 1},
    };
    std::vector<int64_t> rp, ci;
    std::vector<double> vals;
    BuildCsrFromDense(A, rp, ci, vals);

    const int64_t n = 3;
    const int64_t k = 2;
    std::vector<double> eigvals(k);
    std::vector<double> eigvecs(n * k);
    int64_t iters = 0;
    int rc = hartonomous_sparse_sym_eigs_f64(
        n, static_cast<int64_t>(vals.size()), rp.data(), ci.data(), vals.data(),
        k, /*max_iter*/ 12, /*seed*/ 42,
        eigvals.data(), eigvecs.data(), &iters);
    ASSERT_EQ(rc, 0);
    EXPECT_NEAR(eigvals[0], 5.0, 1e-8);
    EXPECT_NEAR(eigvals[1], 3.0, 1e-8);
}

TEST(SparseSymEigs, KnownSpectrum5x5Tridiag) {
    // Symmetric tridiagonal with 2 on diagonal, -1 on off-diagonal — 1D path
    // Laplacian. Eigenvalues λ_k = 2 - 2·cos(kπ / (n+1)), k = 1..n.
    const int64_t n = 5;
    std::vector<std::vector<double>> A(n, std::vector<double>(n, 0.0));
    for (int64_t i = 0; i < n; ++i) {
        A[i][i] = 2.0;
        if (i + 1 < n) {
            A[i][i + 1] = -1.0;
            A[i + 1][i] = -1.0;
        }
    }
    std::vector<int64_t> rp, ci;
    std::vector<double> vals;
    BuildCsrFromDense(A, rp, ci, vals);

    const int64_t k = 2;
    std::vector<double> eigvals(k);
    std::vector<double> eigvecs(n * k);
    int64_t iters = 0;
    int rc = hartonomous_sparse_sym_eigs_f64(
        n, static_cast<int64_t>(vals.size()), rp.data(), ci.data(), vals.data(),
        k, /*max_iter*/ 20, /*seed*/ 7,
        eigvals.data(), eigvecs.data(), &iters);
    ASSERT_EQ(rc, 0);
    // Top-2 algebraic eigenvalues descending.
    const double pi = 3.141592653589793;
    double top = 2.0 - 2.0 * std::cos(5 * pi / 6.0);
    double snd = 2.0 - 2.0 * std::cos(4 * pi / 6.0);
    EXPECT_NEAR(eigvals[0], top, 1e-6);
    EXPECT_NEAR(eigvals[1], snd, 1e-6);
}

// Production chain at MiniLM scale. Reproduces exactly the sequence the
// EmbeddingFireflyPass runs: KnnCosineGraph → normalized-Laplacian → top-k
// Ritz pairs via Lanczos. If the crash is in MKL sparse matvec at scale or
// in Lanczos workspace bounds, it must reproduce here.
// Gated behind HARTNS_SLOW_TESTS (multi-minute runtime).
TEST(SparseSymEigs, FullMiniLmChainCrashRepro) {
    if (std::getenv("HARTNS_SLOW_TESTS") == nullptr) {
        GTEST_SKIP() << "Set HARTNS_SLOW_TESTS=1 to run MiniLM chain";
    }
    std::mt19937_64 rng(0xABADCAFEULL);
    std::uniform_real_distribution<double> uni(-0.1, 0.1);
    const int64_t n = 30522, d = 384, k = 32;
    std::vector<double> rows(n * d);
    for (auto& x : rows) x = uni(rng);
    // Row-L2 normalize.
    for (int64_t i = 0; i < n; ++i) {
        double sq = 0.0;
        for (int64_t j = 0; j < d; ++j) sq += rows[i * d + j] * rows[i * d + j];
        double inv = sq > 0.0 ? 1.0 / std::sqrt(sq) : 0.0;
        for (int64_t j = 0; j < d; ++j) rows[i * d + j] *= inv;
    }

    std::vector<int64_t> rp(n + 1);
    std::vector<int64_t> ci(2 * n * k);
    std::vector<double> vals(2 * n * k);
    int64_t nnz = 0;
    ASSERT_EQ(0, hartonomous_knn_cosine_graph_f64(
        n, d, rows.data(), k, rp.data(), ci.data(), vals.data(), &nnz));

    // Build D^(-1/2) W D^(-1/2) — normalized similarity — in upper-triangular
    // CSR form (MKL symmetric mode = upper).
    std::vector<double> deg(n, 0.0);
    for (int64_t i = 0; i < n; ++i) {
        for (int64_t p = rp[i]; p < rp[i + 1]; ++p) deg[i] += vals[p];
    }
    std::vector<int64_t> urp(n + 1, 0);
    std::vector<int64_t> uci;
    std::vector<double> uvals;
    uci.reserve(static_cast<size_t>(nnz));
    uvals.reserve(static_cast<size_t>(nnz));
    for (int64_t i = 0; i < n; ++i) {
        double di = deg[i] > 0.0 ? 1.0 / std::sqrt(deg[i]) : 0.0;
        for (int64_t p = rp[i]; p < rp[i + 1]; ++p) {
            int64_t j = ci[p];
            if (j < i) continue;  // upper only
            double dj = deg[j] > 0.0 ? 1.0 / std::sqrt(deg[j]) : 0.0;
            uci.push_back(j);
            uvals.push_back(di * vals[p] * dj);
        }
        urp[i + 1] = static_cast<int64_t>(uci.size());
    }

    const int64_t top = 4;
    std::vector<double> eigvals(top);
    std::vector<double> eigvecs(n * top);
    int64_t iters = 0;
    int rc = hartonomous_sparse_sym_eigs_f64(
        n, static_cast<int64_t>(uvals.size()),
        urp.data(), uci.data(), uvals.data(),
        top, /*max_iter*/ 64, /*seed*/ 42ULL,
        eigvals.data(), eigvecs.data(), &iters);
    ASSERT_EQ(rc, 0);
    EXPECT_GT(iters, top);
}

TEST(SparseSymEigs, Determinism) {
    // Same input + same seed must produce bit-identical eigenvalues.
    const int64_t n = 64;
    std::mt19937_64 rng(0xABCDEF01ULL);
    std::uniform_real_distribution<double> uni(-0.5, 0.5);
    std::vector<std::vector<double>> A(n, std::vector<double>(n, 0.0));
    for (int64_t i = 0; i < n; ++i) {
        A[i][i] = 2.0 + uni(rng);
        // sparse off-diag — 3 per row
        for (int t = 0; t < 3; ++t) {
            int64_t j = (i + 1 + (t * 7)) % n;
            double w = uni(rng);
            if (i < j) {
                A[i][j] += w;
                A[j][i] += w;
            }
        }
    }
    std::vector<int64_t> rp, ci;
    std::vector<double> vals;
    BuildCsrFromDense(A, rp, ci, vals);

    const int64_t k = 4;
    std::vector<double> ev1(k), ev2(k);
    std::vector<double> evec1(n * k), evec2(n * k);
    int64_t it1 = 0, it2 = 0;
    ASSERT_EQ(0, hartonomous_sparse_sym_eigs_f64(
        n, static_cast<int64_t>(vals.size()), rp.data(), ci.data(), vals.data(),
        k, 32, 1234567ULL, ev1.data(), evec1.data(), &it1));
    ASSERT_EQ(0, hartonomous_sparse_sym_eigs_f64(
        n, static_cast<int64_t>(vals.size()), rp.data(), ci.data(), vals.data(),
        k, 32, 1234567ULL, ev2.data(), evec2.data(), &it2));
    for (int64_t e = 0; e < k; ++e) {
        ASSERT_EQ(ev1[e], ev2[e]) << "non-det at e=" << e;
    }
}
