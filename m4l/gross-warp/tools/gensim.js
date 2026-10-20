// A simulator for the subset of GenExpr that gross_warp.genexpr uses, so the DSP can be tested without Max.
//
// It runs the codebox text itself (not a re-implementation): declarations become state, functions and the body
// become JavaScript run once per sample. Semantics follow gen~ where the file relies on them: History persists
// between samples, Params are set by name, Data/Buffer are multichannel arrays, peek/poke are 0-based on channel
// and ignore out-of-range indices. It is a test tool: it does not check gen~ syntax (test/lint.test.js does that).

"use strict";

const BUILTINS = {
    floor: Math.floor,
    abs: Math.abs,
    pow: Math.pow,
    exp: Math.exp,
    sin: Math.sin,
    cos: Math.cos,
    min: Math.min,
    max: Math.max,
    fract: (x) => x - Math.floor(x),
    clamp: (x, lo, hi) => Math.min(Math.max(x, lo), hi),
};

function makeStore(frames, chans) {
    const data = [];
    for (let c = 0; c < chans; c++) {
        data.push(new Float64Array(frames));
    }
    return { frames, chans, data };
}

function stripComments(src) {
    return src.replace(/\/\/[^\n]*/g, "");
}

// Identifiers assigned with "=" (not "==", "<=", ">=", "!=") in a piece of code.
function assignedNames(code) {
    const names = new Set();
    const re = /(^|[^=!<>\w.])([A-Za-z_]\w*)\s*=(?!=)/g;
    let m;
    while ((m = re.exec(code)) !== null) {
        names.add(m[2]);
    }
    return names;
}

function matchBrace(src, open) {
    let depth = 0;
    for (let i = open; i < src.length; i++) {
        if (src[i] === "{") {
            depth++;
        } else if (src[i] === "}") {
            depth--;
            if (depth === 0) {
                return i;
            }
        }
    }
    throw new Error("gensim: unbalanced braces");
}

function parse(source) {
    let src = stripComments(source).trim();
    const functions = [];
    const fnHead = /^([A-Za-z_]\w*)\s*\(([^)]*)\)\s*\{/;
    for (;;) {
        const m = fnHead.exec(src);
        if (!m || ["if", "for", "while"].includes(m[1])) {
            break;
        }
        const open = src.indexOf("{", m.index);
        const close = matchBrace(src, open);
        functions.push({
            name: m[1],
            args: m[2].split(",").map((s) => s.trim()).filter(Boolean),
            body: src.slice(open + 1, close),
        });
        src = src.slice(close + 1).trim();
    }
    const decls = [];
    const declRe = /^(Buffer|Data|Param|History)\s+([A-Za-z_]\w*)\s*\(([^)]*)\)\s*;/;
    for (;;) {
        const m = declRe.exec(src);
        if (!m) {
            break;
        }
        decls.push({ kind: m[1], name: m[2], args: m[3] });
        src = src.slice(m[0].length).trim();
    }
    return { functions, decls, body: src };
}

function createGen(source, options) {
    const opts = Object.assign({ samplerate: 48000 }, options);
    const parsed = parse(source);
    const S = Object.create(null);
    const params = new Set();
    const buffers = new Map();
    const historyInit = new Map();

    Object.assign(S, BUILTINS);
    S.samplerate = opts.samplerate;
    S.mstosamps = (ms) => (ms * S.samplerate) / 1000;
    S.dim = (store) => (store ? store.frames : 0);
    S.peek = (store, index, chan) => {
        if (!store) {
            return 0;
        }
        const i = Math.floor(index);
        const c = Math.floor(chan || 0);
        if (i < 0 || i >= store.frames || c < 0 || c >= store.chans) {
            return 0;
        }
        return store.data[c][i];
    };
    S.poke = (store, value, index, chan) => {
        if (!store) {
            return;
        }
        const i = Math.floor(index);
        const c = Math.floor(chan || 0);
        if (i >= 0 && i < store.frames && c >= 0 && c < store.chans) {
            store.data[c][i] = value;
        }
    };

    for (const d of parsed.decls) {
        const args = d.args.split(",").map((s) => s.trim()).filter(Boolean);
        if (d.kind === "Data") {
            S[d.name] = makeStore(Number(args[0]), Number(args[1] || 1));
        } else if (d.kind === "Buffer") {
            buffers.set(d.name, JSON.parse(args[0]));
            S[d.name] = null; // unbound until bindBuffer, like a buffer~ that does not exist yet
        } else if (d.kind === "Param") {
            S[d.name] = Number(args[0] || 0);
            params.add(d.name);
        } else {
            S[d.name] = Number(args[0] || 0);
            historyInit.set(d.name, S[d.name]);
        }
    }

    for (const f of parsed.functions) {
        const locals = [...assignedNames(f.body)].filter((n) => !f.args.includes(n));
        const decl = locals.length ? `var ${locals.join(", ")};` : "";
        // eslint-disable-next-line no-new-func
        const fn = new Function("S", ...f.args, `${decl}\nwith (S) {\n${f.body}\n}`);
        S[f.name] = (...a) => fn(S, ...a);
    }

    for (const n of ["in1", "in2", "in3", "in4", "out1", "out2", "out3", "out4"]) {
        S[n] = 0;
    }
    const locals = [...assignedNames(parsed.body)].filter((n) => !(n in S));
    // exposeLocals: keep the body's locals on S so a test can read them after a tick (the code sets every local
    // before reading it, so keeping them between samples changes nothing).
    if (opts.exposeLocals) {
        for (const n of locals) {
            S[n] = 0;
        }
    }
    const declared = opts.exposeLocals || locals.length === 0 ? "" : `var ${locals.join(", ")};`;
    // Sloppy-mode function so that `with` is allowed: names in S (state, params, builtins, in/out) resolve there,
    // everything else is a per-sample local.
    // eslint-disable-next-line no-new-func
    const tickFn = new Function("S", `${declared}\nwith (S) {\n${parsed.body}\n}`);

    return {
        state: S,
        params,
        bufferNames: buffers,
        setParam(name, value) {
            if (!params.has(name)) {
                throw new Error(`gensim: no Param ${name}`);
            }
            S[name] = value;
        },
        // The gen~ message "<Buffer name> <buffer~ name>": point a Buffer at a store.
        bindBuffer(name, store) {
            if (!buffers.has(name)) {
                throw new Error(`gensim: no Buffer ${name}`);
            }
            S[name] = store;
        },
        reset() {
            for (const [n, v] of historyInit) {
                S[n] = v;
            }
        },
        tick(in1, in2, in3) {
            S.in1 = in1;
            S.in2 = in2;
            S.in3 = in3;
            tickFn(S);
            return [S.out1, S.out2, S.out3, S.out4];
        },
    };
}

module.exports = { createGen, makeStore, parse };
