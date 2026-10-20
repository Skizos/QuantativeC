// The device files: the .amxd containers, the patchers' wiring, and that they match what the gen~ code and the
// editor expect (message names, buffer sizes, parameters, layout). The control logic made of Max objects (MIDI
// triggers, link switch, Live API) runs in tools/maxsim.js on the built patchers' own cords.

"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");
const B = require("../tools/build");
const { MaxSim } = require("../tools/maxsim");
const { parse } = require("../tools/gensim");
const { ROOT, GEN_SRC, loadUi, makeRig } = require("./helpers");

const files = B.build();
const amxd = files[path.join(B.FOLDER, B.DEVICE_NAME)];
const midiAmxd = files[path.join(B.FOLDER, B.MIDI_DEVICE_NAME)];
const device = B.readAmxd(amxd);
const midiDevice = B.readAmxd(midiAmxd);
const top = device.patcher.patcher;
const midiTop = midiDevice.patcher.patcher;
const boxById = new Map(top.boxes.map((b) => [b.box.id, b.box]));
const genBox = top.boxes.map((b) => b.box).find((b) => b.text === "gen~");
const uiBox = top.boxes.map((b) => b.box).find((b) => b.maxclass === "jsui");
const gen = parse(GEN_SRC);
const genParams = new Set(gen.decls.filter((d) => d.kind === "Param").map((d) => d.name));

function linesFrom(patcher, id) {
    return patcher.lines.map((l) => l.patchline).filter((l) => l.source[0] === id);
}

function linesTo(patcher, id) {
    return patcher.lines.map((l) => l.patchline).filter((l) => l.destination[0] === id);
}

function param(patcher, longname) {
    return patcher.boxes
        .map((b) => b.box)
        .find((b) => b.parameter_enable === 1 && b.saved_attribute_attributes.valueof.parameter_longname === longname);
}

test("the .amxd files are an audio and a MIDI effect in the chunk layout Live reads", () => {
    for (const [bytes, type] of [
        [amxd, "aaaa"],
        [midiAmxd, "mmmm"],
    ]) {
        assert.equal(bytes.toString("ascii", 0, 4), "ampf");
        assert.equal(bytes.readUInt32LE(4), 4);
        assert.equal(bytes.toString("ascii", 8, 12), type);
        assert.equal(bytes.toString("ascii", 12, 16), "meta");
        assert.equal(bytes.readUInt32LE(16), 4);
        assert.equal(bytes.readUInt32LE(20), 0);
        assert.equal(bytes.toString("ascii", 24, 28), "ptch");
        assert.equal(bytes.readUInt32LE(28), bytes.length - 32);
        assert.equal(bytes[bytes.length - 1], 0, "the patcher JSON ends with a NUL");
    }
    assert.equal(device.type, "aaaa");
    assert.equal(midiDevice.type, "mmmm");
});

test("dist/ holds exactly what the sources build", () => {
    const r = spawnSync(process.execPath, [path.join(ROOT, "tools", "build.js"), "--check"], { encoding: "utf8" });
    assert.equal(r.status, 0, r.stderr);
});

test("the download zip holds the folder: both devices, the script and the install notes", () => {
    const zip = B.readZip(fs.readFileSync(path.join(B.DIST_ROOT, B.ZIP_NAME)));
    const names = Object.keys(zip).sort();
    const want = [B.DEVICE_NAME, B.MIDI_DEVICE_NAME, B.UI_SCRIPT, B.INSTALL].map((n) => `${B.FOLDER}/${n}`);
    assert.deepEqual(names, [`${B.FOLDER}/`].concat(want).sort());
    for (const n of want) {
        assert.ok(zip[n].equals(fs.readFileSync(path.join(B.DIST, n.slice(B.FOLDER.length + 1)))), n);
    }
    assert.equal(B.crc32(Buffer.from("123456789")), 0xcbf43926, "the standard CRC-32 check value");
    // eslint-disable-next-line no-control-regex
    assert.ok(/^[\x09\x0a\x0d\x20-\x7e]*$/.test(zip[`${B.FOLDER}/${B.INSTALL}`].toString("latin1")));
});

for (const [label, patcher] of [
    ["audio effect", top],
    ["gen~", genBox.patcher],
    ["MIDI effect", midiTop],
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

test("the gen~ codebox is the genexpr source, fed by a beat-locked phase", () => {
    const codebox = genBox.patcher.boxes.map((b) => b.box).find((b) => b.maxclass === "codebox");
    assert.equal(codebox.code, GEN_SRC);
    assert.deepEqual([...new Set(GEN_SRC.match(/\bin\d\b/g))].sort(), ["in1", "in2", "in3"]);
    assert.deepEqual([...new Set(GEN_SRC.match(/\bout\d\b/g))].sort(), ["out1", "out2", "out3", "out4"]);
    const audio = (from, to) => top.lines.some((l) => l.patchline.source[0] === from && l.patchline.destination[0] === to);
    const plugin = top.boxes.find((b) => b.box.text === "plugin~").box.id;
    const plugout = top.boxes.find((b) => b.box.text === "plugout~").box.id;
    assert.ok(audio(plugin, genBox.id) && audio(genBox.id, plugout));
    const phasor = top.boxes.find((b) => (b.box.text || "").startsWith("phasor~")).box;
    assert.equal(phasor.text, "phasor~ 4n @lock 1");
    assert.ok(linesFrom(top, phasor.id).some((l) => l.destination[0] === genBox.id && l.destination[1] === 2));
});

test("every message into gen~ names one of its Params or Buffers, and every Param is set", () => {
    const names = new Set(gen.decls.filter((d) => d.kind === "Param" || d.kind === "Buffer").map((d) => d.name));
    const sent = new Set();
    for (const l of linesTo(top, genBox.id)) {
        const src = boxById.get(l.source[0]);
        if (src.text && src.text.startsWith("prepend ")) {
            const name = src.text.split(" ")[1];
            assert.ok(names.has(name), `gen~ has no ${name}`);
            sent.add(name);
        }
    }
    // the triggers come from the MIDI device, through the link switch
    for (const b of midiTop.boxes.map((x) => x.box).filter((x) => (x.text || "").startsWith("prepend "))) {
        const name = b.text.split(" ")[1];
        assert.ok(genParams.has(name), `gen~ has no ${name}`);
        sent.add(name);
    }
    for (const n of names) {
        assert.ok(sent.has(n), `nothing sets ${n}`);
    }
});

test("every message into the editor is a function of the script", () => {
    const { ctx } = loadUi();
    assert.equal(uiBox.filename, B.UI_SCRIPT);
    for (const l of linesTo(top, uiBox.id)) {
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
    assert.equal(B.ENV_FRAMES, ctx.ENV_FRAMES);
    assert.equal(B.ENV_FRAMES, 16 * blockLen);
    assert.equal(B.PEAK_FRAMES, 2 * ctx.PEAK_BINS);
    const buffers = top.boxes.map((b) => b.box).filter((b) => (b.text || "").startsWith("buffer~"));
    assert.equal(buffers.length, 2);
    for (const b of buffers) {
        const name = b.text.split(" ")[1];
        assert.ok(name.startsWith("---"), `${name} is private to the device`);
        const size = linesTo(top, b.id).map((l) => boxById.get(l.source[0]).text)[0];
        assert.equal(size, `sizeinsamps ${name.endsWith("gwenv") ? B.ENV_FRAMES : B.PEAK_FRAMES}`);
        assert.ok(top.boxes.some((x) => x.box.text === `symbol ${name}`), `the name ${name} is handed on`);
    }
});

test("the load order sizes the buffers before naming them, then pushes the parameters", () => {
    const sim = new MaxSim(device.patcher);
    const trig = sim.byText("t b b b");
    const order = [];
    const origDeliver = sim.deliver.bind(sim);
    sim.deliver = (id, inlet, msg) => {
        order.push(sim.boxes.get(id).text || sim.boxes.get(id).maxclass);
        origDeliver(id, inlet, msg);
    };
    sim.emit(sim.byText("loadbang"), 0, ["bang"]);
    assert.ok(order.indexOf("t b b b") === 0);
    const firstSymbol = order.findIndex((t) => t.startsWith("symbol"));
    const lastSize = order.map((t) => t.startsWith("sizeinsamps")).lastIndexOf(true);
    const firstParam = order.findIndex((t) => t.startsWith("live."));
    assert.ok(lastSize < firstSymbol && firstSymbol < firstParam, order.join(" > "));
    assert.ok(trig);
});

test("the Live API: song position and time signature reach gen~ (and the meter the editor)", () => {
    const sim = new MaxSim(device.patcher);
    const observers = [...sim.boxes.values()].filter((b) => b.text === "live.observer").map((b) => b.id);
    assert.equal(observers.length, 3);
    sim.emit(sim.byText("live.thisdevice"), 0, ["bang"]);
    assert.deepEqual(sim.got(sim.byText("live.path")), [[0, ["path", "live_set"]]]);
    sim.emit(sim.byText("live.path"), 0, ["id", 3]);
    const props = observers.map((o) => {
        const got = sim.got(o);
        assert.deepEqual(got[0], [1, ["id", 3]], "the id comes first");
        return got[1][1].join(" ");
    });
    assert.deepEqual(props.sort(), [
        "property current_song_time",
        "property signature_denominator",
        "property signature_numerator",
    ]);
    const values = { current_song_time: 12.5, signature_numerator: 7, signature_denominator: 8 };
    for (const o of observers) {
        sim.emit(o, 0, [values[sim.got(o)[1][1][1]]]);
    }
    const toGen = sim.got(genBox.id).map(([, m]) => m.join(" "));
    assert.deepEqual(toGen.sort(), ["sigden 8", "signum 7", "songbeats 12.5"]);
    const toUi = sim.got(uiBox.id).map(([, m]) => m.join(" "));
    assert.deepEqual(toUi.sort(), ["sigden 8", "signum 7"]);
});

function encodeIn(sim, v) {
    return sim.got(genBox.id).filter(([, m]) => m[0] === v).map(([, m]) => m[1]);
}

test("Gross Warp MIDI turns notes into trigger messages on the chosen link", () => {
    const sim = new MaxSim(midiDevice.patcher);
    sim.emit(sim.byText("loadbang"), 0, ["bang"]);
    const p = (name) => sim.byVarname(name);
    sim.emit(p("Link"), 0, [0]); // A
    sim.emit(p("Mode"), 0, [0]); // Hold
    sim.emit(p("Base Note"), 0, [36]);
    const parse = sim.byText("midiparse");
    const note = (pitch, vel) => sim.emit(parse, 0, [pitch, vel]);
    note(38, 100); // pattern 3 on
    note(38, 0); // off
    note(38, 90); // on again: the parity has flipped
    note(35, 100); // below Base: ignored
    note(44, 100); // above Base + 7: ignored
    const sent = sim.sent.get(B.LINK_SEND("A"));
    assert.deepEqual(sent, [
        ["trigon", 2],
        ["trigoff", 2],
        ["trigon", 2 + 8],
    ]);
    sim.emit(p("Mode"), 0, [1]); // Latch
    sim.emit(p("Link"), 0, [2]); // C
    sim.emit(p("Base Note"), 0, [60]);
    note(67, 100); // pattern 8
    assert.deepEqual(sim.sent.get(B.LINK_SEND("C")), [["trigon", 7 + 0 + 16]]);
});

test("Gross Warp MIDI passes MIDI through unless Thru is off", () => {
    const sim = new MaxSim(midiDevice.patcher);
    const midiIn = sim.byText("midiin");
    const midiOut = sim.byText("midiout");
    sim.emit(midiIn, 0, [144]);
    sim.emit(sim.byVarname("Thru"), 0, [0]);
    sim.emit(midiIn, 0, [60]);
    assert.deepEqual(sim.got(midiOut), [[0, [144]]]);
});

test("Gross Warp listens to the link its MIDI menu picks", () => {
    const sim = new MaxSim(device.patcher);
    const midi = sim.byVarname("MIDI Link");
    sim.receive(B.LINK_SEND("A"), ["trigon", 3]);
    assert.deepEqual(encodeIn(sim, "trigon"), [], "Off: nothing passes");
    sim.emit(midi, 0, [2]); // B
    assert.deepEqual(encodeIn(sim, "midien"), [1]);
    sim.receive(B.LINK_SEND("A"), ["trigon", 3]);
    sim.receive(B.LINK_SEND("B"), ["trigon", 5]);
    assert.deepEqual(encodeIn(sim, "trigon"), [5]);
    sim.emit(midi, 0, [0]);
    assert.deepEqual(encodeIn(sim, "midien"), [1, 0]);
});

test("end to end: a held note in Gross Warp MIDI plays that pattern in Gross Warp", () => {
    const midi = new MaxSim(midiDevice.patcher);
    const fx = new MaxSim(device.patcher);
    midi.emit(midi.byText("loadbang"), 0, ["bang"]);
    midi.emit(midi.byVarname("Link"), 0, [3]); // D
    midi.emit(midi.byVarname("Mode"), 0, [0]);
    midi.emit(midi.byVarname("Base Note"), 0, [36]);
    fx.emit(fx.byVarname("MIDI Link"), 0, [4]); // D
    fx.emit(fx.byVarname("Switch"), 0, [0]); // Instant
    const rig = makeRig({ sr: 12000 });
    let applied = 0;
    const pump = () => {
        for (const m of midi.sent.get(B.LINK_SEND("D")) || []) {
            fx.receive(B.LINK_SEND("D"), m);
        }
        midi.sent.set(B.LINK_SEND("D"), []);
        const msgs = fx.got(genBox.id).slice(applied);
        applied += msgs.length;
        for (const [, [name, v]] of msgs) {
            rig.gen.setParam(name, v);
        }
        const out = rig.run(50, () => [0, 0]);
        return out.active[out.active.length - 1];
    };
    assert.equal(pump(), 0);
    midi.emit(midi.byText("midiparse"), 0, [41, 100]); // F1 = pattern 6
    assert.equal(pump(), 5);
    midi.emit(midi.byText("midiparse"), 0, [41, 0]);
    assert.equal(pump(), 0);
});

test("parameters are unique, listed and banked in both devices", () => {
    for (const patcher of [top, midiTop]) {
        const params = patcher.boxes.map((b) => b.box).filter((b) => b.parameter_enable === 1);
        const longnames = params.map((b) => b.saved_attribute_attributes.valueof.parameter_longname);
        assert.equal(new Set(longnames).size, longnames.length);
        for (const b of params) {
            assert.ok(patcher.parameters[b.id], `${b.id} listed`);
            assert.equal(patcher.parameters[b.id][0], b.saved_attribute_attributes.valueof.parameter_longname);
        }
        for (const n of patcher.parameters.parameterbanks[0].parameters) {
            assert.ok(n === "-" || longnames.includes(n), n);
        }
    }
    const blob = uiBox.saved_attribute_attributes.valueof;
    assert.equal(blob.parameter_type, 3, "the editor's data is a blob");
    assert.equal(blob.parameter_invisible, 1, "stored with the set, not automatable");
    assert.deepEqual(param(top, "Length").saved_attribute_attributes.valueof.parameter_enum.length, 5);
    assert.deepEqual(param(top, "Switch").saved_attribute_attributes.valueof.parameter_enum, [
        "Instant",
        "1/16",
        "Beat",
        "Bar",
        "Pattern",
    ]);
});

test("the presentations fit their devices and nothing overlaps", () => {
    for (const patcher of [top, midiTop]) {
        const shown = patcher.boxes.map((b) => b.box).filter((b) => b.presentation === 1);
        const width = patcher.openrect[2];
        assert.equal(patcher.devicewidth, width);
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
        assert.equal(patcher.openinpresentation, 1);
    }
    assert.deepEqual(uiBox.patching_rect.slice(2), uiBox.presentation_rect.slice(2), "same size in both views");
});

test("every control explains itself in Live's Info View", () => {
    for (const patcher of [top, midiTop]) {
        for (const b of patcher.boxes.map((x) => x.box).filter((x) => x.parameter_enable === 1 && x.maxclass !== "jsui")) {
            assert.ok(b.annotation && b.annotation.length > 20, `${b.varname} has info text`);
            assert.equal(b.annotation_name, b.varname);
        }
    }
});
