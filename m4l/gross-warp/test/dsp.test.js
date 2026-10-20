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

function switchAt(quant, length, from, samples) {
    const rig = makeRig({ sr: SR });
    rig.gen.setParam("quant", quant);
    rig.gen.setParam("patlen", length);
    const out = rig.run(samples || 2 * BAR, (i) => {
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
    assert.equal(switchAt(3, 2, 10000), 24000); // next bar
    assert.equal(switchAt(4, 2, 10000), 24000); // end of the 1-bar pattern
    assert.equal(switchAt(4, 0, 10000), 12000); // end of a 1-beat pattern
    assert.equal(switchAt(4, 1, 13000), 24000); // end of a 2-beat pattern
    assert.equal(switchAt(4, 3, 10000, 3 * BAR), 2 * BAR); // end of a 2-bar pattern
    assert.equal(switchAt(4, 4, 10000, 5 * BAR), 4 * BAR); // end of a 4-bar pattern
    assert.equal(switchAt(3, 4, 10000), 24000); // Bar does not wait for a long pattern
});

test("a change just after a grid line still takes that line", () => {
    assert.equal(switchAt(3, 2, BAR + 200), BAR + 200, "16 ms late: switches at once");
    assert.equal(switchAt(3, 2, BAR + 800, 3 * BAR), 2 * BAR, "67 ms late: waits for the next bar");
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

test("2-bar and 4-bar patterns stretch over their bars", () => {
    for (const [len, bars] of [
        [3, 2],
        [4, 4],
    ]) {
        const rig = makeRig({ sr: SR });
        rig.gen.setParam("pattern", 1); // Half Time over the whole pattern
        rig.gen.setParam("patlen", len);
        const span = bars * BAR;
        const out = rig.run(2 * span, timecode());
        for (const f of [0.25, 0.5, 0.75]) {
            const i = span + Math.round(f * span);
            // half speed from the pattern's start
            assert.ok(Math.abs(out.L[i] - (span + (f * span) / 2)) < 1e-5, `${bars} bars at ${f}: ${out.L[i]}`);
        }
    }
});

test("time signatures: a bar of 3/4, 6/8 and 7/8", () => {
    for (const [num, den, quarters] of [
        [3, 4, 3],
        [6, 8, 3],
        [7, 8, 3.5],
    ]) {
        const rig = makeRig({ sr: SR, signum: num, sigden: den });
        rig.gen.setParam("pattern", 1); // Half Time, 1 bar
        const bar = (quarters * BAR) / 4;
        assert.equal(rig.barSamples, bar);
        const out = rig.run(3 * bar, timecode());
        for (const b of [1, 2]) {
            const i = b * bar + bar / 2;
            assert.ok(Math.abs(out.L[i] - (b * bar + bar / 4)) < 1e-5, `${num}/${den} bar ${b}: ${out.L[i]}`);
        }
        assert.equal(switchAtMeter(num, den, 1000), bar, `${num}/${den}: Switch Bar lands on the bar`);
    }
});

function switchAtMeter(num, den, from) {
    const rig = makeRig({ sr: SR, signum: num, sigden: den });
    rig.gen.setParam("quant", 3);
    const out = rig.run(2 * BAR, (i) => {
        if (i === from) {
            rig.gen.setParam("pattern", 3);
        }
        return [0, 0];
    });
    return out.active.indexOf(3);
}

test("Live's song position puts a pattern in the right bar, even when it arrives late", () => {
    // the transport starts at beat 13 (bar 4, beat 2); without the song position gen~ would count from 0
    const rig = makeRig({ sr: SR, startBeat: 13, exposeLocals: true });
    rig.gen.setParam("patlen", 3); // 2 bars = 8 beats
    rig.run(100, () => [0, 0]);
    assert.equal(rig.gen.state.songPos < 1, true, "no song position yet");
    rig.gen.setParam("songbeats", 12.97); // measured a little before, delivered now
    rig.run(1, () => [0, 0]);
    const pos = rig.clock() / (BAR / 4);
    assert.ok(Math.abs(rig.gen.state.songPos - (pos - 1 / (BAR / 4))) < 1e-9, `${rig.gen.state.songPos} vs ${pos}`);
    assert.ok(Math.abs(rig.gen.state.patPh - (rig.gen.state.songPos / 8 - 1)) < 1e-9);
    // a late value just after a beat line still rounds to the right beat
    rig.run(Math.round(3.02 * (BAR / 4)) - 101, () => [0, 0]);
    const now = rig.clock() / (BAR / 4);
    rig.gen.setParam("songbeats", now - 0.04);
    rig.run(1, () => [0, 0]);
    assert.ok(Math.abs(rig.gen.state.songPos - now) < 1e-6, `${rig.gen.state.songPos} vs ${now}`);
});

function frequency(signal, from, to) {
    let crossings = 0;
    for (let i = from + 1; i < to; i++) {
        if (signal[i - 1] < 0 && signal[i] >= 0) {
            crossings++;
        }
    }
    return (crossings * SR) / (to - from);
}

function sineRig(pattern, lock) {
    const rig = makeRig({ sr: SR });
    rig.gen.setParam("pattern", pattern);
    rig.gen.setParam("pitchlock", lock ? 1 : 0);
    const w = (2 * Math.PI * 480) / SR;
    return { rig, out: rig.run(2 * BAR, (i) => [Math.sin(w * i), Math.sin(w * i)]) };
}

test("pitch lock: Half Time keeps the pitch (without it the pitch drops an octave)", () => {
    const plain = sineRig(1, false).out;
    const locked = sineRig(1, true).out;
    const from = BAR + 2000;
    const to = 2 * BAR - 2000;
    assert.ok(Math.abs(frequency(plain.L, from, to) - 240) < 5, `plain ${frequency(plain.L, from, to)}`);
    assert.ok(Math.abs(frequency(locked.L, from, to) - 480) < 8, `locked ${frequency(locked.L, from, to)}`);
});

test("pitch lock: reverse and tape stop keep the pitch; grains run backwards when reversing", () => {
    const rev = makeRig({ sr: SR, exposeLocals: true });
    rev.usePreset(0, 0, "Reverse 1/2");
    rev.gen.setParam("pitchlock", 1);
    const w = (2 * Math.PI * 480) / SR;
    const out = rev.run(BAR + BAR * 0.8, (i) => [Math.sin(w * i), Math.sin(w * i)]);
    assert.equal(rev.gen.state.gDA, -1, "grains read backwards");
    const half = BAR + BAR / 2;
    assert.ok(Math.abs(frequency(out.L, half + 1000, BAR + BAR * 0.8 - 10) - 480) < 8);
    const stop = sineRig(3, true).out; // Tape Stop: speed falls to 0 over the second half
    const end = 2 * BAR - 1;
    assert.ok(Math.abs(frequency(stop.L, end - 3000, end) - 480) < 8, "still the same pitch near the stop");
    let rms = 0;
    for (let i = end - 3000; i < end; i++) {
        rms += stop.L[i] * stop.L[i];
    }
    assert.ok(Math.sqrt(rms / 3000) > 0.5, "and still sounding");
});

test("pitch lock: Normal still passes audio through, and toggling it does not click", () => {
    const rig = makeRig({ sr: SR });
    rig.gen.setParam("pitchlock", 1);
    const out = rig.run(BAR, timecode());
    for (let i = 0; i < BAR; i++) {
        assert.ok(Math.abs(out.L[i] - i) < 1e-6, `sample ${i}: ${out.L[i]}`);
    }
    const toggled = makeRig({ sr: SR });
    toggled.gen.setParam("pattern", 1);
    const w = (2 * Math.PI * 50) / SR;
    const o = toggled.run(2 * BAR, (i) => {
        if (i === BAR + 5000) {
            toggled.gen.setParam("pitchlock", 1);
        }
        return [Math.sin(w * i), 0];
    });
    let m = 0;
    for (let i = BAR + 100; i < 2 * BAR - 100; i++) {
        m = Math.max(m, Math.abs(o.L[i] - o.L[i - 1]));
    }
    assert.ok(m < 0.1, `largest step ${m}`);
});

test("pitch lock: rolls still cross-fade without clicks", () => {
    const rig = makeRig({ sr: SR });
    rig.gen.setParam("pattern", 2);
    rig.gen.setParam("pitchlock", 1);
    const natural = (2 * Math.PI * 50) / SR;
    assert.ok(maxStep(rig, 4, 50) < natural + 0.07);
});

// v = pattern + 8 * parity + 16 * mode, as Gross Warp MIDI sends it
function midiRig() {
    const rig = makeRig({ sr: SR });
    rig.gen.setParam("quant", 0);
    rig.gen.setParam("midien", 1);
    const parity = { trigon: 0, trigoff: 0 };
    let last = [0];
    function note(kind, n, mode) {
        parity[kind] = 1 - parity[kind];
        rig.gen.setParam(kind, n + 8 * parity[kind] + 16 * (kind === "trigon" ? mode : 0));
        last = rig.run(10, () => [0, 0]).active;
        return last[last.length - 1];
    }
    return { rig, on: (n, mode) => note("trigon", n, mode || 0), off: (n) => note("trigoff", n, 0) };
}

test("MIDI hold: a pattern plays while its note is down", () => {
    const m = midiRig();
    m.rig.gen.setParam("pattern", 1);
    assert.equal(m.on(3), 3);
    assert.equal(m.on(5), 5, "a second note takes over");
    assert.equal(m.off(3), 5, "releasing the first changes nothing");
    assert.equal(m.off(5), 1, "back to the Pattern parameter");
    assert.equal(m.on(5), 5, "the same note again is seen");
    assert.equal(m.off(5), 1);
});

test("MIDI latch: a note switches, the same note again releases", () => {
    const m = midiRig();
    assert.equal(m.on(2, 1), 2);
    assert.equal(m.off(2), 2, "note-offs do nothing");
    assert.equal(m.on(4, 1), 4);
    assert.equal(m.on(4, 1), 0, "released");
    assert.equal(m.on(6, 1), 6);
    m.rig.gen.setParam("midien", 0);
    assert.equal(m.rig.run(10, () => [0, 0]).active[9], 0, "turning MIDI off clears it");
    assert.equal(m.on(7, 1), 0, "and ignores notes");
});

test("MIDI triggers follow the Switch grid", () => {
    const m = midiRig();
    m.rig.gen.setParam("quant", 2); // Beat
    const start = m.rig.clock() + 9960; // the note arrives at sample 9970, between beats 1 and 2
    m.rig.run(9960, () => [0, 0]);
    m.rig.gen.setParam("trigon", 3);
    const out = m.rig.run(4000, () => [0, 0]);
    assert.equal(start + out.active.indexOf(3), 12000);
});
