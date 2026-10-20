// The device file: the .amxd container, the patcher's wiring, and that it matches what the gen~ code and the editor
// expect (message names, buffer sizes, parameters, layout).

"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");
const { build, readAmxd, DIST, DEVICE_NAME, UI_SCRIPT, ENV_FRAMES, PEAK_FRAMES } = require("../tools/build");
const { parse } = require("../tools/gensim");
const { ROOT, GEN_SRC, loadUi } = require("./helpers");

const files = build();
const amxd = files[DEVICE_NAME];
const device = readAmxd(amxd);
const top = device.patcher.patcher;
const boxById = new Map(top.boxes.map((b) => [b.box.id, b.box]));
const genBox = top.boxes.map((b) => b.box).find((b) => b.text === "gen~");
const uiBox = top.boxes.map((b) => b.box).find((b) => b.maxclass === "jsui");
const gen = parse(GEN_SRC);

function linesFrom(id) {
    return top.lines.map((l) => l.patchline).filter((l) => l.source[0] === id);
}

function linesTo(id) {
    return top.lines.map((l) => l.patchline).filter((l) => l.destination[0] === id);
}

test("the .amxd is an audio effect in the chunk layout Live reads", () => {
    assert.equal(amxd.toString("ascii", 0, 4), "ampf");
    assert.equal(amxd.readUInt32LE(4), 4);
    assert.equal(amxd.toString("ascii", 8, 12), "aaaa");
    assert.equal(amxd.toString("ascii", 12, 16), "meta");
    assert.equal(amxd.readUInt32LE(16), 4);
    assert.equal(amxd.readUInt32LE(20), 0);
    assert.equal(amxd.toString("ascii", 24, 28), "ptch");
    assert.equal(amxd.readUInt32LE(28), amxd.length - 32);
    assert.equal(amxd[amxd.length - 1], 0, "the patcher JSON ends with a NUL");
    assert.equal(device.type, "aaaa");
});

test("dist/ holds exactly what the sources build", () => {
    const r = spawnSync(process.execPath, [path.join(ROOT, "tools", "build.js"), "--check"], { encoding: "utf8" });
    assert.equal(r.status, 0, r.stderr);
    assert.ok(fs.existsSync(path.join(DIST, UI_SCRIPT)), "the editor script ships next to the device");
});

for (const [label, patcher] of [
    ["device", top],
    ["gen~", genBox.patcher],
]) {
    test(`every ${label} patch cord joins real boxes on real inlets and outlets`, () => {
        const ids = new Map(patcher.boxes.map((b) => [b.box.id, b.box]));
        assert.equal(ids.size, patcher.boxes.length, "unique ids");
        for (const { patchline: l } of patcher.lines) {
            const src = ids.get(l.source[0]);
            const dst = ids.get(l.destination[0]);
            assert.ok(src && dst, JSON.stringify(l));
            assert.ok(l.source[1] < src.numoutlets, `${src.text || src.maxclass} outlet ${l.source[1]}`);
            assert.ok(l.destination[1] < dst.numinlets, `${dst.text || dst.maxclass} inlet ${l.destination[1]}`);
        }
    });
}

test("the gen~ codebox is the genexpr source, wired to 3 inlets and 4 outlets", () => {
    const codebox = genBox.patcher.boxes.map((b) => b.box).find((b) => b.maxclass === "codebox");
    assert.equal(codebox.code, GEN_SRC);
    const ins = new Set(GEN_SRC.match(/\bin\d\b/g));
    const outs = new Set(GEN_SRC.match(/\bout\d\b/g));
    assert.deepEqual([...ins].sort(), ["in1", "in2", "in3"]);
    assert.deepEqual([...outs].sort(), ["out1", "out2", "out3", "out4"]);
    assert.equal(genBox.numinlets, 3);
    assert.equal(genBox.numoutlets, 4);
    const audio = (from, to) => top.lines.some((l) => l.patchline.source[0] === from && l.patchline.destination[0] === to);
    const plugin = top.boxes.find((b) => b.box.text === "plugin~").box.id;
    const plugout = top.boxes.find((b) => b.box.text === "plugout~").box.id;
    assert.ok(audio(plugin, genBox.id) && audio(genBox.id, plugout));
    const phasor = top.boxes.find((b) => (b.box.text || "").startsWith("phasor~")).box;
    assert.equal(phasor.text, "phasor~ 1n @lock 1");
    assert.ok(linesFrom(phasor.id).some((l) => l.destination[0] === genBox.id && l.destination[1] === 2));
});

test("every message into gen~ names one of its Params or Buffers", () => {
    const names = new Set(gen.decls.filter((d) => d.kind === "Param" || d.kind === "Buffer").map((d) => d.name));
    const sent = new Set();
    for (const l of linesTo(genBox.id)) {
        const src = boxById.get(l.source[0]);
        if (src.text && src.text.startsWith("prepend ")) {
            const name = src.text.split(" ")[1];
            assert.ok(names.has(name), `gen~ has no ${name}`);
            sent.add(name);
        }
    }
    for (const n of names) {
        assert.ok(sent.has(n), `nothing sets ${n}`);
    }
});

test("every message into the editor is a function of the script", () => {
    const { ctx } = loadUi();
    assert.equal(uiBox.filename, UI_SCRIPT);
    for (const l of linesTo(uiBox.id)) {
        const src = boxById.get(l.source[0]);
        assert.ok(src.text.startsWith("prepend "), src.text);
        const name = src.text.split(" ")[1];
        assert.equal(typeof ctx[name], "function", `gw_ui.js has no ${name}()`);
    }
    for (const fn of ["getvalueof", "setvalueof", "paint", "onclick", "ondrag"]) {
        assert.equal(typeof ctx[fn], "function", fn);
    }
});

test("buffers are device-unique and sized for both sides", () => {
    const { ctx } = loadUi();
    const blockLen = Number(/blockLen = (\d+);/.exec(GEN_SRC)[1]);
    assert.equal(blockLen, ctx.BLOCK);
    assert.equal(ENV_FRAMES, ctx.ENV_FRAMES);
    assert.equal(ENV_FRAMES, 16 * blockLen);
    assert.equal(PEAK_FRAMES, 2 * ctx.PEAK_BINS);
    const buffers = top.boxes.map((b) => b.box).filter((b) => (b.text || "").startsWith("buffer~"));
    assert.equal(buffers.length, 2);
    for (const b of buffers) {
        const name = b.text.split(" ")[1];
        assert.ok(name.startsWith("---"), `${name} is private to the device`);
        const size = linesTo(b.id).map((l) => boxById.get(l.source[0]).text)[0];
        assert.equal(size, `sizeinsamps ${name.endsWith("gwenv") ? ENV_FRAMES : PEAK_FRAMES}`);
        const sym = top.boxes.map((x) => x.box).find((x) => x.text === `symbol ${name}`);
        assert.ok(sym, `the name ${name} is handed on`);
    }
});

test("the load order sizes the buffers before naming them", () => {
    const trig = top.boxes.map((b) => b.box).find((b) => b.text === "t b b b");
    const byOutlet = (o) => linesFrom(trig.id).filter((l) => l.source[1] === o).map((l) => boxById.get(l.destination[0]));
    // a trigger fires right to left
    assert.ok(byOutlet(2).every((b) => b.text.startsWith("sizeinsamps")));
    assert.ok(byOutlet(1).every((b) => b.text.startsWith("symbol ---")));
    assert.ok(byOutlet(0).every((b) => b.parameter_enable === 1));
});

test("parameters are unique, listed and banked", () => {
    const params = top.boxes.map((b) => b.box).filter((b) => b.parameter_enable === 1);
    const longnames = params.map((b) => b.saved_attribute_attributes.valueof.parameter_longname);
    assert.equal(new Set(longnames).size, longnames.length);
    for (const b of params) {
        assert.ok(top.parameters[b.id], `${b.id} listed`);
        assert.equal(top.parameters[b.id][0], b.saved_attribute_attributes.valueof.parameter_longname);
    }
    for (const n of top.parameters.parameterbanks[0].parameters) {
        assert.ok(n === "-" || longnames.includes(n), n);
    }
    const blob = uiBox.saved_attribute_attributes.valueof;
    assert.equal(blob.parameter_type, 3, "the editor's data is a blob");
    assert.equal(blob.parameter_invisible, 1, "stored with the set, not automatable");
});

test("the presentation fits the device and nothing overlaps", () => {
    const shown = top.boxes.map((b) => b.box).filter((b) => b.presentation === 1);
    const width = top.openrect[2];
    for (const b of shown) {
        const [x, y, w, h] = b.presentation_rect;
        assert.ok(x >= 0 && y >= 0 && x + w <= width && y + h <= 169, `${b.maxclass} ${b.presentation_rect}`);
    }
    for (let i = 0; i < shown.length; i++) {
        for (let j = i + 1; j < shown.length; j++) {
            const [ax, ay, aw, ah] = shown[i].presentation_rect;
            const [bx, by, bw, bh] = shown[j].presentation_rect;
            const overlap = ax < bx + bw && bx < ax + aw && ay < by + bh && by < ay + ah;
            assert.ok(!overlap, `${shown[i].varname || shown[i].text} / ${shown[j].varname || shown[j].text}`);
        }
    }
    assert.deepEqual(uiBox.patching_rect.slice(2), uiBox.presentation_rect.slice(2), "same size in both views");
    assert.equal(top.openinpresentation, 1);
});
