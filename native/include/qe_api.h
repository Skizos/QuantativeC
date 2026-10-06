/*
 * qe_api.h - the ONLY exported surface of the QuantEngine native library (qe.dll / libqe.so).
 *
 * Rules (ADR 0001):
 *  - Plain C11. Only fixed-width integers, doubles, pointers and the structs below cross the
 *    boundary. Flags are int32_t, never bool.
 *  - Every function returns qe_status and never lets a C++ exception escape. Results are
 *    delivered through out-pointers. On failure a message is available from qe_last_error()
 *    on the same thread.
 *  - The caller owns all buffers. The library never keeps a pointer past the call.
 *  - Breaking changes bump QE_ABI_MAJOR; additions bump QE_ABI_MINOR.
 */
#ifndef QE_API_H
#define QE_API_H

#include <stdint.h>

#if defined(_WIN32)
#define QE_CALL __cdecl
#if defined(QE_BUILDING_DLL)
#define QE_API __declspec(dllexport)
#else
#define QE_API __declspec(dllimport)
#endif
#else
#define QE_CALL
#define QE_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
#define QE_NOEXCEPT noexcept
#else
#define QE_NOEXCEPT
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* ---- Versioning --------------------------------------------------------------------------- */

#define QE_ABI_MAJOR 1
/* 1.1: pricing, risk and portfolio batch APIs; 1.2: backtest engine; 1.3: per-instrument courtage;
 * 1.4: intraday fills, per-instrument spread */
#define QE_ABI_MINOR 4

/* ---- Status codes ------------------------------------------------------------------------- */

typedef int32_t qe_status;

#define QE_OK 0
#define QE_E_INVALID_ARG 1      /* null pointer, bad size, out-of-domain input */
#define QE_E_NUMERIC 2          /* non-finite result from finite inputs */
#define QE_E_OUT_OF_MEMORY 3    /* allocation failed inside the library */
#define QE_E_BUFFER_TOO_SMALL 4 /* caller buffer too small; required size reported */
#define QE_E_INTERNAL 5         /* unexpected C++ exception or invariant violation */

/* ---- Enumerations (int32_t values) -------------------------------------------------------- */

#define QE_OPTION_CALL 0
#define QE_OPTION_PUT 1

#define QE_EXERCISE_EUROPEAN 0
#define QE_EXERCISE_AMERICAN 1

/* qe_mc_config.flags (combinable) */
#define QE_MC_ANTITHETIC 1
#define QE_MC_CONTROL_VARIATE 2
#define QE_MC_SOBOL 4 /* randomized QMC: Joe-Kuo Sobol with random digital shifts */

#define QE_COV_SAMPLE 0      /* unbiased (T - 1) */
#define QE_COV_EWMA 1        /* weights ~ lambda^(T-1-t), normalized, weighted mean removed */
#define QE_COV_LEDOIT_WOLF 2 /* shrinkage toward mu * I, as scikit-learn LedoitWolf */

#define QE_OPT_MIN_VARIANCE 0
#define QE_OPT_MEAN_VARIANCE 1
#define QE_OPT_RISK_PARITY 2 /* equal risk contribution, long-only; bounds must be NULL */
#define QE_OPT_HRP 3         /* hierarchical risk parity, long-only; bounds must be NULL */

/* qe_bt_order.type / qe_bt_fill.type */
#define QE_BT_LIMIT 0 /* day limit order; fills only if traded through (a touch is not a fill) */
#define QE_BT_MARKET_ON_OPEN 1  /* opening auction: open +/- (half-spread + slippage) */
#define QE_BT_MARKET_ON_CLOSE 2 /* closing auction: close +/- (half-spread + slippage) */

/* qe_bt_order.side / qe_bt_fill.side */
#define QE_BT_BUY 1
#define QE_BT_SELL (-1)

#define QE_STRUCT_ENGINE_CONFIG 1
#define QE_STRUCT_BS_INPUT 2
#define QE_STRUCT_BS_OUTPUT 3
#define QE_STRUCT_LAYOUT_INFO 4
#define QE_STRUCT_BS_GREEKS 5
#define QE_STRUCT_IV_INPUT 6
#define QE_STRUCT_IV_OUTPUT 7
#define QE_STRUCT_LATTICE_INPUT 8
#define QE_STRUCT_MC_CONFIG 9
#define QE_STRUCT_MC_RESULT 10
#define QE_STRUCT_COV_CONFIG 11
#define QE_STRUCT_VAR_ES 12
#define QE_STRUCT_OPT_CONFIG 13
#define QE_STRUCT_OPT_RESULT 14
#define QE_STRUCT_REBALANCE_ASSET 15
#define QE_STRUCT_REBALANCE_CONFIG 16
#define QE_STRUCT_REBALANCE_TRADE 17
#define QE_STRUCT_REBALANCE_SUMMARY 18
/* ABI 1.2 */
#define QE_STRUCT_BT_CONFIG 19
#define QE_STRUCT_BT_INSTRUMENT 20
#define QE_STRUCT_BT_BAR 21
#define QE_STRUCT_BT_ORDER 22
#define QE_STRUCT_BT_FILL 23
#define QE_STRUCT_BT_STATE 24

#define QE_LAYOUT_MAX_FIELDS 16

/* ---- Structs (fixed layout; sizes/offsets are verified by tests on both sides) ------------ */

/* Engine configuration. struct_size must equal sizeof(qe_engine_config). */
typedef struct qe_engine_config {
    int32_t struct_size;
    int32_t flags; /* must be 0 in ABI 1.0 */
    uint64_t seed; /* base seed for all randomness created by this engine */
} qe_engine_config;

/* One European option under Black-Scholes-Merton (continuous dividend yield). */
typedef struct qe_bs_input {
    double spot;           /* > 0 */
    double strike;         /* > 0 */
    double rate;           /* continuously compounded, finite */
    double dividend_yield; /* continuously compounded, finite */
    double volatility;     /* >= 0, annualized */
    double expiry_years;   /* >= 0 */
    int32_t option_type;   /* QE_OPTION_CALL or QE_OPTION_PUT */
    int32_t reserved;      /* must be 0 */
} qe_bs_input;

typedef struct qe_bs_output {
    double price;   /* NaN when status != QE_OK */
    int32_t status; /* per-element status */
    int32_t reserved;
} qe_bs_output;

typedef struct qe_struct_layout_info {
    int32_t size;
    int32_t alignment;
    int32_t field_count;
    int32_t reserved;
    int32_t offsets[QE_LAYOUT_MAX_FIELDS]; /* declaration order; unused entries are -1 */
} qe_struct_layout_info;

/* ---- ABI 1.1 structs ---------------------------------------------------------------------- */

/* Price and Greeks per unit: vega per 1.00 vol, theta = -dV/dT per year, rho per 1.00 rate. */
typedef struct qe_bs_greeks {
    double price;
    double delta;
    double gamma;
    double vega;
    double theta;
    double rho;
    int32_t status; /* QE_E_INVALID_ARG unless volatility > 0 and expiry_years > 0 */
    int32_t reserved;
} qe_bs_greeks;

typedef struct qe_iv_input {
    double spot;
    double strike;
    double rate;
    double dividend_yield;
    double expiry_years; /* > 0 */
    double price;        /* strictly inside the no-arbitrage bounds */
    int32_t option_type;
    int32_t reserved; /* must be 0 */
} qe_iv_input;

typedef struct qe_iv_output {
    double volatility; /* NaN when status != QE_OK */
    int32_t status;
    int32_t iterations;
} qe_iv_output;

typedef struct qe_lattice_input {
    qe_bs_input option; /* volatility > 0, expiry_years > 0 */
    int32_t steps;      /* [1, 100000] */
    int32_t exercise;   /* QE_EXERCISE_* */
} qe_lattice_input;

typedef struct qe_mc_config {
    int32_t struct_size; /* sizeof(qe_mc_config) */
    int32_t flags;       /* QE_MC_* */
    int64_t paths; /* simulated paths; antithetic partners count; even with QE_MC_ANTITHETIC */
    uint64_t seed; /* 0 = use the engine seed */
    int32_t replications; /* QE_MC_SOBOL only: >= 2 digital shifts, SE across replications */
    int32_t steps;        /* [1, 4096] time steps per path; <= 64 with QE_MC_SOBOL */
} qe_mc_config;

typedef struct qe_mc_result {
    double price;
    double std_error;
    int64_t paths;
    uint64_t seed; /* seed actually used */
} qe_mc_result;

typedef struct qe_cov_config {
    int32_t struct_size;
    int32_t method;     /* QE_COV_* */
    double ewma_lambda; /* (0, 1], QE_COV_EWMA only */
} qe_cov_config;

/* VaR and ES as positive loss fractions (0.03 = 3 % loss). */
typedef struct qe_var_es {
    double var;
    double es;
    int64_t observations; /* sample size or simulated paths */
    uint64_t seed;        /* Monte Carlo only */
} qe_var_es;

typedef struct qe_opt_config {
    int32_t struct_size;
    int32_t method;         /* QE_OPT_* */
    double risk_aversion;   /* > 0, QE_OPT_MEAN_VARIANCE only */
    double tolerance;       /* > 0; 0 selects the default 1e-12 */
    int32_t max_iterations; /* >= 1; 0 selects the default 100000 */
    int32_t reserved;
} qe_opt_config;

typedef struct qe_opt_result {
    /* MV: objective value; min-variance: 0.5 * variance; risk parity: max relative deviation of
       risk contributions from 1/N; HRP: portfolio variance. */
    double objective;
    int32_t iterations;
    int32_t converged; /* 1 = converged */
} qe_opt_result;

typedef struct qe_rebalance_asset {
    double price;             /* > 0 */
    double target_weight;     /* >= 0; sum <= 1 */
    int64_t current_quantity; /* >= 0 */
    int64_t lot_size;         /* >= 1 */
} qe_rebalance_asset;

typedef struct qe_rebalance_config {
    int32_t struct_size;
    int32_t reserved;
    double cash;
    double cash_buffer;
    double min_trade_value;
    double fee_min; /* courtage = max(fee_min, fee_rate * |trade value|) */
    double fee_rate;
} qe_rebalance_config;

typedef struct qe_rebalance_trade {
    int64_t target_quantity;
    int64_t trade_quantity; /* signed: > 0 buy, < 0 sell */
    double trade_value;
    double fee;
    double final_weight;
} qe_rebalance_trade;

typedef struct qe_rebalance_summary {
    double portfolio_value;
    double cash_after;
    double total_fees;
    double tracking_error; /* 0.5 * sum |final - target| over assets and cash */
    int32_t trades;
    int32_t feasible; /* 1 when cash_after >= cash_buffer */
} qe_rebalance_summary;

/* ---- ABI 1.2: backtest (docs/plans/05-phase5-backtesting.md "Engine") ---------------------- */

typedef struct qe_bt_config {
    int32_t struct_size;
    int32_t reserved; /* 0 */
    double initial_cash;
    double courtage_min; /* courtage = max(courtage_min, courtage_rate * notional) */
    double courtage_rate;
    double fx_fee_rate;       /* on the notional of foreign-currency instruments */
    double slippage_bps;      /* market-type fills only */
    double half_spread_bps;   /* market-type fills only */
    double participation_cap; /* (0, 1]: share of the bar's volume one instrument may trade */
} qe_bt_config;

typedef struct qe_bt_instrument {
    int64_t lot_size;         /* >= 1 */
    int32_t foreign_currency; /* 1 = the FX fee applies */
    int32_t reserved;         /* 0 */
} qe_bt_instrument;

typedef struct qe_bt_bar {
    double open;
    double high;
    double low;
    double close;
    double volume;
    int32_t valid;    /* 0 = no trading on this bar (holiday, halt, not listed): nothing fills */
    int32_t reserved; /* 0 */
} qe_bt_bar;

typedef struct qe_bt_order {
    int32_t instrument; /* index into the instruments given to qe_bt_create */
    int32_t side;       /* QE_BT_BUY or QE_BT_SELL */
    int32_t type;       /* QE_BT_* */
    int32_t reserved;   /* 0 */
    int64_t quantity;   /* shares, a positive multiple of the lot size */
    double limit_price; /* QE_BT_LIMIT only */
} qe_bt_order;

typedef struct qe_bt_fill {
    int32_t instrument;
    int32_t side;
    int32_t type;
    int32_t order_index; /* index into the orders of the qe_bt_step call */
    int64_t quantity;
    double price;
    double courtage;
    double fx_fee;
    double spread_slippage_cost; /* |price - auction price| * quantity for market-type fills */
} qe_bt_fill;

typedef struct qe_bt_state {
    double cash;
    double equity;          /* cash + positions at the last valid close */
    double gross_exposure;  /* positions at the last valid close */
    double courtage;        /* cumulative */
    double fx_fees;         /* cumulative */
    double spread_slippage; /* cumulative */
    int64_t fills;          /* cumulative */
    int64_t orders;         /* cumulative */
} qe_bt_state;

/* Opaque engine handle. */
typedef struct qe_engine qe_engine;

/* Opaque backtest handle (ABI 1.2). Not thread-safe: one thread at a time per handle. */
typedef struct qe_backtest qe_backtest;

/* ---- Functions ---------------------------------------------------------------------------- */

/* Reports the ABI version the library was built with. */
QE_API qe_status QE_CALL qe_abi_version(int32_t* major, int32_t* minor) QE_NOEXCEPT;

/*
 * Copies the calling thread's last error message (UTF-8, NUL-terminated) into buffer.
 * *required receives the needed capacity including the terminator (1 when there is no error).
 * Returns QE_E_BUFFER_TOO_SMALL when buffer is NULL or capacity < *required.
 * Does not modify the stored message.
 */
QE_API qe_status QE_CALL qe_last_error(char* buffer, int32_t capacity,
                                       int32_t* required) QE_NOEXCEPT;

/* Fills *out with size, alignment and field offsets of the struct identified by struct_id. */
QE_API qe_status QE_CALL qe_struct_layout(int32_t struct_id,
                                          qe_struct_layout_info* out) QE_NOEXCEPT;

/* Creates an engine. config may be NULL for defaults (seed 0). *out_engine is NULL on failure. */
QE_API qe_status QE_CALL qe_engine_create(const qe_engine_config* config,
                                          qe_engine** out_engine) QE_NOEXCEPT;

/* Destroys an engine. Passing NULL is a no-op that returns QE_OK. */
QE_API qe_status QE_CALL qe_engine_destroy(qe_engine* engine) QE_NOEXCEPT;

/*
 * Prices count options. The call status reports argument validity only; each element carries
 * its own status in outputs[i].status, and *failed_count receives the number of elements whose
 * status is not QE_OK. inputs/outputs may be NULL only when count == 0.
 */
QE_API qe_status QE_CALL qe_bs_price_batch(const qe_engine* engine, const qe_bs_input* inputs,
                                           qe_bs_output* outputs, int64_t count,
                                           int64_t* failed_count) QE_NOEXCEPT;

/* ---- ABI 1.1: pricing --------------------------------------------------------------------- */
/* Matrices are row-major double arrays with explicit dimensions. Batch calls report per-element
   status; other calls fail as a whole. */

QE_API qe_status QE_CALL qe_bs_greeks_batch(const qe_engine* engine, const qe_bs_input* inputs,
                                            qe_bs_greeks* outputs, int64_t count,
                                            int64_t* failed_count) QE_NOEXCEPT;

QE_API qe_status QE_CALL qe_implied_vol_batch(const qe_engine* engine, const qe_iv_input* inputs,
                                              qe_iv_output* outputs, int64_t count,
                                              int64_t* failed_count) QE_NOEXCEPT;

/* Cox-Ross-Rubinstein binomial prices (European or American). */
QE_API qe_status QE_CALL qe_lattice_batch(const qe_engine* engine, const qe_lattice_input* inputs,
                                          qe_bs_output* outputs, int64_t count,
                                          int64_t* failed_count) QE_NOEXCEPT;

/* Monte Carlo price of one European option; result carries SE, path count and seed. */
QE_API qe_status QE_CALL qe_mc_european(const qe_engine* engine, const qe_mc_config* config,
                                        const qe_bs_input* option, qe_mc_result* out) QE_NOEXCEPT;

/* ---- ABI 1.1: risk ------------------------------------------------------------------------ */

/* Covariance of a T x N return matrix into out_cov (N x N). out_shrinkage may be NULL. */
QE_API qe_status QE_CALL qe_covariance(const qe_engine* engine, const qe_cov_config* config,
                                       const double* returns, int64_t observations, int64_t assets,
                                       double* out_cov, double* out_shrinkage) QE_NOEXCEPT;

/* Historical VaR/ES: VaR = l_(ceil(c*n)) of sorted losses l = -r, ES = mean of losses from there.
 */
QE_API qe_status QE_CALL qe_var_es_historical(const double* returns, int64_t count,
                                              double confidence, qe_var_es* out) QE_NOEXCEPT;

/* Normal VaR/ES of weights w (N), mean returns (N, NULL = zero) and covariance (N x N). */
QE_API qe_status QE_CALL qe_var_es_parametric(const double* weights, const double* mean,
                                              const double* cov, int64_t assets, double confidence,
                                              qe_var_es* out) QE_NOEXCEPT;

/* Monte Carlo VaR/ES with multivariate normal returns; seed 0 = engine seed. */
QE_API qe_status QE_CALL qe_var_es_monte_carlo(const qe_engine* engine, const double* weights,
                                               const double* mean, const double* cov,
                                               int64_t assets, int64_t paths, uint64_t seed,
                                               double confidence, qe_var_es* out) QE_NOEXCEPT;

/* OLS betas of each column of a T x N return matrix on an index series of length T. */
QE_API qe_status QE_CALL qe_betas(const double* returns, const double* index, int64_t observations,
                                  int64_t assets, double* out_betas) QE_NOEXCEPT;

/* Scenario P&L = shocks (S x N) * values (N) into out_pnl (S). */
QE_API qe_status QE_CALL qe_stress_pnl(const double* values, const double* shocks, int64_t assets,
                                       int64_t scenarios, double* out_pnl) QE_NOEXCEPT;

/* ---- ABI 1.1: portfolio ------------------------------------------------------------------- */

/*
 * Portfolio weights into out_weights (N). mu is required for QE_OPT_MEAN_VARIANCE only.
 * lower/upper (N) apply to min-variance and mean-variance; NULL means 0 and 1.
 */
QE_API qe_status QE_CALL qe_optimize(const qe_engine* engine, const qe_opt_config* config,
                                     const double* mu, const double* cov, const double* lower,
                                     const double* upper, int64_t assets, double* out_weights,
                                     qe_opt_result* out_result) QE_NOEXCEPT;

/* Integer-lot rebalance; out_trades has count entries in input order. */
QE_API qe_status QE_CALL qe_rebalance(const qe_rebalance_config* config,
                                      const qe_rebalance_asset* assets, int64_t count,
                                      qe_rebalance_trade* out_trades,
                                      qe_rebalance_summary* out_summary) QE_NOEXCEPT;

/* ---- ABI 1.2: backtest ------------------------------------------------------------------- */

/* Creates a backtest over count instruments (their order fixes the instrument indices).
 *out is NULL on failure. */
QE_API qe_status QE_CALL qe_bt_create(const qe_bt_config* config,
                                      const qe_bt_instrument* instruments, int64_t count,
                                      qe_backtest** out) QE_NOEXCEPT;

/* Destroys a backtest. Passing NULL is a no-op that returns QE_OK. */
QE_API qe_status QE_CALL qe_bt_destroy(qe_backtest* backtest) QE_NOEXCEPT;

/*
 * Processes one bar: bars holds one entry per instrument (bar_count == instrument count) and
 * orders are the day orders for this bar, decided at the previous close. Fills go to fills in
 * the order they happened (opening auction, continuous, closing auction; sells before buys);
 * *fill_count receives how many. An order fills at most once, so fill_capacity >= order_count
 * always suffices; a smaller buffer returns QE_E_BUFFER_TOO_SMALL with *fill_count set to
 * order_count. The state after the bar goes to *out_state. On any failure the backtest is
 * unchanged. orders/fills may be NULL only when order_count == 0.
 */
QE_API qe_status QE_CALL qe_bt_step(qe_backtest* backtest, const qe_bt_bar* bars, int64_t bar_count,
                                    const qe_bt_order* orders, int64_t order_count,
                                    qe_bt_fill* fills, int64_t fill_capacity, int64_t* fill_count,
                                    qe_bt_state* out_state) QE_NOEXCEPT;

/* Copies the current positions (shares) into out; count must equal the instrument count. */
QE_API qe_status QE_CALL qe_bt_positions(const qe_backtest* backtest, int64_t* out,
                                         int64_t count) QE_NOEXCEPT;

/* ---- ABI 1.3: per-instrument courtage (ADR 0005) ----------------------------------------- */

/*
 * Gives one instrument its own courtage, max(courtage_min, courtage_rate * notional), instead of
 * the config's (a foreign share pays its market's courtage). Only before the first qe_bt_step.
 * courtage_min must be finite and >= 0, courtage_rate in [0, 0.1). On failure the backtest is
 * unchanged.
 */
QE_API qe_status QE_CALL qe_bt_set_courtage(qe_backtest* backtest, int64_t instrument,
                                            double courtage_min, double courtage_rate) QE_NOEXCEPT;

/* ---- ABI 1.4: intraday fills and per-instrument spread (plan 17) ----------------------------- */

/* qe_bt_set_fill_mode mode. DAILY: the bar's open is the opening auction (the default).
 * INTRADAY: a limit fills only at its limit, on a trade-through; never at a better open. */
#define QE_BT_FILL_DAILY 0
#define QE_BT_FILL_INTRADAY 1

/*
 * How limit orders fill. QE_BT_FILL_INTRADAY is for bars of minutes, whose open is not an auction:
 * a buy limit fills at its limit when low < limit, a sell when high > limit (a touch is no fill),
 * and no limit fills at a better bar open. MOO/MOC are unchanged. Only before the first
 * qe_bt_step; on failure the backtest is unchanged.
 */
QE_API qe_status QE_CALL qe_bt_set_fill_mode(qe_backtest* backtest, int32_t mode) QE_NOEXCEPT;

/*
 * Gives one instrument its own half-spread in bps, [0, 1000), for MOO/MOC fills instead of the
 * config's (a measured spread per share). Only before the first qe_bt_step; on failure the
 * backtest is unchanged.
 */
QE_API qe_status QE_CALL qe_bt_set_half_spread(qe_backtest* backtest, int64_t instrument,
                                               double half_spread_bps) QE_NOEXCEPT;

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* QE_API_H */
