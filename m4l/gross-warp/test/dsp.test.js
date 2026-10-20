// The gen~ DSP (src/gross_warp.genexpr, run by tools/gensim.js) fed by envelopes the real editor wrote.
//
// Most tests feed a time code (each input sample = its own index): the output then says exactly which input sample
// was read, so speed = output slope, delay = input - output. 12 kHz keeps them fast: a bar at 120 BPM = 24000 samples.

"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { makeRig } = require("./helpers");

const SR = 12000;
const BAR = 24000;
const FADE = 48; // default Smooth 4 ms at 12 kHz

function timecode() {
    let t = 0;
    return () => {
        const v = t;
        t += 1;
        return [v, v];
    };
}

function maxAbsDiff(a, b, from, to) {
    let m = 0;
    for (let i = from; i < to; i++) {
        m = Math.max(m, Math.abs(a[i] - b[i]));
    }
    return m;
}

test("Normal passes audio through bit for bit", () => {
    const rig = makeRig({ sr: SR });
    const noise = [];
    for (let i = 0; i < 2 * BAR; i++) {
        noise.push([Math.sin(i * 0.37) * 0.8, Math.cos(i * 0.11) * 0.6]);
    }
    const out = rig.run(2 * BAR, (i) => noise[i]);
    for (let i = 0; i < 2 * BAR; i++) {
        assert.equal(out.L[i], noise[i][0]);
        assert.equal(out.R[i], noise[i][1]);
    }
    rig.gen.setParam("wet", 37);
    const mixed = rig.run(BAR, (i) => noise[i]);
    for (let i = 0; i < BAR; i++) {
        assert.ok(Math.abs(mixed.L[i] - noise[i][0]) < 1e-12);
    }
});

test("a flat time envelope is an exact delay", () => {
    const rig = makeRig({ sr: SR });
    rig.usePreset(0, 0, "Delay 1/8");
    const tc = timecode();
    const out = rig.run(2 * BAR, tc);
    for (let i = BAR / 8 + 100; i < 2 * BAR; i++) {
        assert.ok(Math.abs(out.L[i] - (i - BAR / 8)) < 1e-6, `sample ${i}: ${out.L[i]}`);
    }
});

test("Half Time plays at half speed and restarts each bar", () => {
    const rig = makeRig({ sr: SR });
    rig.gen.setParam("pattern", 1); // slot 2 = Half Time
    const out = rig.run(3 * BAR, timecode());
    for (const bar of [1, 2]) {
        const start = bar * BAR;
        for (let i = start + FADE + 2; i < start + BAR - 1; i++) {
            assert.ok(Math.abs(out.L[i + 1] - out.L[i] - 0.5) < 1e-6, `slope at ${i}`);
        }
        // the bar's first half is what gets stretched over the whole bar
        assert.ok(Math.abs(out.L[start + 12000] - (start + 6000)) < 1e-6);
    }
});

test("Reverse 1/2 plays the first half backwards in the second", () => {
    const rig = makeRig({ sr: SR });
    rig.usePreset(0, 0, "Reverse 1/2");
    const out = rig.run(2 * BAR, timecode());
    const start = BAR;
    for (let i = start + FADE + 2; i < start + BAR / 2 - 1; i++) {
        assert.equal(out.L[i], i);
    }
    for (let i = start + BAR / 2 + 2; i < start + BAR - 1; i++) {
        assert.ok(Math.abs(out.L[i + 1] - out.L[i] + 1) < 1e-6, `slope at ${i}`);
    }
    // read = t - 2 (t - start - BAR / 2): the bar's first sample comes back at its end
    assert.ok(Math.abs(out.L[start + BAR - 2] - (start + 2)) < 1e-6);
});

test("1/16 Roll repeats the sixteenth at the half bar (the screenshot's shape)", () => {
    const rig = makeRig({ sr: SR });
    rig.gen.setParam("pattern", 2); // slot 3 = 1/16 Roll
    const out = rig.run(2 * BAR, timecode());
    const start = BAR;
    const six = BAR / 16;
    const slice = start + BAR / 2; // the slice that repeats starts at the half bar
    for (let step = 8; step < 16; step++) {
        const s0 = start + step * six;
        for (let j = FADE + 2; j < six - 1; j++) {
            assert.ok(Math.abs(out.L[s0 + j] - (slice + j)) < 1e-6, `step ${step} sample ${j}: ${out.L[s0 + j]}`);
        }
    }
});

test("Tape Stop slows the second half linearly to a stop", () => {
    const rig = makeRig({ sr: SR });
    rig.gen.setParam("pattern", 3); // slot 4 = Tape Stop
    const out = rig.run(2 * BAR, timecode());
    const half = BAR + BAR / 2;
    for (const f of [0.1, 0.25, 0.5, 0.75, 0.95]) {
        const i = Math.round(half + f * (BAR / 2));
        const speed = out.L[i + 1] - out.L[i];
        assert.ok(Math.abs(speed - (1 - f)) < 0.002, `f=${f}: speed ${speed}`);
    }
});

test("a gate silences the off steps and leaves the on steps", () => {
    const rig = makeRig({ sr: SR });
    rig.gen.setParam("pattern", 5); // slot 6 = Normal + Gate 1/16
    const out = rig.run(2 * BAR, () => [1, 1]);
    const cell = BAR / 32;
    for (let c = 0; c < 32; c++) {
        const mid = BAR + c * cell + cell / 2;
        if (c % 2 === 0) {
            assert.ok(Math.abs(out.L[mid] - 1) < 1e-4, `on cell ${c}: ${out.L[mid]}`);
        } else {
            assert.ok(Math.abs(out.L[mid]) < 1e-4, `off cell ${c}: ${out.L[mid]}`);
        }
    }
});

test("with the transport stopped the device passes audio through", () => {
    const rig = makeRig({ sr: SR });
    rig.gen.setParam("pattern", 1);
    const tc = timecode();
    const held = rig.run(BAR, tc, false);
    for (let i = 0; i < BAR; i++) {
        assert.equal(held.L[i], i);
        assert.equal(held.phase[i], -1);
    }
    // playing, then stopping: back to pass-through once the stop is noticed and the fade is done
    const tc2 = timecode();
    rig.run(BAR, tc2);
    const stopped = rig.run(BAR, tc2, false);
    for (let i = 4096 + FADE + 4; i < BAR; i++) {
        assert.ok(Math.abs(stopped.L[i] - (BAR + i)) < 1e-6, `sample ${i}`);
    }
});

test("Depth 0 leaves the audio untouched", () => {
    const rig = makeRig({ sr: SR });
    rig.gen.setParam("pattern", 7); // Scratch + Pump
    rig.gen.setParam("depth", 0);
    const out = rig.run(BAR, timecode());
    for (let i = 0; i < BAR; i++) {
        assert.equal(out.L[i], i);
    }
});

function switchAt(quant, length, from) {
    const rig = makeRig({ sr: SR });
    rig.gen.setParam("quant", quant);
    rig.gen.setParam("patlen", length);
    const out = rig.run(2 * BAR, (i) => {
        if (i === from) {
            rig.gen.setParam("pattern", 3);
        }
        return [0, 0];
    });
    return out.active.indexOf(3);
}

test("pattern changes wait for the Switch grid", () => {
    assert.equal(switchAt(0, 2, 10000), 10000); // Instant
    assert.equal(switchAt(1, 2, 10000), 10500); // next 1/16
    assert.equal(switchAt(2, 2, 10000), 12000); // next beat
    assert.equal(switchAt(3, 2, 10000), 24000); // end of the 1-bar pattern
    assert.equal(switchAt(3, 0, 10000), 12000); // end of a 1-beat pattern
    assert.equal(switchAt(3, 1, 13000), 24000); // end of a 2-beat pattern
});

test("a 1-beat pattern repeats every beat", () => {
    const rig = makeRig({ sr: SR });
    rig.gen.setParam("pattern", 1); // Half Time
    rig.gen.setParam("patlen", 0);
    const out = rig.run(2 * BAR, timecode());
    const beat = BAR / 4;
    for (let b = 4; b < 8; b++) {
        const s = b * beat;
        assert.ok(Math.abs(out.L[s + 3000] - (s + 1500)) < 1e-6, `beat ${b}`);
    }
});

function maxStep(rig, smoothMs, freq) {
    rig.gen.setParam("smoothms", smoothMs);
    const w = (2 * Math.PI * freq) / SR;
    const out = rig.run(2 * BAR, (i) => [Math.sin(w * i), Math.sin(w * i)]);
    let m = 0;
    for (let i = BAR + 1; i < 2 * BAR; i++) {
        m = Math.max(m, Math.abs(out.L[i] - out.L[i - 1]));
    }
    return m;
}

test("steps in the time envelope cross-fade instead of clicking", () => {
    const smooth = makeRig({ sr: SR });
    smooth.gen.setParam("pattern", 2); // 1/16 Roll: a jump every sixteenth in the second half
    const natural = (2 * Math.PI * 50) / SR;
    assert.ok(maxStep(smooth, 4, 50) < natural + 0.07, "with the default 4 ms fade");
    const hard = makeRig({ sr: SR });
    hard.gen.setParam("pattern", 2);
    assert.ok(maxStep(hard, 0.1, 50) > 0.3, "the same jumps without a fade do click");
});

test("the waveform peaks reach the editor's buffer", () => {
    const rig = makeRig({ sr: SR });
    const w = (2 * Math.PI * 480) / SR;
    rig.run(2 * BAR, (i) => [0.5 * Math.sin(w * i), 0.25 * Math.sin(w * i)]);
    const peaks = rig.buffers.get("101gwpeaks").data[0];
    for (let b = 1; b < 255; b++) {
        assert.ok(Math.abs(peaks[b] - 0.5) < 0.01, `input bin ${b}: ${peaks[b]}`);
        assert.ok(Math.abs(peaks[256 + b] - 0.5) < 0.01, `output bin ${b}: ${peaks[256 + b]}`);
    }
});

test("gen~ evaluates every envelope exactly as the editor draws it", () => {
    const rig = makeRig({ sr: SR, exposeLocals: true });
    const ctx = rig.ui.ctx;
    let seed = 12345;
    const rnd = () => {
        seed = (seed * 1103515245 + 12345) % 2147483648;
        return seed / 2147483648;
    };
    for (let trial = 0; trial < 40; trial++) {
        const pts = [];
        const n = 2 + Math.floor(rnd() * 30);
        for (let i = 0; i < n; i++) {
            // a third of the points share an x with the one before (steps)
            const x = i > 0 && rnd() < 0.33 ? pts[i - 1].x : Math.round(rnd() * 64) / 64;
            pts.push({ x, u: rnd(), c: rnd() < 0.5 ? 0 : rnd() * 2 - 1 });
        }
        const env = ctx.cleanEnv(pts);
        const env2 = ctx.cleanEnv(pts.map((p) => ({ x: p.x, u: 1 - p.u, c: -p.c })));
        rig.setEnv(0, 0, env);
        rig.setEnv(0, 1, env2);
        const time = ctx.S.pats[0][0];
        const vol = ctx.S.pats[0][1];
        for (let k = 0; k < 200; k++) {
            // phases out of order on purpose: the segment cache must recover from any jump
            const ph = k % 50 === 0 ? time[Math.floor(rnd() * time.length)].x : rnd();
            rig.gen.tick(0, 0, ph);
            const s = rig.gen.state;
            assert.ok(Math.abs(s.uT - ctx.evalEnv(time, s.patPh)) < 1e-12, `time trial ${trial} phase ${ph}`);
            assert.ok(Math.abs(s.uV - ctx.evalEnv(vol, s.patPh)) < 1e-12, `volume trial ${trial} phase ${ph}`);
        }
    }
});

test("without the editor's buffer gen~ stays neutral", () => {
    const { createGen } = require("../tools/gensim");
    const { GEN_SRC } = require("./helpers");
    const gen = createGen(GEN_SRC, { samplerate: SR });
    for (let i = 0; i < BAR; i++) {
        const o = gen.tick(i, -i - 1, (i % BAR) / BAR);
        assert.equal(o[0], i);
        assert.equal(o[1], -i - 1);
    }
});
