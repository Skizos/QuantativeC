// Rules the sources must keep because Max cannot be run here: gen~'s codebox compiler is strict (and silent about
// some mistakes), and Max's js/jsui engine is ES5.

"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { parse } = require("../tools/gensim");
const { UI_SRC, GEN_SRC } = require("./helpers");

const body = parse(GEN_SRC).body;
const decls = parse(GEN_SRC).decls;
const bodyLines = body.split("\n");

// Names gen~ already uses (operators and constants); a variable with one of them shadows or fails to compile.
const GEN_NAMES = (
    "abs absdiff accum acos acosh add and asin asinh atan atan2 atanh atodb bool buffer cartopol ceil change " +
    "channels clamp clip codebox constant cos cosh counter cycle data dbtoa dcblock degrees degtorad delay delta " +
    "dim div e elapsed eq exp exp2 expr f fastcos fastexp fastpow fastsin fasttan fixdenorm float floor fold fract " +
    "ftom gate gen gt gte halfpi history hypot i in int interp invpi isdenorm isnan latch length ln ln10 ln2 log " +
    "log10 log10e log2 log2e lookup lt lte max maximum min minimum mix mod mstosamps mtof mul mulequals nearest neg " +
    "neq noise not or out param pass peek phasor pi plusequals poke poltocar pow radians radtodeg rate rdiv " +
    "rmod round rsub sah sample samplerate sampstoms scale selector sign sin sinh slide smoothstep spike sqrt sqrt1_2 " +
    "sqrt2 step sub switch t60 t60time tan tanh train triangle trunc twopi vectorsize voice wave wrap xor"
).split(" ");

function assignments() {
    const out = [];
    let depth = 0;
    bodyLines.forEach((line, n) => {
        const re = /(^|[^=!<>\w.])([A-Za-z_]\w*)\s*=(?!=)/g;
        let m;
        while ((m = re.exec(line)) !== null) {
            out.push({ name: m[2], line: n, depth });
        }
        depth += (line.match(/\{/g) || []).length - (line.match(/\}/g) || []).length;
    });
    return out;
}

test("gen~: peek only as the whole right-hand side of an assignment", () => {
    const code = GEN_SRC.replace(/\/\/[^\n]*/g, "");
    const uses = code.split("\n").filter((l) => l.includes("peek("));
    assert.ok(uses.length > 20);
    for (const l of uses) {
        assert.match(l.trim(), /^[A-Za-z_]\w* = peek\([^()]*\);$/, l.trim());
    }
});

test("gen~: no break, continue, compound assignment, increment or else-if", () => {
    const code = GEN_SRC.replace(/\/\/[^\n]*/g, "");
    for (const bad of [/\bbreak\b/, /\bcontinue\b/, /[+\-*/%]=/, /\+\+/, /--/, /else\s+if/, /\bwhile\b/]) {
        assert.doesNotMatch(code, bad);
    }
});

test("gen~: no variable takes the name of a gen~ operator or constant", () => {
    const declared = decls.map((d) => d.name);
    for (const a of assignments()) {
        assert.ok(!GEN_NAMES.includes(a.name), `${a.name} (line ${a.line})`);
    }
    for (const n of declared) {
        assert.ok(!GEN_NAMES.includes(n), n);
    }
});

test("gen~: every variable is first set at top level", () => {
    const declared = new Set(decls.map((d) => d.name).concat(["out1", "out2", "out3", "out4"]));
    const firstTop = new Map();
    for (const a of assignments()) {
        if (a.depth === 0 && !firstTop.has(a.name)) {
            firstTop.set(a.name, a.line);
        }
    }
    for (const a of assignments()) {
        if (a.depth > 0 && !declared.has(a.name)) {
            assert.ok(firstTop.has(a.name) && firstTop.get(a.name) < a.line, `${a.name} first set in a block (line ${a.line})`);
        }
    }
});

test("gen~: History is read once at the top and written once at the end", () => {
    const histories = decls.filter((d) => d.kind === "History").map((d) => d.name);
    assert.ok(histories.length > 5);
    let lastRead = -1;
    let firstWrite = Infinity;
    for (const h of histories) {
        const at = bodyLines
            .map((l, n) => ({ l: l.trim(), n }))
            .filter((x) => new RegExp(`\\b${h}\\b`).test(x.l));
        assert.equal(at.length, 2, `${h}: ${at.map((x) => x.l).join(" | ")}`);
        assert.match(at[0].l, new RegExp(`^\\w+ = ${h};$`));
        assert.match(at[1].l, new RegExp(`^${h} = `));
        lastRead = Math.max(lastRead, at[0].n);
        firstWrite = Math.min(firstWrite, at[1].n);
    }
    assert.ok(lastRead < firstWrite, "all reads come before all writes");
});

test("sources are ASCII (Max's text handling, the codebox included)", () => {
    for (const [name, src] of [
        ["gross_warp.genexpr", GEN_SRC],
        ["gw_ui.js", UI_SRC],
    ]) {
        // eslint-disable-next-line no-control-regex
        assert.ok(/^[\x09\x0a\x0d\x20-\x7e]*$/.test(src), name);
    }
});

test("gw_ui.js is ES5", () => {
    const code = UI_SRC.replace(/\/\/[^\n]*/g, "").replace(/"(?:[^"\\\n]|\\.)*"/g, '""');
    const banned = [
        [/\b(let|const|class)\s/, "let/const/class"],
        [/=>/, "arrow function"],
        [/`/, "template string"],
        [/\.\.\./, "spread/rest"],
        [/\bfor\s*\([^)]*\bof\b/, "for...of"],
        // (Array fill is ES6 too, but mgraphics has its own fill(), so it cannot be told apart here.)
        [/\.(includes|startsWith|endsWith|padStart|padEnd|find|findIndex|repeat)\(/, "ES6 method"],
        [/\b(Object\.assign|Array\.from|Array\.of|Math\.(sign|log2|log10|trunc|cbrt|hypot)|Number\.is\w+)\b/, "ES6 API"],
        [/\b(Symbol|Promise|Map|Set|WeakMap|Proxy|Reflect)\b/, "ES6 global"],
    ];
    for (const [re, what] of banned) {
        assert.doesNotMatch(code, re, what);
    }
});
