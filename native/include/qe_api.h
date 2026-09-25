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
#define QE_ABI_MINOR 0

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

#define QE_STRUCT_ENGINE_CONFIG 1
#define QE_STRUCT_BS_INPUT 2
#define QE_STRUCT_BS_OUTPUT 3
#define QE_STRUCT_LAYOUT_INFO 4

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

/* Opaque engine handle. */
typedef struct qe_engine qe_engine;

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

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* QE_API_H */
