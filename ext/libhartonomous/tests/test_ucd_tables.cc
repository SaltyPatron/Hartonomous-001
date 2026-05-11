#include <gtest/gtest.h>

#include "hartonomous.h"

TEST(UcdTables, GeneratedTextTablesAreLinked)
{
    EXPECT_EQ(hartonomous_ucd_tables_ready(), 1);
}

TEST(UcdCatalog, EmbeddedFallbackProvidesRepresentativeAtoms)
{
    hartonomous_ucd_unload();
    ASSERT_EQ(hartonomous_ucd_load("__hartonomous_missing_ucd_blob_dir__"), 0);
    ASSERT_EQ(hartonomous_ucd_catalog_ready(), 1);

    const int32_t samples[] = {0x0000, 0x0041, 0x0301, 0x1F600, 0x10FFFF};
    for (int32_t cp : samples) {
        uint8_t hash[HARTONOMOUS_HASH_LEN] = {};
        double centroid[4] = {};
        uint64_t hilbert = 0;

        ASSERT_EQ(hartonomous_ucd_cp_hash(cp, hash), 0) << cp;
        ASSERT_EQ(hartonomous_ucd_cp_centroid(cp, centroid), 0) << cp;
        ASSERT_EQ(hartonomous_ucd_cp_hilbert(cp, &hilbert), 0) << cp;
        EXPECT_EQ(hartonomous_ucd_cp_from_hash(hash), cp) << cp;

        double norm2 = centroid[0] * centroid[0]
            + centroid[1] * centroid[1]
            + centroid[2] * centroid[2]
            + centroid[3] * centroid[3];
        EXPECT_NEAR(norm2, 1.0, 1e-9) << cp;
        (void) hilbert;
    }
}
