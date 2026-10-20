#!/usr/bin/env node
// Builds the Gross Warp Max for Live audio effect from src/:
//   dist/Gross Warp/Gross Warp.amxd   the device (gen~ code embedded in its codebox)
//   dist/Gross Warp/gw_ui.js          the editor script, which the device loads from its own folder
//
//   node tools/build.js          write dist/
//   node tools/build.js --check  exit 1 if dist/ differs from a fresh build (the tests run this)
//
// .amxd = chunks of [4-char tag][uint32 LE size][data]: "ampf" = "aaaa" (audio effect), "meta" = 0, "ptch" = the
// patcher JSON + NUL. Same layout as Ableton's maxdevtools/maxdiff/amxd_textconv.py reads, and as its unfrozen
// audio-effect test devices carry.

"use strict";

const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(__dirname, "..");
const SRC = path.join(ROOT, "src");
const DIST = path.join(ROOT, "dist", "Gross Warp");
const DEVICE_NAME = "Gross Warp.amxd";
const UI_SCRIPT = "gw_ui.js";
const ENV_FRAMES = 8 * 2 * (1 + 3 * 128); // must match gw_ui.js and gross_warp.genexpr
const PEAK_FRAMES = 2 * 256;
const DEVICE_WIDTH = 474;
const APPVERSION = { major: 8, minor: 6, revision: 5, architecture: "x64", modernui: 1 };

// Inlets/outlets of the objects used, as Max reports them (Max recomputes them on load; lines use the indices).
const IO = {
    "plugin~": [2, ["signal", "signal"]],
    "plugout~": [2, ["signal", "signal"]],
    "phasor~": [2, ["signal"]],
    "gen~": [3, ["signal", "signal", "signal", "signal"]],
    "snapshot~": [2, ["float"]],
    prepend: [1, [""]],
    change: [1, ["", "int", "int"]],
    loadbang: [1, ["bang"]],
    t: [1, ["bang", "bang", "bang"]],
    "buffer~": [1, ["float", "bang"]],
    symbol: [2, [""]],
};

function makePatcher() {
    const boxes = [];
    const lines = [];
    let n = 0;

    function add(box) {
        n += 1;
        const id = `obj-${n}`;
        boxes.push({ box: Object.assign({ id }, box) });
        return id;
    }

    function newobj(text, x, y, w, extra) {
        const cls = text.split(" ")[0];
        const io = IO[cls];
        if (!io) {
            throw new Error(`build: no inlet/outlet info for ${cls}`);
        }
        const box = {
            maxclass: "newobj",
            numinlets: io[0],
            numoutlets: io[1].length,
            outlettype: io[1],
            patching_rect: [x, y, w, 22.0],
            text,
        };
        return add(Object.assign(box, extra || {}));
    }

    function message(text, x, y, w) {
        return add({
            maxclass: "message",
            numinlets: 2,
            numoutlets: 1,
            outlettype: [""],
            patching_rect: [x, y, w, 22.0],
            text,
        });
    }

    function connect(src, outlet, dst, inlet) {
        lines.push({ patchline: { destination: [dst, inlet], source: [src, outlet] } });
    }

    return { boxes, lines, add, newobj, message, connect };
}

function genPatcher(code) {
    const p = makePatcher();
    const ins = [1, 2, 3].map((i) =>
        p.add({
            maxclass: "newobj",
            numinlets: 0,
            numoutlets: 1,
            outlettype: [""],
            patching_rect: [40.0 + (i - 1) * 200, 20.0, 30.0, 22.0],
            text: `in ${i}`,
        }),
    );
    const codebox = p.add({
        code,
        fontface: 0,
        fontname: "<Monospaced>",
        fontsize: 12.0,
        maxclass: "codebox",
        numinlets: 3,
        numoutlets: 4,
        outlettype: ["", "", "", ""],
        patching_rect: [40.0, 60.0, 640.0, 560.0],
    });
    const outs = [1, 2, 3, 4].map((i) =>
        p.add({
            maxclass: "newobj",
            numinlets: 1,
            numoutlets: 0,
            patching_rect: [40.0 + (i - 1) * 160, 640.0, 37.0, 22.0],
            text: `out ${i}`,
        }),
    );
    ins.forEach((id, i) => p.connect(id, 0, codebox, i));
    outs.forEach((id, i) => p.connect(codebox, i, id, 0));
    return {
        fileversion: 1,
        appversion: APPVERSION,
        classnamespace: "dsp.gen",
        rect: [80.0, 120.0, 760.0, 720.0],
        boxes: p.boxes,
        lines: p.lines,
    };
}

function liveParam(maxclass, longname, shortname, valueof, rect, prect, extra) {
    const io = {
        "live.dial": [1, ["", "float"]],
        "live.tab": [1, ["", "", "float"]],
        "live.menu": [1, ["", "", "float"]],
    }[maxclass];
    return Object.assign(
        {
            maxclass,
            numinlets: io[0],
            numoutlets: io[1].length,
            outlettype: io[1],
            parameter_enable: 1,
            patching_rect: rect,
            presentation: 1,
            presentation_rect: prect,
            saved_attribute_attributes: {
                valueof: Object.assign(
                    { parameter_longname: longname, parameter_modmode: 0, parameter_shortname: shortname },
                    valueof,
                ),
            },
            varname: longname,
        },
        extra || {},
    );
}

function liveComment(text, prect) {
    return {
        maxclass: "live.comment",
        numinlets: 1,
        numoutlets: 0,
        patching_rect: [prect[0] + 700, prect[1] + 300, prect[2], prect[3]],
        presentation: 1,
        presentation_rect: prect,
        text,
        textjustification: 0,
    };
}

function buildPatcher(genCode) {
    const p = makePatcher();
    const { newobj, message, connect } = p;
    const params = {};

    // ---- audio ----
    const plugin = newobj("plugin~", 30, 30, 60);
    const phasor = newobj("phasor~ 1n @lock 1", 120, 30, 120);
    const gen = p.add({
        maxclass: "newobj",
        numinlets: 3,
        numoutlets: 4,
        outlettype: ["signal", "signal", "signal", "signal"],
        patcher: genPatcher(genCode),
        patching_rect: [30.0, 330.0, 260.0, 22.0],
        text: "gen~",
        varname: "gw_dsp",
    });
    const plugout = newobj("plugout~", 30, 420, 60);
    connect(plugin, 0, gen, 0);
    connect(plugin, 1, gen, 1);
    connect(phasor, 0, gen, 2);
    connect(gen, 0, plugout, 0);
    connect(gen, 1, plugout, 1);

    // ---- the editor ----
    const ui = p.add({
        border: 0,
        filename: UI_SCRIPT,
        maxclass: "jsui",
        numinlets: 1,
        numoutlets: 0,
        parameter_enable: 1,
        patching_rect: [330.0, 420.0, 404.0, 140.0],
        presentation: 1,
        presentation_rect: [4.0, 4.0, 404.0, 140.0],
        saved_attribute_attributes: {
            valueof: {
                parameter_invisible: 1,
                parameter_longname: "GW Envelopes",
                parameter_modmode: 0,
                parameter_shortname: "Envelopes",
                parameter_type: 3,
            },
        },
        varname: "gw_ui",
    });
    params[ui] = ["GW Envelopes", "Envelopes", 0];

    // playhead and the pattern now playing, back to the editor
    const snapPh = newobj("snapshot~ 33", 150, 380, 80);
    const prePh = newobj("prepend phase", 150, 405, 85);
    connect(gen, 2, snapPh, 0);
    connect(snapPh, 0, prePh, 0);
    connect(prePh, 0, ui, 0);
    const snapAct = newobj("snapshot~ 50", 250, 380, 80);
    const chg = newobj("change", 250, 405, 50);
    const preAct = newobj("prepend active", 250, 430, 90);
    connect(gen, 3, snapAct, 0);
    connect(snapAct, 0, chg, 0);
    connect(chg, 0, preAct, 0);
    connect(preAct, 0, ui, 0);

    // ---- load: size the buffers, hand their device-unique names (---) to gen~ and the editor, push parameters ----
    const lb = newobj("loadbang", 330, 30, 60);
    const trig = newobj("t b b b", 330, 60, 60);
    connect(lb, 0, trig, 0);

    const envBuf = newobj("buffer~ ---gwenv", 560, 150, 110);
    const peakBuf = newobj("buffer~ ---gwpeaks", 700, 150, 120);
    const sizeEnv = message(`sizeinsamps ${ENV_FRAMES}`, 560, 100, 110);
    const sizePeak = message(`sizeinsamps ${PEAK_FRAMES}`, 700, 100, 110);
    connect(trig, 2, sizeEnv, 0);
    connect(trig, 2, sizePeak, 0);
    connect(sizeEnv, 0, envBuf, 0);
    connect(sizePeak, 0, peakBuf, 0);

    const symEnv = newobj("symbol ---gwenv", 330, 100, 100);
    const symPeak = newobj("symbol ---gwpeaks", 450, 100, 110);
    connect(trig, 1, symEnv, 0);
    connect(trig, 1, symPeak, 0);
    const genEnv = newobj("prepend gwenv", 330, 200, 90);
    const genPeak = newobj("prepend gwpeaks", 450, 200, 100);
    const uiEnv = newobj("prepend envbuf", 330, 240, 90);
    const uiPeak = newobj("prepend peakbuf", 450, 240, 100);
    connect(symEnv, 0, genEnv, 0);
    connect(symEnv, 0, uiEnv, 0);
    connect(symPeak, 0, genPeak, 0);
    connect(symPeak, 0, uiPeak, 0);
    connect(genEnv, 0, gen, 0);
    connect(genPeak, 0, gen, 0);
    connect(uiEnv, 0, ui, 0);
    connect(uiPeak, 0, ui, 0);

    // ---- parameters ----
    const controls = [
        {
            id: p.add(
                liveParam(
                    "live.tab",
                    "Pattern",
                    "Pattern",
                    {
                        parameter_enum: ["1", "2", "3", "4", "5", "6", "7", "8"],
                        parameter_initial: [0],
                        parameter_initial_enable: 1,
                        parameter_mmax: 7,
                        parameter_type: 2,
                    },
                    [600.0, 230.0, 176.0, 16.0],
                    [4.0, 150.0, 176.0, 16.0],
                    { fontsize: 9.0, num_lines_patching: 1, num_lines_presentation: 1 },
                ),
            ),
            short: "Pattern",
            to: ["pattern", "ui"],
        },
        {
            id: p.add(
                liveParam(
                    "live.menu",
                    "Switch",
                    "Switch",
                    {
                        parameter_enum: ["Instant", "1/16", "Beat", "Pattern"],
                        parameter_initial: [3],
                        parameter_initial_enable: 1,
                        parameter_mmax: 3,
                        parameter_type: 2,
                    },
                    [800.0, 230.0, 66.0, 15.0],
                    [226.0, 150.0, 66.0, 15.0],
                ),
            ),
            short: "Switch",
            to: ["quant"],
        },
        {
            id: p.add(
                liveParam(
                    "live.menu",
                    "Length",
                    "Length",
                    {
                        parameter_enum: ["1 Beat", "2 Beats", "1 Bar"],
                        parameter_initial: [2],
                        parameter_initial_enable: 1,
                        parameter_mmax: 2,
                        parameter_type: 2,
                    },
                    [880.0, 230.0, 66.0, 15.0],
                    [340.0, 150.0, 66.0, 15.0],
                ),
            ),
            short: "Length",
            to: ["patlen", "ui"],
        },
        {
            id: p.add(
                liveParam(
                    "live.dial",
                    "Smooth",
                    "Smooth",
                    {
                        parameter_exponent: 3.0,
                        parameter_initial: [4.0],
                        parameter_initial_enable: 1,
                        parameter_mmax: 50.0,
                        parameter_mmin: 0.1,
                        parameter_type: 0,
                        parameter_unitstyle: 2,
                    },
                    [600.0, 280.0, 54.0, 46.0],
                    [416.0, 4.0, 54.0, 46.0],
                ),
            ),
            short: "Smooth",
            to: ["smoothms"],
        },
        {
            id: p.add(
                liveParam(
                    "live.dial",
                    "Depth",
                    "Depth",
                    {
                        parameter_initial: [100.0],
                        parameter_initial_enable: 1,
                        parameter_mmax: 100.0,
                        parameter_type: 0,
                        parameter_unitstyle: 5,
                    },
                    [670.0, 280.0, 54.0, 46.0],
                    [416.0, 58.0, 54.0, 46.0],
                ),
            ),
            short: "Depth",
            to: ["depth"],
        },
        {
            id: p.add(
                liveParam(
                    "live.dial",
                    "Dry/Wet",
                    "Dry/Wet",
                    {
                        parameter_initial: [100.0],
                        parameter_initial_enable: 1,
                        parameter_mmax: 100.0,
                        parameter_type: 0,
                        parameter_unitstyle: 5,
                    },
                    [740.0, 280.0, 54.0, 46.0],
                    [416.0, 112.0, 54.0, 46.0],
                ),
            ),
            short: "Dry/Wet",
            to: ["wet"],
        },
    ];

    let px = 600;
    for (const c of controls) {
        const box = p.boxes.find((b) => b.box.id === c.id).box;
        params[c.id] = [box.varname, c.short, 0];
        connect(trig, 0, c.id, 0); // push the value out at load
        for (const target of c.to) {
            if (target === "ui") {
                continue;
            }
            const pre = newobj(`prepend ${target}`, px, 350, 100);
            px += 110;
            connect(c.id, 0, pre, 0);
            connect(pre, 0, gen, 0);
            if (c.to.includes("ui")) {
                connect(pre, 0, ui, 0);
            }
        }
    }

    p.add(liveComment("Switch", [186.0, 150.0, 40.0, 16.0]));
    p.add(liveComment("Length", [298.0, 150.0, 40.0, 16.0]));

    params.parameterbanks = {
        0: {
            index: 0,
            name: "Gross Warp",
            parameters: ["Pattern", "Smooth", "Depth", "Dry/Wet", "Switch", "Length", "-", "-"],
        },
    };
    params.inherited_shortname = 1;

    return {
        patcher: {
            fileversion: 1,
            appversion: APPVERSION,
            classnamespace: "box",
            rect: [80.0, 100.0, 1100.0, 720.0],
            openrect: [0.0, 0.0, DEVICE_WIDTH, 169.0],
            bglocked: 0,
            openinpresentation: 1,
            default_fontsize: 10.0,
            default_fontface: 0,
            default_fontname: "Arial Bold",
            gridonopen: 1,
            gridsize: [8.0, 8.0],
            gridsnaponopen: 1,
            objectsnaponopen: 1,
            statusbarvisible: 2,
            toolbarvisible: 1,
            lefttoolbarpinned: 0,
            toptoolbarpinned: 0,
            righttoolbarpinned: 0,
            bottomtoolbarpinned: 0,
            toolbars_unpinned_last_save: 0,
            tallnewobj: 0,
            boxanimatetime: 500,
            enablehscroll: 1,
            enablevscroll: 1,
            devicewidth: DEVICE_WIDTH,
            description: "Gross Beat-style time and volume envelopes, eight patterns, locked to Live's transport.",
            digest: "",
            tags: "",
            style: "",
            subpatcher_template: "",
            assistshowspatchername: 0,
            boxes: p.boxes,
            lines: p.lines,
            parameters: params,
            dependency_cache: [],
            latency: 0,
            is_mpe: 0,
            minimum_live_version: "",
            minimum_max_version: "",
            platform_compatibility: 0,
            autosave: 0,
        },
    };
}

function chunk(tag, data) {
    const head = Buffer.alloc(8);
    head.write(tag, 0, "ascii");
    head.writeUInt32LE(data.length, 4);
    return Buffer.concat([head, data]);
}

function toAmxd(patcher) {
    const json = Buffer.from(`${JSON.stringify(patcher, null, "\t")}\n`, "utf8");
    return Buffer.concat([
        chunk("ampf", Buffer.from("aaaa", "ascii")),
        chunk("meta", Buffer.alloc(4)),
        chunk("ptch", Buffer.concat([json, Buffer.from([0])])),
    ]);
}

// Reads chunks the way maxdiff's amxd_textconv.py does; returns { type, patcher }.
function readAmxd(bytes) {
    let i = 0;
    const out = {};
    while (i + 8 <= bytes.length) {
        const tag = bytes.toString("ascii", i, i + 4);
        const size = bytes.readUInt32LE(i + 4);
        const data = bytes.subarray(i + 8, i + 8 + size);
        if (tag === "ampf") {
            out.type = data.toString("ascii");
        } else if (tag === "ptch") {
            const end = data[data.length - 1] === 0 ? data.length - 1 : data.length;
            out.patcher = JSON.parse(data.subarray(0, end).toString("utf8"));
        } else if (tag !== "meta") {
            throw new Error(`amxd: unknown chunk ${tag}`);
        }
        i += 8 + size;
    }
    return out;
}

function build() {
    const code = fs.readFileSync(path.join(SRC, "gross_warp.genexpr"), "utf8");
    return {
        [DEVICE_NAME]: toAmxd(buildPatcher(code)),
        [UI_SCRIPT]: fs.readFileSync(path.join(SRC, UI_SCRIPT)),
    };
}

function main() {
    const files = build();
    const check = process.argv.includes("--check");
    let stale = false;
    for (const [name, bytes] of Object.entries(files)) {
        const target = path.join(DIST, name);
        if (check) {
            if (!fs.existsSync(target) || !fs.readFileSync(target).equals(bytes)) {
                console.error(`stale: ${path.relative(ROOT, target)} (run: node tools/build.js)`);
                stale = true;
            }
        } else {
            fs.mkdirSync(DIST, { recursive: true });
            fs.writeFileSync(target, bytes);
            console.log(`wrote ${path.relative(ROOT, target)} (${bytes.length} bytes)`);
        }
    }
    if (stale) {
        process.exit(1);
    }
}

if (require.main === module) {
    main();
}

module.exports = { build, buildPatcher, toAmxd, readAmxd, ENV_FRAMES, PEAK_FRAMES, DIST, DEVICE_NAME, UI_SCRIPT };
