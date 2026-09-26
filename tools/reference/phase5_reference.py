#!/usr/bin/env python3
"""Generate independent reference values for the Phase 5 statistics tests (never used at runtime).

Output (committed):
  tests/QuantAnalyst.Analytics.Tests/Reference/phase5_reference.json

Implements, from the papers and independently of the C# code:
  * per-period Sharpe (mean / sample std, ddof=1), skewness G1 and kurtosis G2+3 (the adjusted
    Fisher-Pearson estimators, as pandas/Excel)
  * PSR  - Bailey & Lopez de Prado 2012, "The Sharpe Ratio Efficient Frontier", SSRN 1821643
  * DSR  - Bailey & Lopez de Prado 2014, "The Deflated Sharpe Ratio", SSRN 2460551
  * PBO  - Bailey, Borwein, Lopez de Prado, Zhu 2017, "The Probability of Backtest Overfitting"
           (CSCV), SSRN 2326253
  * scipy.stats.norm cdf/ppf points for the C# normal distribution

Usage (from the repo root, in a venv with numpy and scipy):
  python tools/reference/phase5_reference.py

Everything is seeded; re-running with the same library versions reproduces the file exactly.
"""
from __future__ import annotations

import itertools
import json
from pathlib import Path

import numpy as np
import scipy
from scipy.stats import norm, rankdata

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "QuantAnalyst.Analytics.Tests" / "Reference" / "phase5_reference.json"
SEED = 20260926
EULER_GAMMA = 0.5772156649015329


def sharpe(r: np.ndarray) -> float:
    return float(np.mean(r) / np.std(r, ddof=1))


def skew_g1(r: np.ndarray) -> float:
    n = len(r)
    d = r - r.mean()
    m2 = np.mean(d**2)
    m3 = np.mean(d**3)
    g1 = m3 / m2**1.5
    return float(g1 * np.sqrt(n * (n - 1)) / (n - 2))


def kurt_g2_plus3(r: np.ndarray) -> float:
    n = len(r)
    d = r - r.mean()
    m2 = np.mean(d**2)
    m4 = np.mean(d**4)
    g2 = m4 / m2**2 - 3.0
    G2 = ((n + 1) * g2 + 6.0) * (n - 1) / ((n - 2) * (n - 3))
    return float(G2 + 3.0)


def psr(sr: float, t: int, skew: float, kurt: float, target: float) -> float:
    z = (sr - target) * np.sqrt(t - 1) / np.sqrt(1.0 - skew * sr + (kurt - 1.0) / 4.0 * sr**2)
    return float(norm.cdf(z))


def expected_max_z(n: int) -> float:
    return float((1 - EULER_GAMMA) * norm.ppf(1 - 1.0 / n) + EULER_GAMMA * norm.ppf(1 - 1.0 / (n * np.e)))


def dsr(sr: float, sr_std: float, n: int, t: int, skew: float, kurt: float) -> float:
    return psr(sr, t, skew, kurt, sr_std * expected_max_z(n))


def pbo_cscv(m: np.ndarray, s: int) -> dict:
    """CSCV on a T x N matrix (rows in time order), metric = per-period Sharpe of each column."""
    t, n = m.shape
    m = m[t % s:]  # drop the oldest rows so that S equal blocks fit (as the paper's implementations do)
    t = m.shape[0]
    size = t // s
    blocks = [m[i * size:(i + 1) * size] for i in range(s)]
    logits = []
    oos_best = []
    for train in itertools.combinations(range(s), s // 2):
        test = [i for i in range(s) if i not in train]
        is_m = np.concatenate([blocks[i] for i in train])
        oos_m = np.concatenate([blocks[i] for i in test])
        is_sr = np.array([sharpe(is_m[:, j]) for j in range(n)])
        oos_sr = np.array([sharpe(oos_m[:, j]) for j in range(n)])
        best = int(np.argmax(is_sr))  # first index on ties
        rank = rankdata(oos_sr)[best]  # average ranks, 1 = worst
        w = rank / (n + 1)
        logits.append(float(np.log(w / (1 - w))))
        oos_best.append(float(oos_sr[best]))
    logits_a = np.array(logits)
    return {
        "s": s,
        "combinations": len(logits),
        "pbo": float(np.mean(logits_a <= 0)),
        "probOosLoss": float(np.mean(np.array(oos_best) < 0)),
        "logitMean": float(logits_a.mean()),
        "firstLogits": [float(x) for x in logits_a[:5]],
    }


def main() -> None:
    rng = np.random.default_rng(SEED)

    # 1) A skewed, fat-tailed daily return series (mixture of normals).
    t1 = 500
    base = rng.normal(0.0006, 0.011, t1)
    jumps = rng.random(t1) < 0.03
    r1 = base + jumps * rng.normal(-0.02, 0.03, t1)
    sr1, sk1, ku1 = sharpe(r1), skew_g1(r1), kurt_g2_plus3(r1)

    # 2) A sweep of N strategies: noise plus one column with a small real edge.
    t2, n2 = 480, 12
    m_noise = rng.normal(0.0, 0.01, (t2, n2))
    m_edge = m_noise.copy()
    m_edge[:, 3] += 0.0015
    srs = np.array([sharpe(m_edge[:, j]) for j in range(n2)])
    best = int(np.argmax(srs))
    col = m_edge[:, best]
    sr_std = float(np.std(srs, ddof=1))

    reference = {
        "generator": "tools/reference/phase5_reference.py",
        "numpy": np.__version__,
        "scipy": scipy.__version__,
        "seed": SEED,
        "normal": {
            "cdf": [[x, float(norm.cdf(x))] for x in (-8.0, -5.0, -3.0, -1.96, -1.0, -0.3, 0.0, 0.5, 1.0, 2.5, 4.0, 7.5)],
            "ppf": [[p, float(norm.ppf(p))] for p in (1e-12, 1e-6, 0.001, 0.025, 0.1, 0.3, 0.5, 0.8, 0.975, 0.999, 1 - 1e-9)],
        },
        "series": {
            "returns": [float(x) for x in r1],
            "sharpe": sr1,
            "skew": sk1,
            "kurtosis": ku1,
            "psr0": psr(sr1, t1, sk1, ku1, 0.0),
            "psr005": psr(sr1, t1, sk1, ku1, 0.05),
        },
        "expectedMaxZ": [[n, expected_max_z(n)] for n in (2, 5, 10, 50, 100, 1000)],
        "sweep": {
            "t": t2,
            "n": n2,
            "matrixRowMajor": [float(x) for x in m_edge.ravel()],
            "sharpes": [float(x) for x in srs],
            "best": best,
            "bestSharpe": float(srs[best]),
            "sharpeStd": sr_std,
            "bestSkew": skew_g1(col),
            "bestKurtosis": kurt_g2_plus3(col),
            "dsr": dsr(float(srs[best]), sr_std, n2, t2, skew_g1(col), kurt_g2_plus3(col)),
            "pboS16": pbo_cscv(m_edge, 16),
            "pboS8": pbo_cscv(m_edge, 8),
            "pboNoiseS8": pbo_cscv(m_noise, 8),
        },
    }
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(reference, indent=1) + "\n")
    print(f"wrote {OUT.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
