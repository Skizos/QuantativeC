// The jsui editor (src/gw_ui.js) under stubs of Max's globals: envelope math, presets, persistence, the buffer it
// writes for gen~, mouse editing, the toolbar, and that painting only uses real mgraphics calls.

"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { plain, loadUi, makeMaxBuffers, ENV_FRAMES } = require("./helpers");

// Layout at the device's 404 x 140: display 404 x 100, envelope area x 5..399, y 14..95.
const toX = (x) => 5 + x * 394;
const toY = (u) => 14 + u * 81;

function checkEnv(ctx, pts, label) {
    assert.ok(pts.length >= 2 && pts.length <= ctx.MAX_PTS, `${label}: ${pts.length} points`);
    assert.equal(pts[0].x, 0, `${label}: starts at 0`);
    assert.equal(pts[pts.length - 1].x, 1, `${label}: ends at 1`);
    for (let i = 0; i < pts.length; i++) {
        const p = pts[i];
        assert.ok(p.u >= 0 && p.u <= 1 && p.c >= -1 && p.c <= 1, `${label}: point ${i} in range`);
        if (i > 0) {
            assert.ok(p.x >= pts[i - 1].x, `${label}: sorted at ${i}`);
        }
        if (i > 1) {
            assert.ok(!(p.x === pts[i - 1].x && p.x === pts[i - 2].x), `${label}: three points at x=${p.x}`);
        }
    }
}

function fresh(options) {
    const ui = loadUi(options);
    const buffers = ui.buffers;
    buffers.create("9gwenv", ENV_FRAMES, 1);
    buffers.create("9gwpeaks", 512, 1);
    ui.ctx.envbuf("9gwenv");
    ui.ctx.peakbuf("9gwpeaks");
    return ui;
}

function block(ui, p, e, name) {
    const data = ui.buffers.get(name || "9gwenv").data[0];
    const base = (p * 2 + e) * 385;
    const n = data[base];
    const pts = [];
    for (let i = 0; i < n; i++) {
        pts.push({ x: data[base + 1 + 3 * i], u: data[base + 2 + 3 * i], c: data[base + 3 + 3 * i] });
    }
    return pts;
}

function button(ctx, id) {
    const b = ctx.curLayout().buttons.find((x) => x.id === id);
    assert.ok(b, `button ${id}`);
    return [b.x + b.w / 2, b.y + b.h / 2];
}

function click(ctx, x, y, mods) {
    const m = Object.assign({ shift: 0, option: 0 }, mods);
    ctx.onclick(x, y, 1, 0, m.shift, 0, m.option, 0);
}

function dragTo(ctx, x, y, mods) {
    const m = Object.assign({ shift: 0, option: 0 }, mods);
    ctx.ondrag(x, y, 1, 0, m.shift, 0, m.option, 0);
}

function release(ctx, x, y) {
    ctx.ondrag(x, y, 0, 0, 0, 0, 0, 0);
}

test("every preset is a valid envelope", () => {
    const { ctx } = loadUi();
    for (let e = 0; e < 2; e++) {
        const list = ctx.PRESETS[e];
        assert.ok(list.length >= 10);
        const names = new Set();
        for (let i = 0; i < list.length; i++) {
            names.add(list[i].name);
            checkEnv(ctx, ctx.makePreset(e, i), list[i].name);
            assert.ok(/^[\x20-\x7e]+$/.test(list[i].name), "ASCII names (Max text rendering)");
        }
        assert.equal(names.size, list.length, "unique names");
    }
    for (const [t, v] of plain(ctx.DEFAULT_SLOTS)) {
        assert.ok(ctx.presetIndex(0, t) >= 0 && ctx.presetIndex(1, v) >= 0, `${t} / ${v}`);
    }
});

test("presets mean what their names say", () => {
    const { ctx } = loadUi();
    const env = (e, name) => ctx.makePreset(e, ctx.presetIndex(e, name));
    const roll = env(0, "1/16 Roll");
    assert.equal(ctx.evalEnv(roll, 0.3), 0);
    assert.equal(ctx.evalEnv(roll, 0.55), 0);
    assert.equal(ctx.evalEnv(roll, 0.6), 1 / 16);
    assert.equal(ctx.evalEnv(roll, 0.99), 7 / 16);
    const half = env(0, "Half Time");
    assert.ok(Math.abs(ctx.evalEnv(half, 0.5) - 0.25) < 1e-12);
    const stop = env(0, "Tape Stop");
    // offset t^2 over the second half: speed 1 - 2 (dOffset/dx) falls linearly to 0
    const slope = (x) => (ctx.evalEnv(stop, x + 1e-6) - ctx.evalEnv(stop, x)) / 1e-6;
    assert.ok(Math.abs(slope(0.75) - 0.5) < 1e-4);
    assert.ok(Math.abs(slope(0.999) - 1) < 1e-2);
    const gate = env(1, "Gate 1/16");
    assert.equal(ctx.evalEnv(gate, 0.01), 0);
    assert.equal(ctx.evalEnv(gate, 0.04), 1);
    assert.equal(gate.length <= ctx.MAX_PTS, true);
    const trance = env(1, "Trance Gate");
    for (let i = 0; i < 16; i++) {
        assert.equal(ctx.evalEnv(trance, (i + 0.5) / 16), 1 - ctx.TRANCE[i]);
    }
});

test("curves: c = 1/3 is t squared and handles invert the shape", () => {
    const { ctx } = loadUi();
    assert.ok(Math.abs(ctx.shape(0.3, 1 / 3) - 0.09) < 1e-12);
    assert.ok(Math.abs(ctx.shape(0.3, -1 / 3) - (1 - 0.49)) < 1e-12);
    for (const c of [-1, -0.6, -1 / 3, 0, 0.2, 1 / 3, 0.8, 1]) {
        const mid = ctx.shape(0.5, c);
        assert.ok(Math.abs(ctx.curveForMid(0, 1, mid) - c) < 1e-9, `c=${c}`);
        assert.ok(Math.abs(ctx.curveForMid(1, 0, 1 - mid) - c) < 1e-9, `c=${c} falling`);
    }
    assert.equal(ctx.curveForMid(0.4, 0.4, 0.9), 0);
});

test("cleanEnv sorts, closes the ends and drops points that change nothing", () => {
    const { ctx } = loadUi();
    const pts = plain(
        ctx.cleanEnv([
            { x: 0.5, u: 0.2, c: 0 },
            { x: 0.25, u: 0.2, c: 0 },
            { x: 0.75, u: 0.2, c: 0 },
            { x: 0.6, u: 0.7, c: 0 },
            { x: 0.6, u: 0.1, c: 0 },
            { x: 0.6, u: 0.9, c: 0 },
            { x: 2, u: -1, c: 5 },
        ]),
    );
    assert.deepEqual(pts, [
        { x: 0, u: 0.2, c: 0 },
        { x: 0.5, u: 0.2, c: 0 },
        { x: 0.6, u: 0.7, c: 0 },
        { x: 0.6, u: 0.9, c: 0 },
        { x: 0.75, u: 0.2, c: 0 },
        { x: 1, u: 0, c: 0 },
    ]);
    const many = [];
    for (let i = 0; i < 400; i++) {
        many.push({ x: i / 400, u: (i % 7) / 7, c: 0 });
    }
    checkEnv(ctx, ctx.cleanEnv(many), "capped");
    assert.deepEqual(plain(ctx.cleanEnv([])), [
        { x: 0, u: 0, c: 0 },
        { x: 1, u: 0, c: 0 },
    ]);
});

test("setRange paints one level and keeps the rest", () => {
    const { ctx } = loadUi();
    const ramp = ctx.cleanEnv([
        { x: 0, u: 0, c: 0 },
        { x: 1, u: 0.5, c: 0 },
    ]);
    const out = ctx.setRange(ramp, 0.25, 0.5, 0.9);
    checkEnv(ctx, out, "painted");
    assert.equal(ctx.evalEnv(out, 0.3), 0.9);
    assert.ok(Math.abs(ctx.evalEnv(out, 0.1) - 0.05) < 1e-12);
    assert.ok(Math.abs(ctx.evalEnv(out, 0.75) - 0.375) < 1e-12);
    assert.ok(Math.abs(ctx.evalLeft(out, 0.25) - 0.125) < 1e-12, "the step starts from the old level");
    const whole = ctx.setRange(ramp, 0, 1, 0.3);
    assert.deepEqual(plain(whole), [
        { x: 0, u: 0.3, c: 0 },
        { x: 1, u: 0.3, c: 0 },
    ]);
});

test("the blob round-trips and foreign blobs are ignored", () => {
    const ui = fresh();
    const { ctx } = ui;
    ctx.setgrid(5);
    ctx.setswing(30);
    ctx.S.pats[4][1] = ctx.makePreset(1, 3);
    ctx.S.preset[4][1] = 3;
    const blob = plain(ctx.getvalueof());
    const other = fresh();
    other.ctx.setvalueof(...blob);
    assert.deepEqual(plain(other.ctx.S), plain(ctx.S));
    assert.deepEqual(block(other, 4, 1), plain(ctx.S.pats[4][1]), "restored state reaches gen~");
    const before = plain(other.ctx.S);
    other.ctx.setvalueof(1, 2, 3);
    other.ctx.setvalueof(...blob.slice(0, blob.length - 4));
    assert.deepEqual(plain(other.ctx.S), before);
});

test("every envelope is mirrored into the buffer in gen~'s layout", () => {
    for (const chanBase of [1, 0]) {
        const ui = fresh({ chanBase });
        const { ctx } = ui;
        assert.equal(ctx.chanBase, chanBase, "channel numbering detected");
        for (let p = 0; p < 8; p++) {
            for (let e = 0; e < 2; e++) {
                assert.deepEqual(block(ui, p, e), plain(ctx.S.pats[p][e]), `pattern ${p} env ${e}`);
            }
        }
    }
});

test("an undersized buffer~ is resized before writing", () => {
    const buffers = makeMaxBuffers(1);
    buffers.create("tiny", 10, 1);
    const ui = loadUi({ buffers });
    ui.ctx.envbuf("tiny");
    assert.equal(buffers.get("tiny").frames, ENV_FRAMES);
    assert.deepEqual(block(ui, 7, 1, "tiny"), plain(ui.ctx.S.pats[7][1]));
});

test("click adds a snapped point, drag moves it, mouse-up tells Live once", () => {
    const ui = fresh();
    const { ctx } = ui;
    click(ctx, toX(0.3), toY(0.4));
    let pts = ctx.S.pats[0][0];
    assert.deepEqual(plain(pts[1]), { x: 5 / 16, u: 6 / 16, c: 0 }, "x and time offset snap to 1/16");
    assert.equal(block(ui, 0, 0).length, 3, "gen~ sees the point while dragging");
    dragTo(ctx, toX(0.52), toY(0.49));
    pts = ctx.S.pats[0][0];
    assert.deepEqual(plain(pts[1]), { x: 0.5, u: 0.5, c: 0 });
    assert.equal(ui.notified.count, 0, "no Live notification during the drag");
    release(ctx, toX(0.52), toY(0.49));
    assert.equal(ui.notified.count, 1);
    assert.equal(ctx.S.preset[0][0], -1, "now a custom shape");
    assert.equal(ctx.presetLabel(), "Custom");
    // Shift: free placement
    click(ctx, toX(0.7), toY(0.3), { shift: 1 });
    dragTo(ctx, toX(0.71), toY(0.33), { shift: 1 });
    release(ctx, toX(0.71), toY(0.33));
    const free = ctx.S.pats[0][0].find((p) => p.x > 0.6 && p.x < 0.8);
    assert.ok(Math.abs(free.x - 0.71) < 1e-9 && Math.abs(free.u - 0.33) < 1e-9);
});

test("points stay between their neighbours; ends keep their x", () => {
    const ui = fresh();
    const { ctx } = ui;
    ctx.dopreset(ctx.presetIndex(0, "Slow > Fast")); // (0,0) (0.5,0.25) (1,0)
    click(ctx, toX(0.5), toY(0.25));
    dragTo(ctx, toX(1), toY(0.25));
    assert.equal(ctx.S.pats[0][0][1].x, 1);
    release(ctx, toX(1), toY(0.25));
    click(ctx, toX(0), toY(0));
    dragTo(ctx, toX(0.4), toY(0.5));
    release(ctx, toX(0.4), toY(0.5));
    assert.equal(ctx.S.pats[0][0][0].x, 0);
    assert.equal(ctx.S.pats[0][0][0].u, 0.5);
});

test("a click that moves nothing leaves no undo step and no notification", () => {
    const ui = fresh();
    const { ctx } = ui;
    click(ctx, toX(0), toY(0));
    release(ctx, toX(0), toY(0));
    assert.equal(ctx.undoStack.length, 0);
    assert.equal(ui.notified.count, 0);
    assert.equal(ctx.S.preset[0][0], 0, "still the preset");
});

test("Alt-click and double-click delete points, never the ends", () => {
    const ui = fresh();
    const { ctx } = ui;
    ctx.dopreset(ctx.presetIndex(0, "Reverse 1/2")); // (0,0) (0.5,0) (1,1)
    click(ctx, toX(1), toY(1), { option: 1 });
    assert.equal(ctx.S.pats[0][0].length, 3);
    click(ctx, toX(0.5), toY(0), { option: 1 });
    assert.equal(ctx.S.pats[0][0].length, 2);
    ctx.doundo();
    assert.equal(ctx.S.pats[0][0].length, 3);
    ctx.ondblclick(toX(0.5), toY(0), 1, 0, 0, 0, 0, 0);
    assert.equal(ctx.S.pats[0][0].length, 2);
});

test("curve handles bend a segment; Alt-click straightens it", () => {
    const ui = fresh();
    const { ctx } = ui;
    ctx.dopreset(ctx.presetIndex(0, "Half Time")); // (0,0) -> (1,0.5)
    click(ctx, toX(0.5), toY(0.25));
    dragTo(ctx, toX(0.5), toY(0.125));
    release(ctx, toX(0.5), toY(0.125));
    const c = ctx.S.pats[0][0][0].c;
    assert.ok(Math.abs(c - 1 / 3) < 1e-9, `curve ${c}`);
    assert.ok(Math.abs(ctx.evalEnv(ctx.S.pats[0][0], 0.5) - 0.125) < 1e-9);
    assert.equal(block(ui, 0, 0)[0].c, c);
    click(ctx, toX(0.5), toY(0.125), { option: 1 });
    assert.equal(ctx.S.pats[0][0][0].c, 0);
});

test("the draw tool paints grid cells and Alt erases", () => {
    const ui = fresh();
    const { ctx } = ui;
    ctx.settool(1);
    click(ctx, toX(0.51), toY(0.25));
    dragTo(ctx, toX(0.58), toY(0.25));
    release(ctx, toX(0.58), toY(0.25));
    const pts = ctx.S.pats[0][0];
    checkEnv(ctx, pts, "painted");
    assert.equal(ctx.evalEnv(pts, 0.49), 0);
    assert.equal(ctx.evalEnv(pts, 0.53), 0.25);
    assert.equal(ctx.evalEnv(pts, 0.6), 0.25);
    assert.equal(ctx.evalEnv(pts, 0.63), 0);
    assert.equal(ui.notified.count, 1);
    click(ctx, toX(0.53), toY(0.8), { option: 1 });
    release(ctx, toX(0.53), toY(0.8));
    assert.equal(ctx.evalEnv(ctx.S.pats[0][0], 0.53), 0);
    assert.equal(ctx.evalEnv(ctx.S.pats[0][0], 0.6), 0.25);
});

test("undo and redo walk the history", () => {
    const ui = fresh();
    const { ctx } = ui;
    const original = plain(ctx.S.pats[0][0]);
    ctx.nextpreset();
    const next = plain(ctx.S.pats[0][0]);
    assert.notDeepEqual(next, original);
    ctx.doundo();
    assert.deepEqual(plain(ctx.S.pats[0][0]), original);
    assert.deepEqual(block(ui, 0, 0), original);
    ctx.doredo();
    assert.deepEqual(plain(ctx.S.pats[0][0]), next);
    assert.equal(ctx.S.preset[0][0], 1);
});

test("copy in one device, paste in another", () => {
    const globals = new Map();
    const a = fresh({ globals });
    const b = fresh({ globals });
    a.ctx.pattern(2);
    a.ctx.docopy();
    assert.equal(b.ctx.curLayout().buttons.find((x) => x.id === "paste").enabled, true);
    b.ctx.dopaste();
    assert.deepEqual(plain(b.ctx.S.pats[0][0]), plain(a.ctx.S.pats[2][0]));
    assert.equal(b.ctx.S.preset[0][0], -1);
    assert.deepEqual(block(b, 0, 0), plain(a.ctx.S.pats[2][0]));
});

test("the toolbar switches envelopes, steps presets and opens the preset list", () => {
    const ui = fresh();
    const { ctx } = ui;
    click(ctx, ...button(ctx, "paste"));
    assert.equal(ctx.undoStack.length, 0, "paste is disabled with an empty clipboard");
    click(ctx, ...button(ctx, "vol"));
    assert.equal(ctx.S.edit, 1);
    click(ctx, ...button(ctx, "pnext"));
    assert.equal(ctx.S.preset[0][1], 1);
    click(ctx, ...button(ctx, "pprev"));
    click(ctx, ...button(ctx, "pprev"));
    assert.equal(ctx.S.preset[0][1], ctx.PRESETS[1].length - 1, "wraps around");
    click(ctx, ...button(ctx, "pname"));
    assert.equal(ctx.view.overlay, true);
    const item = ctx.overlayItems(ctx.curLayout())[3];
    click(ctx, item.x + 2, item.y + 2);
    assert.equal(ctx.view.overlay, false);
    assert.equal(ctx.S.preset[0][1], 3);
    click(ctx, ...button(ctx, "draw"));
    assert.equal(ctx.S.tool, 1);
    click(ctx, ...button(ctx, "gnext"));
    assert.equal(ctx.S.grid, 4);
    click(ctx, ...button(ctx, "snext"));
    assert.equal(ctx.S.swing, 5);
    click(ctx, ...button(ctx, "clear"));
    assert.deepEqual(plain(ctx.S.pats[0][1]), [
        { x: 0, u: 0, c: 0 },
        { x: 1, u: 0, c: 0 },
    ]);
    click(ctx, ...button(ctx, "undo"));
    assert.equal(ctx.S.preset[0][1], 3);
});

test("grid follows the pattern length and swing moves the off-lines", () => {
    const { ctx } = loadUi();
    assert.equal(ctx.gridCells(), 16);
    ctx.patlen(0);
    assert.equal(ctx.gridCells(), 4, "1/16 over one beat");
    ctx.patlen(2);
    ctx.setswing(50);
    assert.equal(ctx.gridLine(2, 16), 2 / 16);
    assert.equal(ctx.gridLine(3, 16), 3 / 16 + 0.25 / 16);
    assert.equal(ctx.snapX(3.3 / 16, false), 3.25 / 16);
    ctx.setgrid(0);
    assert.equal(ctx.snapX(0.333, false), 0.333);
    assert.equal(ctx.snapU(0.01, false), 0, "levels stick to the edges");
});

test("lengths up to 4 bars and any meter: grid cells, a shorter last cell, snapping to the end", () => {
    const { ctx } = loadUi();
    ctx.patlen(4);
    assert.equal(ctx.patQuarters(), 16);
    assert.equal(ctx.gridCells(), 64, "1/16 over 4 bars of 4/4");
    ctx.signum(7);
    ctx.sigden(8);
    ctx.patlen(2);
    assert.equal(ctx.quartersPerBar(), 3.5);
    ctx.setgrid(1); // 1/4
    assert.equal(ctx.gridCells(), 3.5);
    assert.deepEqual(plain(ctx.cellAt(0.95)), { k: 3, a: 3 / 3.5, b: 1 }, "the last cell is half a beat");
    assert.equal(ctx.snapX(0.97, false), 1);
    assert.equal(ctx.snapX(0.3, false), 1 / 3.5);
    ctx.signum(6);
    ctx.sigden(8);
    ctx.patlen(3);
    assert.equal(ctx.patQuarters(), 6, "two bars of 6/8");
    ctx.patlen(99);
    assert.equal(ctx.view.patlen, 4);
});

test("messages from the patcher update the view", () => {
    const { ctx } = loadUi();
    ctx.pattern(5.0);
    ctx.active(2);
    ctx.phase(0.25);
    assert.equal(ctx.view.pattern, 5);
    assert.equal(ctx.view.active, 2);
    assert.equal(ctx.view.phase, 0.25);
    ctx.pattern(99);
    assert.equal(ctx.view.pattern, 7);
});

test("hover marks points, handles and buttons", () => {
    const ui = fresh();
    const { ctx } = ui;
    ctx.onidle(toX(1) - 1, toY(0), 0, 0, 0, 0, 0, 0);
    assert.equal(ctx.view.hover, "pt:1");
    ctx.onidle(...button(ctx, "copy"), 0, 0, 0, 0, 0, 0);
    assert.equal(ctx.view.hover, "btn:copy");
    ctx.onidleout();
    assert.equal(ctx.view.hover, "");
});

const MGRAPHICS_API = new Set([
    "init",
    "redraw",
    "set_source_rgba",
    "rectangle",
    "rectangle_rounded",
    "fill",
    "stroke",
    "move_to",
    "line_to",
    "close_path",
    "set_line_width",
    "ellipse",
    "select_font_face",
    "set_font_size",
    "text_measure",
    "show_text",
]);

test("paint draws every state with real mgraphics calls only", () => {
    const ui = fresh();
    const { ctx, mg } = ui;
    const peaks = ui.buffers.get("9gwpeaks").data[0];
    for (let i = 0; i < 512; i++) {
        peaks[i] = Math.abs(Math.sin(i / 9));
    }
    const states = [
        () => {},
        () => ctx.phase(0.4),
        () => ctx.active(3),
        () => ctx.setedit(1),
        () => ctx.settool(1),
        () => {
            ctx.settool(0);
            ctx.view.overlay = true;
        },
        () => {
            ctx.view.overlay = false;
            ctx.setgrid(4);
            ctx.patlen(0);
            ctx.view.hover = "pt:1";
        },
        () => {
            ctx.patlen(4);
            ctx.signum(7);
            ctx.sigden(8);
            ctx.setgrid(1);
        },
    ];
    for (const set of states) {
        set();
        mg.calls.length = 0;
        ctx.paint();
        const used = new Set(mg.calls.map((c) => c[0]));
        for (const name of used) {
            assert.ok(MGRAPHICS_API.has(name), `mgraphics.${name}`);
        }
        assert.ok(used.has("fill") && used.has("stroke") && used.has("show_text"));
        for (const c of mg.calls) {
            for (const v of c.slice(1)) {
                assert.ok(typeof v !== "number" || Number.isFinite(v), `${c[0]} got ${v}`);
            }
        }
    }
});
