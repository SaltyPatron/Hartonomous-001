include(FetchContent)
FetchContent_Declare(
    googletest
    GIT_REPOSITORY https://github.com/google/googletest.git
    GIT_TAG        v1.17.0
)
set(gtest_force_shared_crt ON CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(googletest)

add_executable(hartonomous_tests
    test_blake3.cc
    test_s3.cc
    test_karcher_s3.cc
    test_super_fibonacci.cc
    test_hilbert.cc
    test_gemm.cc
    test_knn.cc
    test_knearest_exact.cc
    test_svd.cc
    test_procrustes.cc
    test_sparse_eigs.cc
    test_laplacian_eigenmap.cc
    test_kmeans_plusplus.cc
    test_delaunay_4d.cc
    test_tensor_decode.cc
    test_gram_schmidt.cc
    test_merkle.cc
    test_distance_4d.cc
    test_centroid_4d.cc
    test_box4d.cc
    test_trajectory_4d.cc
    test_glicko_bulk.cc
    test_ucd_tables.cc
)

target_link_libraries(hartonomous_tests
    PRIVATE
        hartonomous
        GTest::gtest_main
)

include(GoogleTest)
gtest_discover_tests(hartonomous_tests)
