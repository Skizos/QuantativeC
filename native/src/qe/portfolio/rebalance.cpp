#include "qe/portfolio/rebalance.hpp"

#include "qe/core/errors.hpp"

#include <algorithm>
#include <cmath>
#include <limits>

namespace qe::portfolio {
namespace {

void validate(std::span<const RebalanceAsset> assets, const RebalanceConfig& c) {
    if (assets.empty()) {
        throw InvalidArgument("rebalance: no assets");
    }
    double weight_sum = 0.0;
    for (const auto& a : assets) {
        if (!(a.price > 0.0) || !std::isfinite(a.price)) {
            throw InvalidArgument("rebalance: prices must be finite and > 0");
        }
        if (!(a.target_weight >= 0.0) || !std::isfinite(a.target_weight)) {
            throw InvalidArgument("rebalance: target weights must be finite and >= 0");
        }
        if (a.current_quantity < 0 || a.lot_size < 1) {
            throw InvalidArgument("rebalance: quantities must be >= 0 and lot sizes >= 1");
        }
        weight_sum += a.target_weight;
    }
    if (weight_sum > 1.0 + 1e-9) {
        throw InvalidArgument("rebalance: target weights sum to more than 1");
    }
    const double values[] = {c.cash, c.cash_buffer, c.min_trade_value, c.fee_min, c.fee_rate};
    for (const double v : values) {
        if (!(v >= 0.0) || !std::isfinite(v)) {
            throw InvalidArgument(
                "rebalance: cash, buffer, thresholds and fees must be finite and >= 0");
        }
    }
    if (c.fee_rate > 0.1) {
        throw InvalidArgument("rebalance: fee_rate must be <= 0.1");
    }
}

class State {
  public:
    State(std::span<const RebalanceAsset> assets, const RebalanceConfig& config)
        : assets_(assets), config_(config), delta_(assets.size(), 0) {
        value_ = config.cash;
        for (const auto& a : assets) {
            value_ += a.price * static_cast<double>(a.current_quantity);
        }
    }

    [[nodiscard]] double portfolio_value() const { return value_; }
    [[nodiscard]] std::int64_t delta(std::size_t i) const { return delta_[i]; }
    void set_delta(std::size_t i, std::int64_t d) { delta_[i] = d; }

    [[nodiscard]] double fee(double trade_value) const {
        return trade_value == 0.0
                   ? 0.0
                   : std::max(config_.fee_min, config_.fee_rate * std::abs(trade_value));
    }

    [[nodiscard]] double trade_value(std::size_t i) const {
        return assets_[i].price * static_cast<double>(delta_[i]);
    }

    [[nodiscard]] double cash_after() const {
        double cash = config_.cash;
        for (std::size_t i = 0; i < assets_.size(); ++i) {
            const double v = trade_value(i);
            cash -= v + fee(v);
        }
        return cash;
    }

    [[nodiscard]] double weight(std::size_t i) const {
        const auto q = static_cast<double>(assets_[i].current_quantity + delta_[i]);
        return value_ > 0.0 ? assets_[i].price * q / value_ : 0.0;
    }

  private:
    std::span<const RebalanceAsset> assets_;
    RebalanceConfig config_;
    std::vector<std::int64_t> delta_;
    double value_{0.0};
};

} // namespace

RebalanceResult rebalance(std::span<const RebalanceAsset> assets, const RebalanceConfig& config) {
    validate(assets, config);
    State s(assets, config);
    const std::size_t n = assets.size();
    const double investable = std::max(s.portfolio_value() - config.cash_buffer, 0.0);
    const double value = s.portfolio_value();
    auto target = [&](std::size_t i) {
        return value > 0.0 ? assets[i].target_weight * investable / value : 0.0;
    };

    // 1-2. Lot rounding toward the target, then the minimum trade threshold.
    for (std::size_t i = 0; i < n; ++i) {
        const auto& a = assets[i];
        const auto lot = static_cast<double>(a.lot_size);
        const double ideal = a.target_weight * investable / a.price;
        const double diff_lots = (ideal - static_cast<double>(a.current_quantity)) / lot;
        auto lots = static_cast<std::int64_t>(diff_lots >= 0.0 ? std::floor(diff_lots)
                                                               : std::round(diff_lots));
        lots = std::max(lots, -(a.current_quantity / a.lot_size)); // never below zero
        s.set_delta(i, lots * a.lot_size);
        if (std::abs(s.trade_value(i)) < config.min_trade_value) {
            s.set_delta(i, 0);
        }
    }

    // 3. Cash repair: trim the most overweight buy one lot at a time.
    while (s.cash_after() < config.cash_buffer) {
        std::size_t pick = n;
        double most_over = -std::numeric_limits<double>::infinity();
        for (std::size_t i = 0; i < n; ++i) {
            if (s.delta(i) > 0 && s.weight(i) - target(i) > most_over) {
                most_over = s.weight(i) - target(i);
                pick = i;
            }
        }
        if (pick == n) {
            break; // no buys left to trim: infeasible, reported below
        }
        s.set_delta(pick, s.delta(pick) - assets[pick].lot_size);
        if (s.delta(pick) > 0 && s.trade_value(pick) < config.min_trade_value) {
            s.set_delta(pick, 0);
        }
    }

    // 4. Greedy top-up of underweight names while cash allows and tracking error improves.
    while (s.cash_after() >= config.cash_buffer) {
        std::size_t pick = n;
        std::int64_t pick_step = 0;
        double most_under = 0.0;
        for (std::size_t i = 0; i < n; ++i) {
            if (s.delta(i) < 0) {
                continue; // do not shrink sells
            }
            const double under = target(i) - s.weight(i);
            if (under <= most_under) {
                continue;
            }
            const auto& a = assets[i];
            std::int64_t lots = 1;
            if (s.delta(i) == 0 && config.min_trade_value > 0.0) {
                lots = std::max<std::int64_t>(
                    1, static_cast<std::int64_t>(std::ceil(
                           config.min_trade_value / (a.price * static_cast<double>(a.lot_size)))));
            }
            const std::int64_t step = lots * a.lot_size;
            const double step_weight = a.price * static_cast<double>(step) / value;
            if (step_weight >= 2.0 * under) {
                continue; // would overshoot by more than it corrects
            }
            const std::int64_t before = s.delta(i);
            s.set_delta(i, before + step);
            const bool affordable = s.cash_after() >= config.cash_buffer;
            s.set_delta(i, before);
            if (affordable) {
                most_under = under;
                pick = i;
                pick_step = step;
            }
        }
        if (pick == n) {
            break;
        }
        s.set_delta(pick, s.delta(pick) + pick_step);
    }

    RebalanceResult result;
    result.trades.resize(n);
    RebalanceSummary& sum = result.summary;
    sum.portfolio_value = value;
    sum.cash_after = s.cash_after();
    double cash_target = 1.0;
    for (std::size_t i = 0; i < n; ++i) {
        auto& t = result.trades[i];
        t.trade_quantity = s.delta(i);
        t.target_quantity = assets[i].current_quantity + t.trade_quantity;
        t.trade_value = s.trade_value(i);
        t.fee = s.fee(t.trade_value);
        t.final_weight = s.weight(i);
        sum.total_fees += t.fee;
        sum.trades += t.trade_quantity != 0 ? 1 : 0;
        sum.tracking_error += std::abs(t.final_weight - assets[i].target_weight);
        cash_target -= assets[i].target_weight;
    }
    const double cash_weight = value > 0.0 ? sum.cash_after / value : 0.0;
    sum.tracking_error = 0.5 * (sum.tracking_error + std::abs(cash_weight - cash_target));
    sum.feasible = sum.cash_after >= config.cash_buffer;
    return result;
}

} // namespace qe::portfolio
