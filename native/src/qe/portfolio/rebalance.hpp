#pragma once

#include <cstdint>
#include <span>
#include <vector>

namespace qe::portfolio {

struct RebalanceAsset {
    double price{};                  ///< > 0, portfolio currency
    double target_weight{};          ///< >= 0; weights sum to <= 1 (remainder stays in cash)
    std::int64_t current_quantity{}; ///< >= 0 (no shorts)
    std::int64_t lot_size{1};        ///< >= 1; every trade is a multiple of it
};

struct RebalanceConfig {
    double cash{};            ///< >= 0
    double cash_buffer{};     ///< cash to keep after trades and fees, >= 0
    double min_trade_value{}; ///< trades below this value are not placed, >= 0
    double fee_min{};         ///< courtage = max(fee_min, fee_rate * |trade value|)
    double fee_rate{};        ///< in [0, 0.1]
};

struct RebalanceTrade {
    std::int64_t target_quantity{}; ///< quantity after the trade
    std::int64_t trade_quantity{};  ///< signed: > 0 buy, < 0 sell
    double trade_value{};           ///< signed price * trade_quantity
    double fee{};
    double final_weight{};
};

struct RebalanceSummary {
    double portfolio_value{}; ///< before trades
    double cash_after{};
    double total_fees{};
    double tracking_error{}; ///< 0.5 * sum |final - target| over assets and cash
    int trades{};
    bool feasible{}; ///< cash_after >= cash_buffer
};

struct RebalanceResult {
    std::vector<RebalanceTrade> trades;
    RebalanceSummary summary;
};

/// Integer-lot rebalance toward target weights of (portfolio value - cash_buffer):
/// 1. round each trade to the nearest lot (buys rounded down), never selling below zero;
/// 2. drop trades below min_trade_value;
/// 3. while cash_after < cash_buffer, trim the most overweight buy by one lot;
/// 4. greedily add lots to the most underweight names while cash allows and tracking error falls.
/// Deterministic (ties break by index). Throws InvalidArgument on invalid input.
[[nodiscard]] RebalanceResult rebalance(std::span<const RebalanceAsset> assets,
                                        const RebalanceConfig& config);

} // namespace qe::portfolio
