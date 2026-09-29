#include "qe/backtest/engine.hpp"

#include "qe/core/errors.hpp"

#include <algorithm>
#include <cmath>
#include <string>

namespace qe::backtest {

namespace {

void require(bool condition, const char* message) {
    if (!condition) {
        throw qe::InvalidArgument(message);
    }
}

bool finite_positive(double x) noexcept {
    return std::isfinite(x) && x > 0.0;
}

} // namespace

Engine::Engine(const Config& config, std::span<const Instrument> instruments)
    : config_(config), instruments_(instruments.begin(), instruments.end()) {
    require(!instruments_.empty(), "at least one instrument is required");
    require(std::isfinite(config.initial_cash) && config.initial_cash >= 0.0,
            "initial_cash must be finite and >= 0");
    require(std::isfinite(config.courtage_min) && config.courtage_min >= 0.0,
            "courtage_min must be finite and >= 0");
    require(std::isfinite(config.courtage_rate) && config.courtage_rate >= 0.0 &&
                config.courtage_rate < 0.1,
            "courtage_rate must be in [0, 0.1)");
    require(std::isfinite(config.fx_fee_rate) && config.fx_fee_rate >= 0.0 &&
                config.fx_fee_rate < 0.1,
            "fx_fee_rate must be in [0, 0.1)");
    require(std::isfinite(config.slippage_bps) && config.slippage_bps >= 0.0 &&
                config.slippage_bps < 1000.0,
            "slippage_bps must be in [0, 1000)");
    require(std::isfinite(config.half_spread_bps) && config.half_spread_bps >= 0.0 &&
                config.half_spread_bps < 1000.0,
            "half_spread_bps must be in [0, 1000)");
    require(config.participation_cap > 0.0 && config.participation_cap <= 1.0,
            "participation_cap must be in (0, 1]");
    for (const Instrument& i : instruments_) {
        require(i.lot_size >= 1, "every instrument needs lot_size >= 1");
    }
    const std::size_t n = instruments_.size();
    positions_.assign(n, 0);
    last_close_.assign(n, std::nan(""));
    traded_this_bar_.assign(n, 0.0);
    state_.cash = config.initial_cash;
    state_.equity = config.initial_cash;
}

void Engine::set_courtage(std::size_t instrument, double courtage_min, double courtage_rate) {
    require(!stepped_, "set_courtage must come before the first step");
    require(instrument < instruments_.size(), "instrument out of range");
    require(std::isfinite(courtage_min) && courtage_min >= 0.0, "courtage_min must be finite and >= 0");
    require(std::isfinite(courtage_rate) && courtage_rate >= 0.0 && courtage_rate < 0.1,
            "courtage_rate must be in [0, 0.1)");
    Instrument& i = instruments_[instrument];
    i.own_courtage = true;
    i.courtage_min = courtage_min;
    i.courtage_rate = courtage_rate;
}

double Engine::courtage(const Instrument& instrument, double notional) const noexcept {
    return instrument.own_courtage
               ? std::max(instrument.courtage_min, instrument.courtage_rate * notional)
               : std::max(config_.courtage_min, config_.courtage_rate * notional);
}

// Largest lot multiple whose price plus costs the cash can pay.
std::int64_t Engine::affordable(double price, const Instrument& instrument) const noexcept {
    const double cash = state_.cash;
    const std::int64_t lot = instrument.lot_size;
    const double fx = instrument.foreign_currency ? config_.fx_fee_rate : 0.0;
    const double minimum = instrument.own_courtage ? instrument.courtage_min : config_.courtage_min;
    auto cost = [&](std::int64_t q) {
        const double v = static_cast<double>(q) * price;
        return v + courtage(instrument, v) + fx * v;
    };
    if (cash <= 0.0) {
        return 0;
    }
    // courtage = max(min, rate * v) has two regimes. Solve each in closed form (no search loops):
    //  - rate regime (rate * v >= min): v * (1 + rate + fx) <= cash
    //  - minimum regime (rate * v <= min): v * (1 + fx) + min <= cash, and v at most min / rate
    const double lot_value = price * static_cast<double>(lot);
    auto lots_at_most = [](double x) { return x > 0.0 ? std::floor(std::min(x, 9.0e15)) : 0.0; };
    const double rate = instrument.own_courtage ? instrument.courtage_rate : config_.courtage_rate;
    double lots_min = lots_at_most((cash - minimum) / (lot_value * (1.0 + fx)));
    if (rate > 0.0) {
        lots_min = std::min(lots_min, lots_at_most(minimum / (rate * lot_value)));
    }
    const double lots_rate = lots_at_most(cash / (lot_value * (1.0 + rate + fx)));
    std::int64_t best = 0;
    for (double lots : {lots_rate, lots_min}) {
        // Floating-point rounding can overshoot by a hair: step back at most one lot, never search.
        std::int64_t q = static_cast<std::int64_t>(lots) * lot;
        if (q > 0 && cost(q) > cash) {
            q -= lot;
        }
        if (q > 0 && cost(q) <= cash) {
            best = std::max(best, q);
        }
    }
    return best;
}

bool Engine::try_fill(const Order& order, std::int32_t index, const Bar& bar, Phase phase,
                      Fill& fill) {
    const auto i = static_cast<std::size_t>(order.instrument);
    const Instrument& instrument = instruments_[i];
    const bool buy = order.side > 0;

    // Which phase fills this order, and at what price?
    double price = 0.0;
    double reference = 0.0; // price before spread/slippage (market-type fills)
    switch (order.type) {
    case OrderType::Limit:
        if (phase == Phase::Open) {
            if (buy ? bar.open <= order.limit_price : bar.open >= order.limit_price) {
                price = bar.open;
            }
        } else if (phase == Phase::Continuous) {
            if (buy ? (bar.open > order.limit_price && bar.low < order.limit_price)
                    : (bar.open < order.limit_price && bar.high > order.limit_price)) {
                price = order.limit_price; // traded through during continuous trading
            }
        }
        reference = price;
        break;
    case OrderType::MarketOnOpen:
    case OrderType::MarketOnClose: {
        const bool mine =
            (order.type == OrderType::MarketOnOpen) ? phase == Phase::Open : phase == Phase::Close;
        if (mine) {
            reference = order.type == OrderType::MarketOnOpen ? bar.open : bar.close;
            const double bps = (config_.half_spread_bps + config_.slippage_bps) / 10000.0;
            price = reference * (buy ? 1.0 + bps : 1.0 - bps);
        }
        break;
    }
    }
    if (!(price > 0.0)) {
        return false;
    }

    // Quantity caps: participation (per instrument and bar), long-only, cash.
    const std::int64_t lot = instrument.lot_size;
    const double room = std::max(0.0, config_.participation_cap * bar.volume - traded_this_bar_[i]);
    std::int64_t quantity = std::min<std::int64_t>(
        order.quantity,
        static_cast<std::int64_t>(std::floor(room / static_cast<double>(lot))) * lot);
    if (buy) {
        quantity = std::min(quantity, affordable(price, instrument));
    } else {
        quantity = std::min(quantity, positions_[i] / lot * lot);
    }
    if (quantity <= 0) {
        return false;
    }

    const double notional = static_cast<double>(quantity) * price;
    const double fee = courtage(instrument, notional);
    const double fx = instrument.foreign_currency ? config_.fx_fee_rate * notional : 0.0;
    const double spread_slippage = std::abs(price - reference) * static_cast<double>(quantity);
    if (buy) {
        state_.cash -= notional + fee + fx;
        positions_[i] += quantity;
    } else {
        state_.cash += notional - fee - fx;
        positions_[i] -= quantity;
    }
    traded_this_bar_[i] += static_cast<double>(quantity);
    state_.courtage += fee;
    state_.fx_fees += fx;
    state_.spread_slippage += spread_slippage;
    state_.fills += 1;
    fill = Fill{order.instrument, order.side, order.type, index, quantity, price, fee, fx,
                spread_slippage};
    return true;
}

std::size_t Engine::step(std::span<const Bar> bars, std::span<const Order> orders,
                         std::span<Fill> out, State& state) {
    const std::size_t n = instruments_.size();
    require(bars.size() == n, "bars must hold one entry per instrument");
    require(out.size() >= orders.size(), "the fill buffer must hold at least one fill per order");
    for (const Bar& b : bars) {
        if (!b.valid) {
            continue;
        }
        require(finite_positive(b.open) && finite_positive(b.high) && finite_positive(b.low) &&
                    finite_positive(b.close),
                "valid bars need finite positive prices");
        require(b.low <= b.open && b.low <= b.close && b.high >= b.open && b.high >= b.close &&
                    b.low <= b.high,
                "valid bars need low <= open, close <= high");
        require(std::isfinite(b.volume) && b.volume >= 0.0, "valid bars need a finite volume >= 0");
    }
    for (const Order& o : orders) {
        require(o.instrument >= 0 && static_cast<std::size_t>(o.instrument) < n,
                "order instrument out of range");
        require(o.side == 1 || o.side == -1, "order side must be +1 (buy) or -1 (sell)");
        require(o.type == OrderType::Limit || o.type == OrderType::MarketOnOpen ||
                    o.type == OrderType::MarketOnClose,
                "order type must be LIMIT, MOO or MOC");
        const std::int64_t lot = instruments_[static_cast<std::size_t>(o.instrument)].lot_size;
        require(o.quantity > 0 && o.quantity % lot == 0,
                "order quantity must be a positive multiple of the lot size");
        require(o.type != OrderType::Limit || finite_positive(o.limit_price),
                "limit orders need a finite positive limit");
    }

    // Validated: from here on the step cannot fail.
    stepped_ = true;
    std::fill(traded_this_bar_.begin(), traded_this_bar_.end(), 0.0);
    state_.orders += static_cast<std::int64_t>(orders.size());
    // Each order can fill in exactly one phase (a limit marketable at the open cannot also trade
    // through later, and MOO/MOC belong to their auction), so no order fills twice and the fill
    // count never exceeds the order count.
    std::size_t count = 0;
    Fill fill;
    for (Phase phase : {Phase::Open, Phase::Continuous, Phase::Close}) {
        for (int side : {-1, 1}) { // sells first: their proceeds can pay for buys in the same phase
            for (std::size_t k = 0; k < orders.size(); ++k) {
                const Order& o = orders[k];
                const Bar& bar = bars[static_cast<std::size_t>(o.instrument)];
                if (o.side != side || !bar.valid) {
                    continue;
                }
                // Fill into a local: out[count] is past the end once every order has filled.
                if (try_fill(o, static_cast<std::int32_t>(k), bar, phase, fill)) {
                    out[count++] = fill;
                }
            }
        }
    }

    double exposure = 0.0;
    for (std::size_t i = 0; i < n; ++i) {
        if (bars[i].valid) {
            last_close_[i] = bars[i].close;
        }
        if (positions_[i] != 0) {
            exposure += std::abs(static_cast<double>(positions_[i])) * last_close_[i];
        }
    }
    state_.gross_exposure = exposure;
    state_.equity = state_.cash + exposure;
    state = state_;
    return count;
}

} // namespace qe::backtest
