// Exercises the shared library strictly through qe_api.h, the same surface .NET binds to.
#include "qe_api.h"

#include <cmath>
#include <cstddef>
#include <gtest/gtest.h>
#include <string>
#include <thread>
#include <vector>

extern "C" int qe_c_compat_abi_major(void);

namespace {

std::string last_error() {
    std::int32_t required = 0;
    EXPECT_EQ(qe_last_error(nullptr, 0, &required), QE_E_BUFFER_TOO_SMALL);
    std::string buffer(static_cast<std::size_t>(required), '\0');
    EXPECT_EQ(qe_last_error(buffer.data(), required, &required), QE_OK);
    buffer.resize(static_cast<std::size_t>(required - 1));
    return buffer;
}

qe_bs_input input(double s, double k, double r, double q, double vol, double t, std::int32_t type) {
    return qe_bs_input{s, k, r, q, vol, t, type, 0};
}

class EngineTest : public ::testing::Test {
  protected:
    void SetUp() override {
        const qe_engine_config config{static_cast<std::int32_t>(sizeof(qe_engine_config)), 0, 42};
        ASSERT_EQ(qe_engine_create(&config, &engine_), QE_OK) << last_error();
        ASSERT_NE(engine_, nullptr);
    }
    void TearDown() override { EXPECT_EQ(qe_engine_destroy(engine_), QE_OK); }

    qe_engine* engine_{nullptr};
};

TEST(Abi, VersionMatchesHeader) {
    std::int32_t major = -1;
    std::int32_t minor = -1;
    ASSERT_EQ(qe_abi_version(&major, &minor), QE_OK);
    EXPECT_EQ(major, QE_ABI_MAJOR);
    EXPECT_EQ(minor, QE_ABI_MINOR);
    EXPECT_EQ(qe_c_compat_abi_major(), QE_ABI_MAJOR);
}

TEST(Abi, VersionRejectsNullPointers) {
    std::int32_t minor = 0;
    EXPECT_EQ(qe_abi_version(nullptr, &minor), QE_E_INVALID_ARG);
    EXPECT_NE(last_error().find("must not be NULL"), std::string::npos);
}

TEST(Abi, LastErrorBufferProtocol) {
    std::int32_t major = 0;
    std::int32_t minor = 0;
    ASSERT_EQ(qe_abi_version(&major, &minor), QE_OK); // success clears the error
    std::int32_t required = -1;
    EXPECT_EQ(qe_last_error(nullptr, 0, &required), QE_E_BUFFER_TOO_SMALL);
    EXPECT_EQ(required, 1);
    EXPECT_EQ(qe_last_error(nullptr, 0, nullptr), QE_E_INVALID_ARG);

    ASSERT_EQ(qe_abi_version(nullptr, nullptr), QE_E_INVALID_ARG);
    ASSERT_EQ(qe_last_error(nullptr, 0, &required), QE_E_BUFFER_TOO_SMALL);
    ASSERT_GT(required, 1);
    std::vector<char> small(static_cast<std::size_t>(required - 1));
    EXPECT_EQ(qe_last_error(small.data(), required - 1, &required), QE_E_BUFFER_TOO_SMALL);
    // Reading the error does not clear it.
    EXPECT_FALSE(last_error().empty());
    EXPECT_FALSE(last_error().empty());
}

TEST(Abi, LastErrorIsThreadLocal) {
    ASSERT_EQ(qe_abi_version(nullptr, nullptr), QE_E_INVALID_ARG);
    std::string other_thread_error = "unset";
    std::thread worker([&] { other_thread_error = last_error(); });
    worker.join();
    EXPECT_TRUE(other_thread_error.empty());
    EXPECT_FALSE(last_error().empty());
}

TEST(Abi, StructLayoutsMatchThisCompiler) {
    qe_struct_layout_info info{};
    ASSERT_EQ(qe_struct_layout(QE_STRUCT_BS_INPUT, &info), QE_OK);
    EXPECT_EQ(info.size, static_cast<std::int32_t>(sizeof(qe_bs_input)));
    EXPECT_EQ(info.alignment, static_cast<std::int32_t>(alignof(qe_bs_input)));
    EXPECT_EQ(info.field_count, 8);
    EXPECT_EQ(info.offsets[0], static_cast<std::int32_t>(offsetof(qe_bs_input, spot)));
    EXPECT_EQ(info.offsets[6], static_cast<std::int32_t>(offsetof(qe_bs_input, option_type)));
    EXPECT_EQ(info.offsets[7], static_cast<std::int32_t>(offsetof(qe_bs_input, reserved)));
    EXPECT_EQ(info.offsets[8], -1);

    ASSERT_EQ(qe_struct_layout(QE_STRUCT_BS_OUTPUT, &info), QE_OK);
    EXPECT_EQ(info.size, static_cast<std::int32_t>(sizeof(qe_bs_output)));
    EXPECT_EQ(info.offsets[1], static_cast<std::int32_t>(offsetof(qe_bs_output, status)));

    ASSERT_EQ(qe_struct_layout(QE_STRUCT_ENGINE_CONFIG, &info), QE_OK);
    EXPECT_EQ(info.size, static_cast<std::int32_t>(sizeof(qe_engine_config)));
    EXPECT_EQ(info.offsets[2], static_cast<std::int32_t>(offsetof(qe_engine_config, seed)));

    ASSERT_EQ(qe_struct_layout(QE_STRUCT_LAYOUT_INFO, &info), QE_OK);
    EXPECT_EQ(info.size, static_cast<std::int32_t>(sizeof(qe_struct_layout_info)));
    EXPECT_EQ(info.offsets[4], static_cast<std::int32_t>(offsetof(qe_struct_layout_info, offsets)));

    EXPECT_EQ(qe_struct_layout(999, &info), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_struct_layout(QE_STRUCT_BS_INPUT, nullptr), QE_E_INVALID_ARG);
}

TEST(Abi, EngineCreateValidatesArguments) {
    EXPECT_EQ(qe_engine_create(nullptr, nullptr), QE_E_INVALID_ARG);

    qe_engine* engine = reinterpret_cast<qe_engine*>(0x1);
    const qe_engine_config wrong_size{8, 0, 0};
    EXPECT_EQ(qe_engine_create(&wrong_size, &engine), QE_E_INVALID_ARG);
    EXPECT_EQ(engine, nullptr);
    EXPECT_NE(last_error().find("struct_size"), std::string::npos);

    const qe_engine_config bad_flags{static_cast<std::int32_t>(sizeof(qe_engine_config)), 1, 0};
    EXPECT_EQ(qe_engine_create(&bad_flags, &engine), QE_E_INVALID_ARG);
    EXPECT_NE(last_error().find("flags"), std::string::npos);
}

TEST(Abi, EngineDefaultsAndNullDestroy) {
    qe_engine* engine = nullptr;
    ASSERT_EQ(qe_engine_create(nullptr, &engine), QE_OK);
    ASSERT_NE(engine, nullptr);
    EXPECT_EQ(qe_engine_destroy(engine), QE_OK);
    EXPECT_EQ(qe_engine_destroy(nullptr), QE_OK);
}

TEST_F(EngineTest, BsBatchSpecReference) {
    const std::vector<qe_bs_input> in{input(100, 100, 0.05, 0.0, 0.2, 1.0, QE_OPTION_CALL),
                                      input(100, 100, 0.05, 0.0, 0.2, 1.0, QE_OPTION_PUT)};
    std::vector<qe_bs_output> out(in.size());
    std::int64_t failed = -1;
    ASSERT_EQ(qe_bs_price_batch(engine_, in.data(), out.data(),
                                static_cast<std::int64_t>(in.size()), &failed),
              QE_OK);
    EXPECT_EQ(failed, 0);
    EXPECT_EQ(out[0].status, QE_OK);
    EXPECT_EQ(out[1].status, QE_OK);
    EXPECT_NEAR(out[0].price, 10.4506, 1e-4);
    EXPECT_NEAR(out[1].price, 5.5735, 1e-4);
}

TEST_F(EngineTest, BsBatchReportsPerElementFailures) {
    std::vector<qe_bs_input> in{input(100, 100, 0.05, 0.0, 0.2, 1.0, QE_OPTION_CALL),
                                input(-1, 100, 0.05, 0.0, 0.2, 1.0, QE_OPTION_CALL),
                                input(100, 100, 0.05, 0.0, 0.2, 1.0, 5),
                                input(100, 100, 0.05, 0.0, 0.2, 1.0, QE_OPTION_PUT)};
    in[3].reserved = 1;
    std::vector<qe_bs_output> out(in.size());
    std::int64_t failed = -1;
    ASSERT_EQ(qe_bs_price_batch(engine_, in.data(), out.data(),
                                static_cast<std::int64_t>(in.size()), &failed),
              QE_OK);
    EXPECT_EQ(failed, 3);
    EXPECT_EQ(out[0].status, QE_OK);
    for (std::size_t i = 1; i < out.size(); ++i) {
        EXPECT_EQ(out[i].status, QE_E_INVALID_ARG) << i;
        EXPECT_TRUE(std::isnan(out[i].price)) << i;
    }
}

TEST_F(EngineTest, BsBatchValidatesCallArguments) {
    std::int64_t failed = -1;
    qe_bs_input in = input(100, 100, 0.05, 0.0, 0.2, 1.0, QE_OPTION_CALL);
    qe_bs_output out{};
    EXPECT_EQ(qe_bs_price_batch(engine_, &in, &out, 1, nullptr), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bs_price_batch(nullptr, &in, &out, 1, &failed), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bs_price_batch(engine_, &in, &out, -1, &failed), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bs_price_batch(engine_, nullptr, &out, 1, &failed), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bs_price_batch(engine_, &in, nullptr, 1, &failed), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bs_price_batch(engine_, nullptr, nullptr, 0, &failed), QE_OK);
    EXPECT_EQ(failed, 0);
}

TEST_F(EngineTest, BsBatchLargeIsDeterministic) {
    constexpr std::size_t n = 100'000;
    std::vector<qe_bs_input> in;
    in.reserve(n);
    for (std::size_t i = 0; i < n; ++i) {
        const double s = 50.0 + static_cast<double>(i % 1000) * 0.1;
        in.push_back(
            input(s, 100, 0.03, 0.01, 0.25, 0.5, (i % 2 == 0) ? QE_OPTION_CALL : QE_OPTION_PUT));
    }
    std::vector<qe_bs_output> a(n);
    std::vector<qe_bs_output> b(n);
    std::int64_t failed = -1;
    ASSERT_EQ(
        qe_bs_price_batch(engine_, in.data(), a.data(), static_cast<std::int64_t>(n), &failed),
        QE_OK);
    ASSERT_EQ(failed, 0);
    ASSERT_EQ(
        qe_bs_price_batch(engine_, in.data(), b.data(), static_cast<std::int64_t>(n), &failed),
        QE_OK);
    for (std::size_t i = 0; i < n; ++i) {
        ASSERT_EQ(a[i].price, b[i].price) << i;
    }
}

} // namespace
