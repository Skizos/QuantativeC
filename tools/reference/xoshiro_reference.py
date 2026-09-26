#!/usr/bin/env python3
"""Reference outputs for QuantAnalyst.Analytics.Backtesting.SeededRandom (never used at runtime).

An independent Python port of the published C code: xoshiro256** (Blackman & Vigna 2018,
https://prng.di.unimi.it/xoshiro256starstar.c) seeded with four SplitMix64 outputs
(https://prng.di.unimi.it/splitmix64.c). The printed values are pinned in BacktestTests.cs.
Run: python3 tools/reference/xoshiro_reference.py
"""
M = (1 << 64) - 1
def rotl(x, k): return ((x << k) | (x >> (64 - k))) & M
def splitmix(x):
    x = (x + 0x9E3779B97F4A7C15) & M
    z = x
    z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & M
    z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & M
    return x, z ^ (z >> 31)
def gen(seed, count):
    x = seed; s = []
    for _ in range(4):
        x, z = splitmix(x); s.append(z)
    out = []
    for _ in range(count):
        r = (rotl((s[1] * 5) & M, 7) * 9) & M
        t = (s[1] << 17) & M
        s[2] ^= s[0]; s[3] ^= s[1]; s[1] ^= s[2]; s[0] ^= s[3]; s[2] ^= t; s[3] = rotl(s[3], 45)
        out.append(r)
    return out
for seed in (0, 42, 20260926):
    print(seed, [hex(v) for v in gen(seed, 3)])
