// Test rig: the real jsui script under stubs of Max's globals, and the real gen~ code in the simulator, sharing the
// envelope and peak buffers the way the device does.

"use strict";

const fs = require("fs");
const path = require("path");
const vm = require("vm");
const { createGen, makeStore } = require("../tools/gensim");

const ROOT = path.resolve(__dirname, "..");
const UI_PATH = path.join(ROOT, "src", "gw_ui.js");
const GEN_PATH = path.join(ROOT, "src", "gross_warp.genexpr");
const UI_SRC = fs.readFileSync(UI_PATH, "utf8");
const GEN_SRC = fs.readFileSync(GEN_PATH, "utf8");
const ENV_FRAMES = 8 * 2 * 385;
const PEAK_FRAMES = 512;

// Values made inside the vm context have that context's prototypes; compare plain copies.
function plain(v) {
    return JSON.parse(JSON.stringify(v));
}

// An mgraphics stand-in that records every call.
function recorder(size) {
    const calls = [];
    const target = { size, calls };
    return new Proxy(target, {
        get(t, key) {
            if (key in t) {
                return t[key];
            }
            return (...args) => {
                calls.push([key, ...args]);
                if (key === "text_measure") {
                    return [String(args[0]).length * 5, 9];
                }
                return undefined;
            };
        },
        set(t, key, value) {
            t[key] = value;
            return true;
        },
    });
}

// Max's JS Buffer over named stores; `chanBase` is how this fake numbers channels (Max: 1).
function makeMaxBuffers(chanBase) {
    const stores = new Map();
    function get(name) {
        if (!stores.has(name)) {
            stores.set(name, makeStore(1, 1));
        }
        return stores.get(name);
    }
    function create(name, frames, chans) {
        stores.set(name, makeStore(frames, chans || 1));
        return stores.get(name);
    }
    class MaxBuffer {
        constructor(name) {
            this.name = String(name);
        }
        store() {
            return get(this.name);
        }
        chan(c) {
            const i = c - chanBase;
            if (i < 0 || i >= this.store().chans) {
                throw new Error(`buffer~ ${this.name}: no channel ${c}`);
            }
            return i;
        }
        framecount() {
            return this.store().frames;
        }
        channelcount() {
            return this.store().chans;
        }
        peek(ch, frame, count) {
            const s = this.store();
            const c = this.chan(ch);
            const n = count || 1;
            const out = [];
            for (let i = 0; i < n; i++) {
                out.push(frame + i < s.frames ? s.data[c][frame + i] : 0);
            }
            return n === 1 ? out[0] : out;
        }
        poke(ch, frame, values) {
            const s = this.store();
            const c = this.chan(ch);
            const list = Array.isArray(values) ? values : [values];
            list.forEach((v, i) => {
                if (frame + i < s.frames) {
                    s.data[c][frame + i] = v;
                }
            });
        }
        send(msg, n) {
            if (msg === "sizeinsamps") {
                // resize in place, so that gen~'s binding to the store stays valid
                const s = this.store();
                s.data = s.data.map((old) => {
                    const a = new Float64Array(n);
                    a.set(old.subarray(0, Math.min(n, old.length)));
                    return a;
                });
                s.frames = n;
            }
        }
    }
    return { MaxBuffer, stores, get, create };
}

function loadUi(options) {
    const opts = Object.assign({ chanBase: 1, size: [404, 140] }, options);
    const buffers = opts.buffers || makeMaxBuffers(opts.chanBase);
    const globals = opts.globals || new Map();
    const mg = recorder(opts.size);
    const notified = { count: 0 };
    const ctx = {
        mgraphics: mg,
        Buffer: buffers.MaxBuffer,
        post() {},
        notifyclients() {
            notified.count += 1;
        },
        arrayfromargs: (args) => Array.prototype.slice.call(args),
        Global: function Global(name) {
            if (!globals.has(name)) {
                globals.set(name, {});
            }
            return globals.get(name);
        },
        box: { rect: [0, 0, opts.size[0], opts.size[1]] },
        jsarguments: [],
    };
    vm.createContext(ctx);
    vm.runInContext(UI_SRC, ctx, { filename: "gw_ui.js" });
    return { ctx, mg, buffers, notified, globals };
}

// The device: buffers sized first, then their names handed to gen~ and the editor (the patcher's load order).
// The transport feeds gen~ a quarter-note phase, like [phasor~ 4n @lock 1]; `startBeat` starts the song elsewhere.
function makeRig(options) {
    const opts = Object.assign({ sr: 48000, bpm: 120, chanBase: 1, signum: 4, sigden: 4, startBeat: 0 }, options);
    const buffers = makeMaxBuffers(opts.chanBase);
    buffers.create("101gwenv", ENV_FRAMES, 1);
    buffers.create("101gwpeaks", PEAK_FRAMES, 1);
    const ui = loadUi({ buffers, chanBase: opts.chanBase });
    const gen = createGen(GEN_SRC, { samplerate: opts.sr, exposeLocals: opts.exposeLocals });
    gen.bindBuffer("gwenv", buffers.get("101gwenv"));
    gen.bindBuffer("gwpeaks", buffers.get("101gwpeaks"));
    gen.setParam("signum", opts.signum);
    gen.setParam("sigden", opts.sigden);
    ui.ctx.envbuf("101gwenv");
    ui.ctx.peakbuf("101gwpeaks");
    ui.ctx.signum(opts.signum);
    ui.ctx.sigden(opts.sigden);
    const beatSamples = Math.round((opts.sr * 60) / opts.bpm);
    const barSamples = Math.round((beatSamples * opts.signum * 4) / opts.sigden);
    let clock = Math.round(opts.startBeat * beatSamples); // transport position in samples

    // Run n samples; input(i) -> [L, R]; playing: transport running (the phase moves) or held.
    function run(n, input, playing) {
        const out = { L: new Float64Array(n), R: new Float64Array(n), phase: new Float64Array(n), active: [] };
        for (let i = 0; i < n; i++) {
            const beat = (clock % beatSamples) / beatSamples;
            const [l, r] = input(i);
            const o = gen.tick(l, r, beat);
            out.L[i] = o[0];
            out.R[i] = o[1];
            out.phase[i] = o[2];
            out.active.push(o[3]);
            if (playing !== false) {
                clock += 1;
            }
        }
        return out;
    }

    // Put envelopes into a pattern slot, as the editor does.
    function setEnv(p, e, pts) {
        ui.ctx.S.pats[p][e] = ui.ctx.cleanEnv(pts);
        ui.ctx.writeEnv(p, e);
    }

    function usePreset(p, e, name) {
        const i = ui.ctx.presetIndex(e, name);
        if (i < 0) {
            throw new Error(`no preset ${name}`);
        }
        ui.ctx.S.pats[p][e] = ui.ctx.makePreset(e, i);
        ui.ctx.writeEnv(p, e);
    }

    return {
        ui,
        gen,
        buffers,
        run,
        setEnv,
        usePreset,
        beatSamples,
        barSamples,
        sr: opts.sr,
        clock: () => clock,
    };
}

module.exports = { ROOT, UI_SRC, GEN_SRC, ENV_FRAMES, PEAK_FRAMES, plain, recorder, makeMaxBuffers, loadUi, makeRig };
