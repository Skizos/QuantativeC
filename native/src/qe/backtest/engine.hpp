#pragma once
// Event-driven daily backtest engine (Phase 5; docs/plans/05-phase5-backtesting.md "Engine").
//
// One call per bar processes the orders decided at the previous close. Fill rules:
//   LIMIT buy L : open <= L fills at the open (opening auction); else low < L fills at L; a touch
//   is no fill. LIMIT sell L: open >= L fills at the open; else high > L fills at L. MOO / MOC   :
//   fill at open / close, moved against the trader by half-spread + slippage (bps).
// Within a bar the phases run in time order (opening auction, continuous, closing auction), sells
// before buys, then by order index. Every fill is capped by the participation cap (share of the
// bar's volume, per instrument), long-only (sells <= position) and cash (buys <= what the cash can
// pay, costs included). Unfilled quantity expires (day orders). An invalid bar (no trading) fills
// nothing. The engine is a model: money is double.

#include <cstdint>
#include <span>
#include <vector>

namespace qe::backtest {

enum class OrderType : std::int32_t { Limit = 0, MarketOnOpen = 1, MarketOnClose = 2 };

// How limit orders fill (ABI 1.4, plan 17).
//   Daily   : the bar's open is the opening auction: a limit marketable there fills at the open;
//             otherwise it fills at its limit on a trade-through during continuous trading.
//   Intraday: a bar's open is just its first trade, not an auction: a limit fills only at its own
//             limit, and only when the bar trades through it (buy: low < limit; sell: high >
//             limit). Prices sit on the tick grid, so "through" is at least one tick; a touch is no
//             fill, and a better open is never given.
enum class FillMode : std::int32_t { Daily = 0, Intraday = 1 };

struct Config {
    double initial_cash{0};
    double courtage_min{0}; // courtage = max(min, rate * notional)
    double courtage_rate{0};
    double fx_fee_rate{0};       // on notional of foreign-currency instruments
    double slippage_bps{0};      // market-type fills only
    double half_spread_bps{0};   // market-type fills only
    double participation_cap{1}; // (0, 1]: share of the bar's volume an instrument may trade
};

struct Instrument {
    std::int64_t lot_size{1};
    bool foreign_currency{false};
    // ABI 1.3 (ADR 0005): an instrument's own courtage, max(min, rate * notional), instead of the
    // config's.
    bool own_courtage{false};
    double courtage_min{0};
    double courtage_rate{0};
    // ABI 1.4 (plan 17): an instrument's own half-spread for market-type fills (measured, bps).
    bool own_spread{false};
    double half_spread_bps{0};
};

struct Bar {
    double open{0}, high{0}, low{0}, close{0}, volume{0};
    bool valid{false};
};

struct Order {
    std::int32_t instrument{0};
    std::int32_t side{0}; // +1 buy, -1 sell
    OrderType type{OrderType::Limit};
    std::int64_t quantity{0}; // shares, a positive multiple of the lot size
    double limit_price{0};    // LIMIT only
};

struct Fill {
    std::int32_t instrument{0};
    std::int32_t side{0};
    OrderType type{OrderType::Limit};
    std::int32_t order_index{0};
    std::int64_t quantity{0};
    double price{0};
    double courtage{0};
    double fx_fee{0};
    double spread_slippage_cost{0};
};

struct State {
    double cash{0};
    double equity{0};
    double gross_exposure{0};
    double courtage{0};
    double fx_fees{0};
    double spread_slippage{0};
    std::int64_t fills{0};
    std::int64_t orders{0};
};

class Engine {
  public:
    Engine(const Config& config, std::span<const Instrument> instruments);

    /// Gives one instrument its own courtage (a foreign share pays its market's). Only before the
    /// first step; throws qe::InvalidArgument otherwise, or for an index out of range or a negative
    /// / non-finite / >= 10 % value.
    void set_courtage(std::size_t instrument, double courtage_min, double courtage_rate);

    /// Sets how limit orders fill (see FillMode). Only before the first step.
    void set_fill_mode(FillMode mode);

    /// Gives one instrument its own half-spread (bps, [0, 1000)) for MOO/MOC fills instead of the
    /// config's. Only before the first step.
    void set_half_spread(std::size_t instrument, double half_spread_bps);

    [[nodiscard]] FillMode fill_mode() const noexcept { return fill_mode_; }

    /// Processes one bar. Validates everything first, so a throwing call leaves the state
    /// unchanged. out must hold at least orders.size() fills (an order fills at most once per bar).
    /// Returns the fill count.
    std::size_t step(std::span<const Bar> bars, std::span<const Order> orders, std::span<Fill> out,
                     State& state);

    [[nodiscard]] std::span<const std::int64_t> positions() const noexcept { return positions_; }
    [[nodiscard]] const State& state() const noexcept { return state_; }
    [[nodiscard]] std::size_t instrument_count() const noexcept { return instruments_.size(); }

  private:
    enum class Phase { Open, Continuous, Close };

    [[nodiscard]] double courtage(const Instrument& instrument, double notional) const noexcept;
    [[nodiscard]] std::int64_t affordable(double price,
                                          const Instrument& instrument) const noexcept;
    bool try_fill(const Order& order, std::int32_t index, const Bar& bar, Phase phase, Fill& fill);

    Config config_;
    std::vector<Instrument> instruments_;
    std::vector<std::int64_t> positions_;
    std::vector<double> last_close_;
    std::vector<double> traded_this_bar_;
    State state_;
    FillMode fill_mode_{FillMode::Daily};
    bool stepped_{false};
};

} // namespace qe::backtest
