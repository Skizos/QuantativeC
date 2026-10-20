#!/usr/bin/env node
// Builds the Gross Warp Max for Live devices from src/:
//   dist/Gross Warp/Gross Warp.amxd        the audio effect (gen~ code embedded in its codebox)
//   dist/Gross Warp/Gross Warp MIDI.amxd   the MIDI effect that triggers its patterns from notes
//   dist/Gross Warp/gw_ui.js               the editor script, which the audio effect loads from its own folder
//   dist/Gross Warp/INSTALL.txt            how to install
//   dist/GrossWarp.zip                     the folder above, for downloading
//
//   node tools/build.js          write dist/
//   node tools/build.js --check  exit 1 if dist/ differs from a fresh build (the tests run this)
//
// .amxd = chunks of [4-char tag][uint32 LE size][data]: "ampf" = device type ("aaaa" audio effect, "mmmm" MIDI
// effect), "meta" = 0, "ptch" = the patcher JSON + NUL. Same layout as Ableton's maxdevtools/maxdiff/amxd_textconv.py
// reads, and as its unfrozen test devices carry. The zip is stored (no compression) with a fixed date, so a rebuild is
// byte-identical.

"use strict";

const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(__dirname, "..");
const SRC = path.join(ROOT, "src");
const DIST_ROOT = path.join(ROOT, "dist");
const FOLDER = "Gross Warp";
const DIST = path.join(DIST_ROOT, FOLDER);
const DEVICE_NAME = "Gross Warp.amxd";
const MIDI_DEVICE_NAME = "Gross Warp MIDI.amxd";
const UI_SCRIPT = "gw_ui.js";
const INSTALL = "INSTALL.txt";
const ZIP_NAME = "GrossWarp.zip";
const ENV_FRAMES = 8 * 2 * (1 + 3 * 128); // must match gw_ui.js and gross_warp.genexpr
const PEAK_FRAMES = 2 * 256;
const DEVICE_WIDTH = 540;
const MIDI_DEVICE_WIDTH = 200;
const LINKS = ["A", "B", "C", "D"];
const LINK_SEND = (l) => `grosswarp_${l}`; // global names: they connect devices
const APPVERSION = { major: 8, minor: 6, revision: 5, architecture: "x64", modernui: 1 };

// Inlets and outlets of the objects used, as Max reports them (Max recomputes them on load; cords use the indices).
function io(text) {
    const [cls, ...args] = text.split(" ");
    const fixed = {
        "plugin~": [2, ["signal", "signal"]],
        "plugout~": [2, ["signal", "signal"]],
        "phasor~": [2, ["signal"]],
        "snapshot~": [2, ["float"]],
        prepend: [1, [""]],
        change: [1, ["", "int", "int"]],
        loadbang: [1, ["bang"]],
        "buffer~": [1, ["float", "bang"]],
        symbol: [2, [""]],
        "live.thisdevice": [1, ["bang", "int", "int"]],
        "live.path": [1, ["", "", ""]],
        "live.observer": [2, ["", ""]],
        r: [0, [""]],
        s: [1, []],
        midiin: [1, ["int"]],
        midiout: [1, []],
        midiparse: [1, ["", "", "", "int", "int", "", "int", ""]],
        "==": [2, ["int"]],
        ">": [2, ["int"]],
        "+": [2, ["int"]],
        "-": [2, ["int"]],
        "*": [2, ["int"]],
        split: [3, ["", ""]],
        counter: [5, ["int", "", "", "int"]],
    };
    if (fixed[cls]) {
        return fixed[cls];
    }
    if (cls === "t") {
        return [1, args.map((a) => ({ b: "bang", i: "int", f: "float", l: "" })[a])];
    }
    if (cls === "unpack") {
        return [1, args.map(() => "int")];
    }
    if (cls === "gate") {
        return [2, Array(Number(args[0])).fill("")];
    }
    if (cls === "switch") {
        return [Number(args[0]) + 1, [""]];
    }
    throw new Error(`build: no inlet/outlet info for ${cls}`);
}

function makePatcher() {
    const boxes = [];
    const lines = [];
    const params = {};
    let n = 0;

    function add(box) {
        n += 1;
        const id = `obj-${n}`;
        boxes.push({ box: Object.assign({ id }, box) });
        if (box.parameter_enable === 1) {
            const v = box.saved_attribute_attributes.valueof;
            params[id] = [v.parameter_longname, v.parameter_shortname, 0];
        }
        return id;
    }

    function newobj(text, x, y, w, extra) {
        const [inlets, outlets] = io(text);
        const box = {
            maxclass: "newobj",
            numinlets: inlets,
            numoutlets: outlets.length,
            patching_rect: [x, y, w, 22.0],
            text,
        };
        if (outlets.length) {
            box.outlettype = outlets;
        }
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

    return { boxes, lines, params, add, newobj, message, connect };
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

const LIVE_IO = {
    "live.dial": [1, ["", "float"]],
    "live.tab": [1, ["", "", "float"]],
    "live.menu": [1, ["", "", "float"]],
    "live.numbox": [1, ["", "float"]],
};

// Shown in Live's Info View when the pointer is over a control.
const INFO = {
    Pattern: "The pattern that plays and that the editor shows. Automate it to switch effects during the song.",
    Switch: "When a new pattern takes over: at once, or at the next 1/16, beat, bar or end of the pattern.",
    Length: "How long one pattern is: 1 beat up to 4 bars, in the song's time signature.",
    Smooth: "Cross-fade time at steps in the time envelope, and slew of the volume envelope. Raise it if steps click.",
    Depth: "How much of both envelopes is applied. 0 % = no effect.",
    "Dry/Wet": "Mix of the processed and the original sound.",
    "Pitch Lock": "Lock: time effects (half time, stops, reverses) keep the original pitch, using short grains.",
    Grain: "Grain size for Pitch Lock: short for drums, long for pads and vocals.",
    "MIDI Link": "Follow the notes of a Gross Warp MIDI device set to the same link (A-D). Off: ignore MIDI.",
    Link: "Which Gross Warps follow these notes: the ones whose MIDI is set to the same letter.",
    Mode: "Hold: a pattern plays while its note is held. Latch: a note switches to it, the same note again releases it.",
    "Base Note": "The note for pattern 1; the next seven notes play patterns 2 to 8.",
    Thru: "Pass the MIDI on to the next device on this track.",
};

function liveParam(maxclass, longname, shortname, valueof, rect, prect, extra) {
    const [inlets, outlets] = LIVE_IO[maxclass];
    const info = INFO[longname] ? { annotation: INFO[longname], annotation_name: longname } : {};
    return Object.assign(
        info,
        {
            maxclass,
            numinlets: inlets,
            numoutlets: outlets.length,
            outlettype: outlets,
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

function enumParam(items, initial) {
    return {
        parameter_enum: items,
        parameter_initial: [initial],
        parameter_initial_enable: 1,
        parameter_mmax: items.length - 1,
        parameter_type: 2,
    };
}

function tabExtra() {
    return { fontsize: 9.0, num_lines_patching: 1, num_lines_presentation: 1 };
}

function liveComment(text, prect, patchXY) {
    return {
        maxclass: "live.comment",
        numinlets: 1,
        numoutlets: 0,
        patching_rect: [patchXY[0], patchXY[1], prect[2], prect[3]],
        presentation: 1,
        presentation_rect: prect,
        text,
        textjustification: 0,
    };
}

function topPatcher(p, width, description, banks) {
    const params = Object.assign({}, p.params, {
        parameterbanks: { 0: { index: 0, name: banks.name, parameters: banks.parameters } },
        inherited_shortname: 1,
    });
    return {
        patcher: {
            fileversion: 1,
            appversion: APPVERSION,
            classnamespace: "box",
            rect: [80.0, 100.0, 1240.0, 760.0],
            openrect: [0.0, 0.0, width, 169.0],
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
            devicewidth: width,
            description,
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

// ---------------------------------------------------------------------------------------------------------------
// The audio effect

function buildPatcher(genCode) {
    const p = makePatcher();
    const { newobj, message, connect } = p;

    // ---- audio ----
    const plugin = newobj("plugin~", 30, 30, 60);
    const phasor = newobj("phasor~ 4n @lock 1", 120, 30, 120);
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
        patching_rect: [330.0, 470.0, 404.0, 140.0],
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

    // ---- Live API: the song position (to count bars) and the time signature ----
    const thisDevice = newobj("live.thisdevice", 880, 30, 90);
    const pathMsg = message("path live_set", 880, 60, 80);
    const livePath = newobj("live.path", 880, 90, 60);
    const idTrig = newobj("t b l", 880, 120, 40);
    connect(thisDevice, 0, pathMsg, 0);
    connect(pathMsg, 0, livePath, 0);
    connect(livePath, 0, idTrig, 0);
    const observed = [
        ["current_song_time", "songbeats", false],
        ["signature_numerator", "signum", true],
        ["signature_denominator", "sigden", true],
    ];
    observed.forEach(([property, param, toUi], i) => {
        const x = 880 + i * 170;
        const prop = message(`property ${property}`, x, 160, 160);
        const obs = newobj("live.observer", x, 190, 90, { saved_object_attributes: { _persistence: 0 } });
        const pre = newobj(`prepend ${param}`, x, 220, 100);
        connect(idTrig, 1, obs, 1); // the id first (a trigger fires right to left), then the property
        connect(idTrig, 0, prop, 0);
        connect(prop, 0, obs, 0);
        connect(obs, 0, pre, 0);
        connect(pre, 0, gen, 0);
        if (toUi) {
            connect(pre, 0, ui, 0);
        }
    });

    // ---- pattern triggers from Gross Warp MIDI: the MIDI menu picks which link (A-D) is listened to ----
    const linkSwitch = newobj(`switch ${LINKS.length}`, 880, 300, 120);
    LINKS.forEach((l, i) => {
        const r = newobj(`r ${LINK_SEND(l)}`, 880 + i * 110, 270, 100);
        connect(r, 0, linkSwitch, i + 1);
    });
    connect(linkSwitch, 0, gen, 0);

    // ---- parameters ----
    const P = (box) => p.add(box);
    const controls = [
        {
            id: P(
                liveParam(
                    "live.tab",
                    "Pattern",
                    "Pattern",
                    enumParam(["1", "2", "3", "4", "5", "6", "7", "8"], 0),
                    [600.0, 520.0, 176.0, 16.0],
                    [4.0, 150.0, 176.0, 16.0],
                    tabExtra(),
                ),
            ),
            to: ["pattern"],
            ui: true,
        },
        {
            id: P(
                liveParam(
                    "live.menu",
                    "Switch",
                    "Switch",
                    enumParam(["Instant", "1/16", "Beat", "Bar", "Pattern"], 4),
                    [800.0, 520.0, 66.0, 15.0],
                    [226.0, 150.0, 66.0, 15.0],
                ),
            ),
            to: ["quant"],
        },
        {
            id: P(
                liveParam(
                    "live.menu",
                    "Length",
                    "Length",
                    enumParam(["1 Beat", "2 Beats", "1 Bar", "2 Bars", "4 Bars"], 2),
                    [880.0, 520.0, 66.0, 15.0],
                    [340.0, 150.0, 66.0, 15.0],
                ),
            ),
            to: ["patlen"],
            ui: true,
        },
        {
            id: P(
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
                    [600.0, 570.0, 54.0, 46.0],
                    [414.0, 4.0, 54.0, 46.0],
                ),
            ),
            to: ["smoothms"],
        },
        {
            id: P(
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
                    [670.0, 570.0, 54.0, 46.0],
                    [414.0, 58.0, 54.0, 46.0],
                ),
            ),
            to: ["depth"],
        },
        {
            id: P(
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
                    [740.0, 570.0, 54.0, 46.0],
                    [414.0, 112.0, 54.0, 46.0],
                ),
            ),
            to: ["wet"],
        },
        {
            id: P(
                liveParam(
                    "live.tab",
                    "Pitch Lock",
                    "Pitch",
                    enumParam(["Off", "Lock"], 0),
                    [810.0, 570.0, 58.0, 15.0],
                    [476.0, 18.0, 58.0, 15.0],
                    tabExtra(),
                ),
            ),
            to: ["pitchlock"],
        },
        {
            id: P(
                liveParam(
                    "live.dial",
                    "Grain",
                    "Grain",
                    {
                        parameter_exponent: 2.0,
                        parameter_initial: [60.0],
                        parameter_initial_enable: 1,
                        parameter_mmax: 250.0,
                        parameter_mmin: 10.0,
                        parameter_type: 0,
                        parameter_unitstyle: 2,
                    },
                    [880.0, 570.0, 54.0, 46.0],
                    [478.0, 40.0, 54.0, 46.0],
                ),
            ),
            to: ["grainms"],
        },
        {
            id: P(
                liveParam(
                    "live.menu",
                    "MIDI Link",
                    "MIDI",
                    enumParam(["Off"].concat(LINKS), 0),
                    [950.0, 570.0, 58.0, 15.0],
                    [476.0, 112.0, 58.0, 15.0],
                ),
            ),
            to: [],
            midi: true,
        },
    ];

    let px = 600;
    for (const c of controls) {
        connect(trig, 0, c.id, 0); // push the value out at load
        for (const target of c.to) {
            const pre = newobj(`prepend ${target}`, px, 640, 100);
            px += 110;
            connect(c.id, 0, pre, 0);
            connect(pre, 0, gen, 0);
            if (c.ui) {
                connect(pre, 0, ui, 0);
            }
        }
        if (c.midi) {
            // 0 = off; 1-4 open the switch's inlet for that link; any change also clears a held MIDI pattern
            const fan = newobj("t i i", px, 640, 40);
            const on = newobj("> 0", px, 670, 30);
            const pre = newobj("prepend midien", px, 700, 100);
            connect(c.id, 0, fan, 0);
            connect(fan, 1, on, 0);
            connect(on, 0, pre, 0);
            connect(pre, 0, gen, 0);
            connect(fan, 0, linkSwitch, 0);
        }
    }

    p.add(liveComment("Switch", [186.0, 150.0, 40.0, 16.0], [700, 760]));
    p.add(liveComment("Length", [298.0, 150.0, 40.0, 16.0], [760, 760]));
    p.add(liveComment("Pitch", [476.0, 2.0, 58.0, 15.0], [820, 760]));
    p.add(liveComment("MIDI", [476.0, 96.0, 58.0, 15.0], [880, 760]));

    return topPatcher(
        p,
        DEVICE_WIDTH,
        "Gross Beat-style time and volume envelopes, eight patterns, locked to Live's transport.",
        {
            name: "Gross Warp",
            parameters: ["Pattern", "Smooth", "Depth", "Dry/Wet", "Switch", "Length", "Pitch Lock", "Grain"],
        },
    );
}

// ---------------------------------------------------------------------------------------------------------------
// The MIDI effect: notes from Base upwards trigger patterns 1-8 of every Gross Warp listening to the same link.
// Native objects only, so the triggers stay in the scheduler thread (a js object would defer them).
// Each event is sent as "trigon v" / "trigoff v" with v = pattern + 8 * parity + 16 * mode: the parity alternates
// per event so gen~ sees a change even when the same note repeats; gen~ applies hold/latch (gross_warp.genexpr).

function buildMidiPatcher() {
    const p = makePatcher();
    const { newobj, connect } = p;

    const midiIn = newobj("midiin", 30, 30, 50);
    const thru = newobj("gate 1 1", 30, 300, 60);
    const midiOut = newobj("midiout", 30, 330, 55);
    connect(midiIn, 0, thru, 1);
    connect(thru, 0, midiOut, 0);

    const parse = newobj("midiparse", 120, 60, 70);
    const notes = newobj("unpack 0 0", 120, 90, 70);
    const isOff = newobj("== 0", 220, 120, 40);
    const path = newobj("+ 1", 220, 150, 40);
    const onOff = newobj("gate 2", 120, 180, 50);
    connect(midiIn, 0, parse, 0);
    connect(parse, 0, notes, 0);
    connect(notes, 1, isOff, 0); // velocity first (unpack fires right to left) picks the path...
    connect(isOff, 0, path, 0);
    connect(path, 0, onOff, 0); // ... 1 = note on, 2 = note off
    connect(notes, 0, onOff, 1); // then the pitch takes it

    const linkGate = newobj(`gate ${LINKS.length}`, 120, 420, 80);
    LINKS.forEach((l, i) => {
        const s = newobj(`s ${LINK_SEND(l)}`, 120 + i * 110, 450, 100);
        connect(linkGate, i, s, 0);
    });

    const offsets = [];
    const modeTimes16 = newobj("* 16", 520, 240, 40);
    ["trigon", "trigoff"].forEach((name, i) => {
        const x = 120 + i * 200;
        const minus = newobj("- 36", x, 210, 40);
        const range = newobj("split 0 7", x, 240, 60);
        const fan = newobj("t i b", x, 270, 40);
        const parity = newobj("counter 0 1", x + 60, 300, 70);
        const times8 = newobj("* 8", x + 60, 330, 30);
        const addParity = newobj("+ 0", x, 360, 40);
        connect(onOff, i, minus, 0);
        connect(minus, 0, range, 0);
        connect(range, 0, fan, 0);
        connect(fan, 1, parity, 0); // parity first...
        connect(parity, 0, times8, 0);
        connect(times8, 0, addParity, 1);
        connect(fan, 0, addParity, 0); // ... then the pattern number
        let last = addParity;
        if (name === "trigon") {
            const addMode = newobj("+ 0", x, 380, 40);
            connect(addParity, 0, addMode, 0);
            connect(modeTimes16, 0, addMode, 1);
            last = addMode;
        }
        const pre = newobj(`prepend ${name}`, x, 400, 90);
        connect(last, 0, pre, 0);
        connect(pre, 0, linkGate, 1);
        offsets.push(minus);
    });

    const lb = newobj("loadbang", 600, 30, 60);
    const push = newobj("t b", 600, 60, 30);
    connect(lb, 0, push, 0);

    const link = p.add(
        liveParam("live.menu", "Link", "Link", enumParam(LINKS, 0), [600.0, 100.0, 60.0, 15.0], [50.0, 24.0, 60.0, 15.0]),
    );
    const linkPlus = newobj("+ 1", 600, 130, 40);
    connect(link, 0, linkPlus, 0);
    connect(linkPlus, 0, linkGate, 0);

    const mode = p.add(
        liveParam(
            "live.tab",
            "Mode",
            "Mode",
            enumParam(["Hold", "Latch"], 0),
            [680.0, 100.0, 90.0, 15.0],
            [50.0, 46.0, 90.0, 15.0],
            tabExtra(),
        ),
    );
    connect(mode, 0, modeTimes16, 0);

    const base = p.add(
        liveParam(
            "live.numbox",
            "Base Note",
            "Base",
            {
                parameter_initial: [36],
                parameter_initial_enable: 1,
                parameter_mmax: 120.0,
                parameter_type: 1,
                parameter_unitstyle: 8,
            },
            [780.0, 100.0, 60.0, 15.0],
            [50.0, 68.0, 60.0, 15.0],
        ),
    );
    offsets.forEach((minus) => connect(base, 0, minus, 1));

    const thruParam = p.add(
        liveParam(
            "live.tab",
            "Thru",
            "Thru",
            enumParam(["Off", "On"], 1),
            [860.0, 100.0, 90.0, 15.0],
            [50.0, 90.0, 90.0, 15.0],
            tabExtra(),
        ),
    );
    connect(thruParam, 0, thru, 0);

    for (const id of [link, mode, base, thruParam]) {
        connect(push, 0, id, 0);
    }

    p.add(liveComment("Notes trigger Gross Warp patterns", [4.0, 2.0, 192.0, 16.0], [600, 180]));
    p.add(liveComment("Link", [4.0, 24.0, 44.0, 15.0], [600, 200]));
    p.add(liveComment("Mode", [4.0, 46.0, 44.0, 15.0], [650, 200]));
    p.add(liveComment("Base", [4.0, 68.0, 44.0, 15.0], [700, 200]));
    p.add(liveComment("Thru", [4.0, 90.0, 44.0, 15.0], [750, 200]));
    p.add(liveComment("Base = pattern 1 ... Base + 7 = 8", [4.0, 112.0, 192.0, 16.0], [600, 220]));
    p.add(liveComment("Set Gross Warp's MIDI to the same link", [4.0, 130.0, 192.0, 16.0], [600, 240]));

    return topPatcher(p, MIDI_DEVICE_WIDTH, "Plays Gross Warp patterns from MIDI notes (hold or latch).", {
        name: "Gross Warp MIDI",
        parameters: ["Link", "Mode", "Base Note", "Thru", "-", "-", "-", "-"],
    });
}

// ---------------------------------------------------------------------------------------------------------------
// Containers

function chunk(tag, data) {
    const head = Buffer.alloc(8);
    head.write(tag, 0, "ascii");
    head.writeUInt32LE(data.length, 4);
    return Buffer.concat([head, data]);
}

function toAmxd(patcher, type) {
    const json = Buffer.from(`${JSON.stringify(patcher, null, "\t")}\n`, "utf8");
    return Buffer.concat([
        chunk("ampf", Buffer.from(type || "aaaa", "ascii")),
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

const CRC_TABLE = (() => {
    const t = new Uint32Array(256);
    for (let n = 0; n < 256; n++) {
        let c = n;
        for (let k = 0; k < 8; k++) {
            c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
        }
        t[n] = c >>> 0;
    }
    return t;
})();

function crc32(buf) {
    let c = 0xffffffff;
    for (const b of buf) {
        c = CRC_TABLE[(c ^ b) & 0xff] ^ (c >>> 8);
    }
    return (c ^ 0xffffffff) >>> 0;
}

// A stored (uncompressed) zip: [name, bytes] entries; names ending in "/" are folders. Fixed date for reproducibility.
function toZip(entries) {
    const DOS_TIME = 12 << 11; // 12:00:00
    const DOS_DATE = ((2026 - 1980) << 9) | (10 << 5) | 20; // 2026-10-20
    const locals = [];
    const centrals = [];
    let offset = 0;
    for (const [name, data] of entries) {
        const nameBytes = Buffer.from(name, "utf8");
        const isDir = name.endsWith("/");
        const crc = crc32(data);
        const local = Buffer.alloc(30);
        local.writeUInt32LE(0x04034b50, 0);
        local.writeUInt16LE(20, 4);
        local.writeUInt16LE(0x0800, 6); // UTF-8 names
        local.writeUInt16LE(0, 8); // stored
        local.writeUInt16LE(DOS_TIME, 10);
        local.writeUInt16LE(DOS_DATE, 12);
        local.writeUInt32LE(crc, 14);
        local.writeUInt32LE(data.length, 18);
        local.writeUInt32LE(data.length, 22);
        local.writeUInt16LE(nameBytes.length, 26);
        local.writeUInt16LE(0, 28);
        locals.push(local, nameBytes, data);
        const central = Buffer.alloc(46);
        central.writeUInt32LE(0x02014b50, 0);
        central.writeUInt16LE(0x0314, 4); // made by: unix, 2.0
        central.writeUInt16LE(20, 6);
        central.writeUInt16LE(0x0800, 8);
        central.writeUInt16LE(0, 10);
        central.writeUInt16LE(DOS_TIME, 12);
        central.writeUInt16LE(DOS_DATE, 14);
        central.writeUInt32LE(crc, 16);
        central.writeUInt32LE(data.length, 20);
        central.writeUInt32LE(data.length, 24);
        central.writeUInt16LE(nameBytes.length, 28);
        central.writeUInt16LE(0, 30);
        central.writeUInt16LE(0, 32);
        central.writeUInt16LE(0, 34);
        central.writeUInt16LE(0, 36);
        // unix mode in the high 16 bits (folder 755, file 644), MS-DOS folder flag in the low ones
        central.writeUInt32LE(isDir ? ((0o40755 << 16) | 0x10) >>> 0 : (0o100644 << 16) >>> 0, 38);
        central.writeUInt32LE(offset, 42);
        centrals.push(central, nameBytes);
        offset += 30 + nameBytes.length + data.length;
    }
    const cd = Buffer.concat(centrals);
    const end = Buffer.alloc(22);
    end.writeUInt32LE(0x06054b50, 0);
    end.writeUInt16LE(0, 4);
    end.writeUInt16LE(0, 6);
    end.writeUInt16LE(entries.length, 8);
    end.writeUInt16LE(entries.length, 10);
    end.writeUInt32LE(cd.length, 12);
    end.writeUInt32LE(offset, 16);
    end.writeUInt16LE(0, 20);
    return Buffer.concat([...locals, cd, end]);
}

// Reads a zip written by toZip (and checks every CRC); returns { name: bytes }.
function readZip(bytes) {
    const endAt = bytes.length - 22;
    if (bytes.readUInt32LE(endAt) !== 0x06054b50) {
        throw new Error("zip: no end record");
    }
    const count = bytes.readUInt16LE(endAt + 10);
    let at = bytes.readUInt32LE(endAt + 16);
    const out = {};
    for (let i = 0; i < count; i++) {
        if (bytes.readUInt32LE(at) !== 0x02014b50) {
            throw new Error("zip: bad central entry");
        }
        const crc = bytes.readUInt32LE(at + 16);
        const size = bytes.readUInt32LE(at + 24);
        const nameLen = bytes.readUInt16LE(at + 28);
        const extra = bytes.readUInt16LE(at + 30);
        const comment = bytes.readUInt16LE(at + 32);
        const local = bytes.readUInt32LE(at + 42);
        const name = bytes.toString("utf8", at + 46, at + 46 + nameLen);
        const dataAt = local + 30 + bytes.readUInt16LE(local + 26) + bytes.readUInt16LE(local + 28);
        const data = bytes.subarray(dataAt, dataAt + size);
        if (crc32(data) !== crc) {
            throw new Error(`zip: CRC mismatch for ${name}`);
        }
        out[name] = data;
        at += 46 + nameLen + extra + comment;
    }
    return out;
}

// ---------------------------------------------------------------------------------------------------------------

function build() {
    const code = fs.readFileSync(path.join(SRC, "gross_warp.genexpr"), "utf8");
    const folder = {
        [DEVICE_NAME]: toAmxd(buildPatcher(code), "aaaa"),
        [MIDI_DEVICE_NAME]: toAmxd(buildMidiPatcher(), "mmmm"),
        [UI_SCRIPT]: fs.readFileSync(path.join(SRC, UI_SCRIPT)),
        [INSTALL]: fs.readFileSync(path.join(SRC, INSTALL)),
    };
    const zipEntries = [[`${FOLDER}/`, Buffer.alloc(0)]].concat(
        Object.entries(folder).map(([name, bytes]) => [`${FOLDER}/${name}`, bytes]),
    );
    const files = {};
    for (const [name, bytes] of Object.entries(folder)) {
        files[path.join(FOLDER, name)] = bytes;
    }
    files[ZIP_NAME] = toZip(zipEntries);
    return files;
}

function main() {
    const files = build();
    const check = process.argv.includes("--check");
    let stale = false;
    for (const [name, bytes] of Object.entries(files)) {
        const target = path.join(DIST_ROOT, name);
        if (check) {
            if (!fs.existsSync(target) || !fs.readFileSync(target).equals(bytes)) {
                console.error(`stale: ${path.relative(ROOT, target)} (run: node tools/build.js)`);
                stale = true;
            }
        } else {
            fs.mkdirSync(path.dirname(target), { recursive: true });
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

module.exports = {
    build,
    buildPatcher,
    buildMidiPatcher,
    toAmxd,
    readAmxd,
    toZip,
    readZip,
    crc32,
    ENV_FRAMES,
    PEAK_FRAMES,
    DIST,
    DIST_ROOT,
    FOLDER,
    DEVICE_NAME,
    MIDI_DEVICE_NAME,
    UI_SCRIPT,
    INSTALL,
    ZIP_NAME,
    LINKS,
    LINK_SEND,
};
