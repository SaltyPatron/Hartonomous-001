#include <array>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

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

TEST(Merkle, EmptyChildren) {
    // Hash of the empty byte string via BLAKE3.
    std::array<uint8_t, HARTONOMOUS_HASH_LEN> merkle{};
    std::array<uint8_t, HARTONOMOUS_HASH_LEN> direct{};
    ASSERT_EQ(0, hartonomous_blake3_merkle(nullptr, 0, merkle.data()));
    hartonomous_blake3(nullptr, 0, direct.data());
    EXPECT_EQ(Hex(merkle.data(), merkle.size()),
              Hex(direct.data(), direct.size()));
}

TEST(Merkle, SingleChildIsHashOfThatChild) {
    // Hash the bytes of a single 32-byte child — equivalent to BLAKE3 of that
    // 32-byte input.
    std::array<uint8_t, HARTONOMOUS_HASH_LEN> child{};
    for (size_t i = 0; i < child.size(); ++i) child[i] = static_cast<uint8_t>(i);

    std::array<uint8_t, HARTONOMOUS_HASH_LEN> merkle{};
    std::array<uint8_t, HARTONOMOUS_HASH_LEN> direct{};
    ASSERT_EQ(0, hartonomous_blake3_merkle(child.data(), 1, merkle.data()));
    hartonomous_blake3(child.data(), child.size(), direct.data());
    EXPECT_EQ(Hex(merkle.data(), merkle.size()),
              Hex(direct.data(), direct.size()));
}

TEST(Merkle, OrderSensitive) {
    // Order of children matters: [A, B] must hash differently from [B, A].
    std::vector<uint8_t> ab(2 * HARTONOMOUS_HASH_LEN);
    std::vector<uint8_t> ba(2 * HARTONOMOUS_HASH_LEN);
    for (size_t i = 0; i < HARTONOMOUS_HASH_LEN; ++i) {
        ab[i] = 0x11;
        ab[HARTONOMOUS_HASH_LEN + i] = 0x22;
        ba[i] = 0x22;
        ba[HARTONOMOUS_HASH_LEN + i] = 0x11;
    }
    std::array<uint8_t, HARTONOMOUS_HASH_LEN> h1{}, h2{};
    ASSERT_EQ(0, hartonomous_blake3_merkle(ab.data(), 2, h1.data()));
    ASSERT_EQ(0, hartonomous_blake3_merkle(ba.data(), 2, h2.data()));
    EXPECT_NE(Hex(h1.data(), h1.size()), Hex(h2.data(), h2.size()));
}

TEST(Merkle, Determinism) {
    // Same input → same output, repeatedly.
    std::vector<uint8_t> kids(4 * HARTONOMOUS_HASH_LEN);
    for (size_t i = 0; i < kids.size(); ++i) kids[i] = static_cast<uint8_t>(i * 7 + 3);
    std::array<uint8_t, HARTONOMOUS_HASH_LEN> a{}, b{};
    ASSERT_EQ(0, hartonomous_blake3_merkle(kids.data(), 4, a.data()));
    ASSERT_EQ(0, hartonomous_blake3_merkle(kids.data(), 4, b.data()));
    EXPECT_EQ(Hex(a.data(), a.size()), Hex(b.data(), b.size()));
}

TEST(Merkle, RejectsNullOutput) {
    EXPECT_EQ(-1, hartonomous_blake3_merkle(nullptr, 0, nullptr));
}
