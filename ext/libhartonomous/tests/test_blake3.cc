#include <array>
#include <cstdint>
#include <cstring>
#include <string>

#include <gtest/gtest.h>
#include "hartonomous.h"

namespace {

std::string Hex(const uint8_t* bytes, size_t len) {
    static const char kDigits[] = "0123456789abcdef";
    std::string s(len * 2, '\0');
    for (size_t i = 0; i < len; ++i) {
        s[i * 2]     = kDigits[bytes[i] >> 4];
        s[i * 2 + 1] = kDigits[bytes[i] & 0x0f];
    }
    return s;
}

}  // namespace

// Official BLAKE3 test vectors (first 32 bytes of the digest).
// Source: https://github.com/BLAKE3-team/BLAKE3/blob/master/test_vectors/test_vectors.json

TEST(Blake3, EmptyInput) {
    std::array<uint8_t, HARTONOMOUS_HASH_LEN> out{};
    hartonomous_blake3(nullptr, 0, out.data());
    EXPECT_EQ(Hex(out.data(), out.size()),
              "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262");
}

TEST(Blake3, SingleByte0x00) {
    const uint8_t input[1] = {0x00};
    std::array<uint8_t, HARTONOMOUS_HASH_LEN> out{};
    hartonomous_blake3(input, sizeof(input), out.data());
    EXPECT_EQ(Hex(out.data(), out.size()),
              "2d3adedff11b61f14c886e35afa036736dcd87a74d27b5c1510225d0f592e213");
}

TEST(Blake3, TestVectorLen1024) {
    // Input is the first 1024 bytes of the canonical ramp 0x00..0xff repeating.
    std::array<uint8_t, 1024> input{};
    for (size_t i = 0; i < input.size(); ++i) {
        input[i] = static_cast<uint8_t>(i % 251);
    }
    std::array<uint8_t, HARTONOMOUS_HASH_LEN> out{};
    hartonomous_blake3(input.data(), input.size(), out.data());
    // From test_vectors.json entry input_len=1024.
    EXPECT_EQ(Hex(out.data(), out.size()),
              "42214739f095a406f3fc83deb889744ac00df831c10daa55189b5d121c855af7");
}

TEST(Blake3, IncrementalMatchesOneShot) {
    std::array<uint8_t, 512> input{};
    for (size_t i = 0; i < input.size(); ++i) {
        input[i] = static_cast<uint8_t>(i * 7 + 3);
    }

    std::array<uint8_t, HARTONOMOUS_HASH_LEN> oneshot{};
    hartonomous_blake3(input.data(), input.size(), oneshot.data());

    hartonomous_blake3_state st{};
    hartonomous_blake3_init(&st);
    hartonomous_blake3_update(&st, input.data(), 100);
    hartonomous_blake3_update(&st, input.data() + 100, 300);
    hartonomous_blake3_update(&st, input.data() + 400, 112);
    std::array<uint8_t, HARTONOMOUS_HASH_LEN> incremental{};
    hartonomous_blake3_finalize(&st, incremental.data());

    EXPECT_EQ(Hex(oneshot.data(), oneshot.size()),
              Hex(incremental.data(), incremental.size()));
}

TEST(Version, ReturnsSemVerString) {
    const char* v = hartonomous_version();
    ASSERT_NE(v, nullptr);
    EXPECT_STRNE(v, "");
}
