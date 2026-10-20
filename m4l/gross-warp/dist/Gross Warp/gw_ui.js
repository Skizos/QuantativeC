// Gross Warp: envelope editor and display (jsui, mgraphics).
//
// ES5 only: Max's js/jsui engine predates ES6 (no let/const, arrows, template strings). The tests also run this file
// under node with stubs for Max's globals, so Max objects (mgraphics, Buffer, Global, box) are touched only inside
// functions, never while the file loads (except the guarded mgraphics.init below).
//
// Data: 8 patterns x 2 envelopes (0 = time, 1 = volume). An envelope is a list of points {x, u, c}:
//   x 0..1 across the pattern; the first point is at 0, the last at 1; sorted; two points may share an x (a step);
//   u 0..1 from the top of the editor (time: read offset in patterns; volume: 1 - gain);
//   c -1..1, the bend of the segment that starts at the point.
// Every change is mirrored into buffer~ ---gwenv, which gen~ reads (layout in gross_warp.genexpr). The whole state is
// this jsui's blob parameter, so Live saves it with the set (getvalueof / setvalueof).

autowatch = 1;
inlets = 1;
outlets = 0;

var MAX_PTS = 128;
var NUM_PAT = 8;
var BLOCK = 1 + 3 * MAX_PTS;
var ENV_FRAMES = NUM_PAT * 2 * BLOCK;
var PEAK_BINS = 256;
var BLOB_MAGIC = 4757;
var BLOB_VERSION = 1;
var UNDO_DEPTH = 64;
var EPS = 1e-9;

var GRIDS = [
    { name: "Off", div: 0 },
    { name: "1/4", div: 4 },
    { name: "1/8", div: 8 },
    { name: "1/16", div: 16 },
    { name: "1/32", div: 32 },
    { name: "1/8T", div: 12 },
    { name: "1/16T", div: 24 }
];
var LENGTH_NAMES = ["1 Beat", "2 Beats", "1 Bar", "2 Bars", "4 Bars"];
var ENV_NAMES = ["Time", "Volume"];

// ---------------------------------------------------------------------------------------------------------------
// Envelope math (shared with gen~: shape, segment choice and evaluation must match gross_warp.genexpr exactly)

function clamp(v, lo, hi) {
    return v < lo ? lo : (v > hi ? hi : v);
}

function near(a, b) {
    return Math.abs(a - b) < EPS;
}

function pt(x, u, c) {
    return { x: x, u: u, c: c || 0 };
}

function copyPts(pts) {
    var out = [];
    for (var i = 0; i < pts.length; i++) {
        out.push(pt(pts[i].x, pts[i].u, pts[i].c));
    }
    return out;
}

// Bend of a segment: c = 1/3 is t^2, a linear speed ramp in the time envelope (tape stop); c = -1/3 its mirror.
function shape(t, c) {
    var e = 1 + 3 * Math.abs(c);
    if (c < 0) {
        return 1 - Math.pow(1 - t, e);
    }
    return Math.pow(t, e);
}

// The segment gen~ uses at x: the last s <= n - 2 with pts[s].x <= x (right-continuous at steps).
function segmentAt(pts, x) {
    var s = 0;
    while (s < pts.length - 2 && x >= pts[s + 1].x) {
        s++;
    }
    return s;
}

function evalSeg(pts, s, x) {
    var a = pts[s];
    var b = pts[s + 1];
    var w = b.x - a.x;
    var t = w > 0.0000001 ? clamp((x - a.x) / w, 0, 1) : 1;
    return clamp(a.u + (b.u - a.u) * shape(t, a.c), 0, 1);
}

function evalEnv(pts, x) {
    return evalSeg(pts, segmentAt(pts, x), x);
}

// The level just left of x: where a step at x starts from.
function evalLeft(pts, x) {
    if (x <= 0) {
        return pts[0].u;
    }
    var i = 0;
    while (i < pts.length - 2 && pts[i + 1].x < x) {
        i++;
    }
    return evalSeg(pts, i, x);
}

// Sorted, clamped, endpoints at 0 and 1, at most two points per x, no points that change nothing, <= MAX_PTS.
function cleanEnv(src) {
    var pts = [];
    var i;
    for (i = 0; i < src.length; i++) {
        var p = src[i];
        if (!p || !isFinite(p.x) || !isFinite(p.u)) {
            continue;
        }
        pts.push({ x: clamp(p.x, 0, 1), u: clamp(p.u, 0, 1), c: clamp(isFinite(p.c) ? p.c : 0, -1, 1), k: i });
    }
    if (pts.length === 0) {
        return [pt(0, 0, 0), pt(1, 0, 0)];
    }
    pts.sort(function (a, b) {
        return a.x - b.x || a.k - b.k;
    });
    if (pts[0].x > 0) {
        pts.unshift({ x: 0, u: pts[0].u, c: 0 });
    }
    if (pts[pts.length - 1].x < 1) {
        pts.push({ x: 1, u: pts[pts.length - 1].u, c: 0 });
    }
    // Groups sharing an x: at 0 only the last point counts, at 1 only the first, inside the first and the last.
    var grouped = [];
    i = 0;
    while (i < pts.length) {
        var j = i;
        while (j + 1 < pts.length && pts[j + 1].x === pts[i].x) {
            j++;
        }
        if (pts[i].x === 0) {
            grouped.push(pt(0, pts[j].u, pts[j].c));
        } else if (pts[i].x === 1) {
            grouped.push(pt(1, pts[i].u, 0));
        } else {
            grouped.push(pt(pts[i].x, pts[i].u, pts[i].c));
            if (j > i) {
                grouped.push(pt(pts[j].x, pts[j].u, pts[j].c));
            }
        }
        i = j + 1;
    }
    // Drop a point equal to the one before it, and the middle of three on one level.
    var out = [grouped[0]];
    for (i = 1; i < grouped.length; i++) {
        var q = grouped[i];
        var prev = out[out.length - 1];
        var last = i === grouped.length - 1;
        if (!last && near(q.x, prev.x) && near(q.u, prev.u)) {
            prev.c = q.c;
            continue;
        }
        if (!last && near(q.u, prev.u) && near(grouped[i + 1].u, q.u)) {
            continue;
        }
        out.push(q);
    }
    while (out.length > MAX_PTS) {
        out.splice(out.length - 2, 1);
    }
    out[out.length - 1].c = 0;
    return out;
}

// Set the envelope to level u on [a, b), keeping the rest (the draw tool).
function setRange(pts, a, b, u) {
    a = clamp(a, 0, 1);
    b = clamp(b, 0, 1);
    if (b <= a) {
        return pts;
    }
    var left = evalLeft(pts, a);
    var right = evalEnv(pts, b);
    var rightCurve = pts[segmentAt(pts, b)].c;
    var out = [];
    var i;
    for (i = 0; i < pts.length; i++) {
        if (pts[i].x < a) {
            out.push(pt(pts[i].x, pts[i].u, pts[i].c));
        }
    }
    if (a > 0) {
        out.push(pt(a, left, 0));
    }
    out.push(pt(a, u, 0));
    out.push(pt(b, u, 0));
    if (b < 1) {
        out.push(pt(b, right, rightCurve));
    }
    for (i = 0; i < pts.length; i++) {
        if (pts[i].x > b) {
            out.push(pt(pts[i].x, pts[i].u, pts[i].c));
        }
    }
    return cleanEnv(out);
}

// The curve that puts the segment's midpoint at level m (dragging a curve handle).
function curveForMid(u0, u1, m) {
    var d = u1 - u0;
    if (Math.abs(d) < 1e-6) {
        return 0;
    }
    var r = clamp((m - u0) / d, 0.001, 0.999);
    var e;
    if (r <= 0.5) {
        e = Math.log(r) / Math.log(0.5);
        return clamp((e - 1) / 3, 0, 1);
    }
    e = Math.log(1 - r) / Math.log(0.5);
    return -clamp((e - 1) / 3, 0, 1);
}

// An envelope from equal cells, one level per cell.
function fromCells(levels) {
    var n = levels.length;
    var pts = [pt(0, levels[0], 0)];
    for (var i = 1; i < n; i++) {
        if (levels[i] !== levels[i - 1]) {
            pts.push(pt(i / n, levels[i - 1], 0));
            pts.push(pt(i / n, levels[i], 0));
        }
    }
    pts.push(pt(1, levels[n - 1], 0));
    return cleanEnv(pts);
}

function cells(n, fn) {
    var out = [];
    for (var i = 0; i < n; i++) {
        out.push(fn(i));
    }
    return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Presets. Time: u = how far behind "now" the audio plays, in patterns. A 45-degree fall is a stop, steeper plays
// backwards, a staircase repeats. Volume: u = 1 - gain.

var TIME_PRESETS = [
    { name: "Normal", make: function () { return [pt(0, 0), pt(1, 0)]; } },
    { name: "Half Time", make: function () { return [pt(0, 0), pt(1, 0.5)]; } },
    { name: "Half Time 1/2", make: function () { return [pt(0, 0), pt(0.5, 0.25), pt(0.5, 0), pt(1, 0.25)]; } },
    { name: "Tape Stop", make: function () { return [pt(0, 0), pt(0.5, 0, 1 / 3), pt(1, 0.25)]; } },
    { name: "Tape Stop Bar", make: function () { return [pt(0, 0, 1 / 3), pt(1, 0.5)]; } },
    { name: "Tape Start", make: function () { return [pt(0, 0, -1 / 3), pt(1, 0.5)]; } },
    { name: "Reverse 1/2", make: function () { return [pt(0, 0), pt(0.5, 0), pt(1, 1)]; } },
    {
        name: "Reverse Beats",
        make: function () {
            return [pt(0, 0), pt(0.25, 0.5), pt(0.25, 0), pt(0.5, 0.5), pt(0.5, 0), pt(0.75, 0.5), pt(0.75, 0),
                pt(1, 0.5)];
        }
    },
    {
        name: "1/16 Roll",
        make: function () {
            return fromCells(cells(16, function (i) { return i <= 8 ? 0 : (i - 8) / 16; }));
        }
    },
    {
        name: "Stutter 1/4",
        make: function () {
            return fromCells(cells(16, function (i) { return i <= 12 ? 0 : (i - 12) / 16; }));
        }
    },
    {
        name: "Roll Build",
        make: function () {
            return fromCells(cells(32, function (i) {
                if (i < 16) {
                    return 0;
                }
                if (i < 24) {
                    return Math.floor((i - 16) / 2) / 16;
                }
                return 0.25 + (i - 24) / 32;
            }));
        }
    },
    {
        name: "Scratch",
        make: function () {
            var p = [];
            for (var b = 0; b < 4; b++) {
                p.push(pt(b / 4, 0, -1 / 3));
                p.push(pt(b / 4 + 0.125, 0.2, 1 / 3));
            }
            p.push(pt(1, 0));
            return p;
        }
    },
    { name: "Slow > Fast", make: function () { return [pt(0, 0), pt(0.5, 0.25), pt(1, 0)]; } },
    { name: "Delay 1/8", make: function () { return [pt(0, 0.125), pt(1, 0.125)]; } }
];

var TRANCE = [1, 0, 1, 1, 0, 1, 1, 0, 1, 0, 1, 1, 0, 1, 1, 1];

var VOL_PRESETS = [
    { name: "Full", make: function () { return [pt(0, 0), pt(1, 0)]; } },
    { name: "Gate 1/8", make: function () { return fromCells(cells(16, function (i) { return i % 2; })); } },
    { name: "Gate 1/16", make: function () { return fromCells(cells(32, function (i) { return i % 2; })); } },
    { name: "Trance Gate", make: function () { return fromCells(cells(16, function (i) { return 1 - TRANCE[i]; })); } },
    {
        name: "Pump 1/4",
        make: function () {
            var p = [];
            for (var b = 0; b < 4; b++) {
                p.push(pt(b / 4, 0, 0));
                p.push(pt(b / 4, 0.9, -1 / 3));
                p.push(pt(b / 4 + 0.2, 0, 0));
            }
            p.push(pt(1, 0));
            return cleanEnv(p);
        }
    },
    { name: "Fade In", make: function () { return [pt(0, 1), pt(1, 0)]; } },
    { name: "Fade Out", make: function () { return [pt(0, 0), pt(1, 1)]; } },
    { name: "Off-Beat", make: function () { return fromCells(cells(8, function (i) { return i % 2 === 0 ? 1 : 0; })); } },
    {
        name: "Chop Last Beat",
        make: function () {
            return fromCells(cells(32, function (i) { return i < 24 ? 0 : i % 2; }));
        }
    },
    { name: "Swell 1/2", make: function () { return [pt(0, 1, 1 / 3), pt(0.5, 0), pt(0.5, 1, 1 / 3), pt(1, 0)]; } }
];

var PRESETS = [TIME_PRESETS, VOL_PRESETS];

// Pattern slots 1-8 start as useful pairs (time preset, volume preset).
var DEFAULT_SLOTS = [
    ["Normal", "Full"],
    ["Half Time", "Full"],
    ["1/16 Roll", "Full"],
    ["Tape Stop", "Full"],
    ["Reverse 1/2", "Full"],
    ["Normal", "Gate 1/16"],
    ["Stutter 1/4", "Trance Gate"],
    ["Scratch", "Pump 1/4"]
];

function presetIndex(env, name) {
    var list = PRESETS[env];
    for (var i = 0; i < list.length; i++) {
        if (list[i].name === name) {
            return i;
        }
    }
    return -1;
}

function makePreset(env, i) {
    return cleanEnv(PRESETS[env][i].make());
}

// ---------------------------------------------------------------------------------------------------------------
// State

function defaultState() {
    var st = { pats: [], preset: [], edit: 0, tool: 0, grid: 3, swing: 0 };
    for (var p = 0; p < NUM_PAT; p++) {
        var ti = presetIndex(0, DEFAULT_SLOTS[p][0]);
        var vi = presetIndex(1, DEFAULT_SLOTS[p][1]);
        st.pats.push([makePreset(0, ti), makePreset(1, vi)]);
        st.preset.push([ti, vi]);
    }
    return st;
}

var S = defaultState();
var view = { pattern: 0, active: 0, phase: -1, patlen: 2, signum: 4, sigden: 4, overlay: false, hover: "" };
var drag = null;
var undoStack = [];
var redoStack = [];
var localClip = null;

var envBuf = null;
var peakBuf = null;
var chanBase = 1;

// Blob: [magic, version, edit, tool, grid, swing, then per pattern and envelope: preset, n, n x (x, u, c)].
function serialize() {
    var out = [BLOB_MAGIC, BLOB_VERSION, S.edit, S.tool, S.grid, S.swing];
    for (var p = 0; p < NUM_PAT; p++) {
        for (var e = 0; e < 2; e++) {
            var pts = S.pats[p][e];
            out.push(S.preset[p][e], pts.length);
            for (var i = 0; i < pts.length; i++) {
                out.push(pts[i].x, pts[i].u, pts[i].c);
            }
        }
    }
    return out;
}

// Returns the state, or null when the blob is not one of ours (then the caller keeps what it has).
function deserialize(a) {
    if (!a || a.length < 6 || a[0] !== BLOB_MAGIC || a[1] !== BLOB_VERSION) {
        return null;
    }
    var st = {
        pats: [],
        preset: [],
        edit: clamp(Math.floor(a[2]), 0, 1),
        tool: clamp(Math.floor(a[3]), 0, 1),
        grid: clamp(Math.floor(a[4]), 0, GRIDS.length - 1),
        swing: clamp(Number(a[5]) || 0, 0, 75)
    };
    var k = 6;
    for (var p = 0; p < NUM_PAT; p++) {
        var pair = [];
        var pre = [];
        for (var e = 0; e < 2; e++) {
            if (k + 2 > a.length) {
                return null;
            }
            var preset = Math.floor(a[k]);
            var n = Math.floor(a[k + 1]);
            k += 2;
            if (n < 2 || n > MAX_PTS || k + 3 * n > a.length) {
                return null;
            }
            var pts = [];
            for (var i = 0; i < n; i++) {
                pts.push(pt(Number(a[k]), Number(a[k + 1]), Number(a[k + 2])));
                k += 3;
            }
            pair.push(cleanEnv(pts));
            pre.push(preset >= -1 && preset < PRESETS[e].length ? preset : -1);
        }
        st.pats.push(pair);
        st.preset.push(pre);
    }
    return st;
}

// ---------------------------------------------------------------------------------------------------------------
// Buffers: the envelopes go out to gen~, the waveform peaks come back from it.

function bufPoke(buf, frame, values) {
    try {
        buf.poke(chanBase, frame, values);
    } catch (err) {
        for (var i = 0; i < values.length; i++) {
            buf.poke(chanBase, frame + i, values[i]);
        }
    }
}

function firstValue(v) {
    return (v && typeof v === "object") ? v[0] : v;
}

// Max's JS Buffer numbers channels from 1; try that, and fall back to 0 if a test write does not read back.
function detectChannelBase(buf) {
    var probe = 4757.25;
    var bases = [1, 0];
    for (var i = 0; i < bases.length; i++) {
        try {
            buf.poke(bases[i], 0, probe);
            if (firstValue(buf.peek(bases[i], 0, 1)) === probe) {
                return bases[i];
            }
        } catch (err) {
            // try the next numbering
        }
    }
    return 1;
}

function writeEnv(p, e) {
    if (!envBuf) {
        return;
    }
    var pts = S.pats[p][e];
    var base = (p * 2 + e) * BLOCK;
    var flat = [];
    for (var i = 0; i < pts.length; i++) {
        flat.push(pts[i].x, pts[i].u, pts[i].c);
    }
    bufPoke(envBuf, base + 1, flat);
    bufPoke(envBuf, base, [pts.length]);
}

function writeAll() {
    for (var p = 0; p < NUM_PAT; p++) {
        writeEnv(p, 0);
        writeEnv(p, 1);
    }
}

function envbuf(name) {
    envBuf = new Buffer(String(name));
    try {
        if (envBuf.framecount() < ENV_FRAMES) {
            envBuf.send("sizeinsamps", ENV_FRAMES);
        }
    } catch (err) {
        post("gross warp: could not size " + name + "\n");
    }
    chanBase = detectChannelBase(envBuf);
    writeAll();
}

function peakbuf(name) {
    peakBuf = new Buffer(String(name));
}

function readPeaks() {
    if (!peakBuf) {
        return null;
    }
    try {
        var v = peakBuf.peek(chanBase, 0, 2 * PEAK_BINS);
        return (v && v.length === 2 * PEAK_BINS) ? v : null;
    } catch (err) {
        return null;
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Messages from the patcher

function pattern(i) {
    view.pattern = clamp(Math.floor(i), 0, NUM_PAT - 1);
    view.overlay = false;
    redraw();
}

function active(i) {
    view.active = clamp(Math.floor(i), 0, NUM_PAT - 1);
    redraw();
}

function phase(v) {
    view.phase = v;
    redraw();
}

function patlen(i) {
    view.patlen = clamp(Math.floor(i), 0, LENGTH_NAMES.length - 1);
    redraw();
}

// Live's time signature (from the Live API); beats are quarter notes, as in Live's song time.
function signum(n) {
    view.signum = clamp(Math.floor(n), 1, 32);
    redraw();
}

function sigden(d) {
    view.sigden = clamp(Math.floor(d), 1, 32);
    redraw();
}

function bang() {
    redraw();
}

function loadbang() {
    redraw();
}

// Live stores the blob with the set; Max asks for it with getvalueof and restores it with setvalueof.
function getvalueof() {
    return serialize();
}

function setvalueof() {
    var st = deserialize(arrayfromargs(arguments));
    if (st) {
        S = st;
        undoStack = [];
        redoStack = [];
        writeAll();
        redraw();
    }
}

// Tell Live the blob changed: only for envelope edits that are finished (mouse-up, a preset, paste...), never during
// a drag or for view settings, so Live's undo history gets one step per edit.
function changed() {
    try {
        notifyclients();
    } catch (err) {
        // not under pattr/Live (tests)
    }
}

function redraw() {
    if (typeof mgraphics !== "undefined") {
        mgraphics.redraw();
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Editing commands (also reachable as messages)

function curPts() {
    return S.pats[view.pattern][S.edit];
}

function snapshot(p, e) {
    return { p: p, e: e, pts: copyPts(S.pats[p][e]), preset: S.preset[p][e] };
}

function pushUndo(p, e) {
    undoStack.push(snapshot(p, e));
    if (undoStack.length > UNDO_DEPTH) {
        undoStack.shift();
    }
    redoStack = [];
}

function restore(s) {
    S.pats[s.p][s.e] = copyPts(s.pts);
    S.preset[s.p][s.e] = s.preset;
    S.edit = s.e;
    writeEnv(s.p, s.e);
    changed();
    redraw();
}

function doundo() {
    if (undoStack.length === 0) {
        return;
    }
    var s = undoStack.pop();
    redoStack.push(snapshot(s.p, s.e));
    restore(s);
}

function doredo() {
    if (redoStack.length === 0) {
        return;
    }
    var s = redoStack.pop();
    undoStack.push(snapshot(s.p, s.e));
    restore(s);
}

// Replace the edited envelope as one undoable step.
function replaceCur(pts, preset) {
    var p = view.pattern;
    var e = S.edit;
    pushUndo(p, e);
    S.pats[p][e] = cleanEnv(pts);
    S.preset[p][e] = preset;
    writeEnv(p, e);
    changed();
    redraw();
}

function dopreset(i) {
    var e = S.edit;
    i = Math.floor(i);
    if (i < 0 || i >= PRESETS[e].length) {
        return;
    }
    replaceCur(makePreset(e, i), i);
}

function stepPreset(dir) {
    var n = PRESETS[S.edit].length;
    var cur = S.preset[view.pattern][S.edit];
    var next = cur < 0 ? (dir > 0 ? 0 : n - 1) : (cur + dir + n) % n;
    dopreset(next);
}

function nextpreset() {
    stepPreset(1);
}

function prevpreset() {
    stepPreset(-1);
}

function doclear() {
    replaceCur([pt(0, 0), pt(1, 0)], 0);
}

function clipboard() {
    try {
        return new Global("grosswarp_clipboard");
    } catch (err) {
        if (!localClip) {
            localClip = {};
        }
        return localClip;
    }
}

// The clipboard is shared by every Gross Warp in Live (a Max Global), as plain numbers.
function docopy() {
    var pts = curPts();
    var flat = [];
    for (var i = 0; i < pts.length; i++) {
        flat.push(pts[i].x, pts[i].u, pts[i].c);
    }
    clipboard().points = flat;
    redraw();
}

function hasClip() {
    var c = clipboard().points;
    return !!(c && c.length >= 6);
}

function dopaste() {
    if (!hasClip()) {
        return;
    }
    var flat = clipboard().points;
    var pts = [];
    for (var i = 0; i + 2 < flat.length; i += 3) {
        pts.push(pt(flat[i], flat[i + 1], flat[i + 2]));
    }
    replaceCur(pts, -1);
}

function setedit(e) {
    S.edit = clamp(Math.floor(e), 0, 1);
    view.overlay = false;
    redraw();
}

function settool(t) {
    S.tool = clamp(Math.floor(t), 0, 1);
    redraw();
}

function setgrid(g) {
    S.grid = clamp(Math.floor(g), 0, GRIDS.length - 1);
    redraw();
}

function setswing(v) {
    S.swing = clamp(Math.round(v), 0, 75);
    redraw();
}

// ---------------------------------------------------------------------------------------------------------------
// Grid and snapping

// Quarter notes per bar and per pattern (the same arithmetic as gen~).
function quartersPerBar() {
    return (view.signum * 4) / view.sigden;
}

function patQuarters() {
    var qpb = quartersPerBar();
    return [1, 2, qpb, 2 * qpb, 4 * qpb][view.patlen];
}

// Grid cells across the pattern; may be fractional (quarter notes in 7/8: the last cell is shorter).
function gridCells() {
    var g = GRIDS[S.grid];
    return g.div > 0 ? (patQuarters() * g.div) / 4 : 0;
}

// Position of grid line k (0..ceil(n)): odd lines are pushed later by the swing; never past the end.
function gridLine(k, n) {
    var step = 1 / n;
    var x = k * step;
    if (k % 2 === 1 && x < 1) {
        x += (S.swing / 100) * step * 0.5;
    }
    return Math.min(x, 1);
}

function lineCount(n) {
    return Math.ceil(n - 1e-9);
}

function snapX(x, free) {
    var n = gridCells();
    if (free || n <= 0) {
        return clamp(x, 0, 1);
    }
    var best = 0;
    var bestD = 2;
    var k0 = Math.max(0, Math.floor(x * n) - 1);
    var k1 = Math.min(lineCount(n), Math.floor(x * n) + 2);
    for (var k = k0; k <= k1; k++) {
        var gx = k >= lineCount(n) ? 1 : gridLine(k, n);
        if (Math.abs(gx - x) < bestD) {
            bestD = Math.abs(gx - x);
            best = gx;
        }
    }
    return best;
}

// Time offsets snap to the grid too (whole-step offsets repeat in time); every level sticks to the top and bottom.
function snapU(u, free) {
    u = clamp(u, 0, 1);
    if (!free && S.edit === 0) {
        var n = gridCells();
        if (n > 0) {
            return clamp(Math.round(u * n) / n, 0, 1);
        }
    }
    if (u < 0.02) {
        return 0;
    }
    if (u > 0.98) {
        return 1;
    }
    return u;
}

// The draw tool's cell under x: [start, end) between swung grid lines (1/32 notes when the grid is off).
function cellAt(x) {
    var n = gridCells();
    if (n <= 0) {
        n = patQuarters() * 8;
    }
    var cells = lineCount(n);
    for (var k = 0; k < cells; k++) {
        var b = k + 1 >= cells ? 1 : gridLine(k + 1, n);
        if (x < b || k === cells - 1) {
            return { k: k, a: gridLine(k, n), b: b };
        }
    }
    return { k: 0, a: 0, b: 1 };
}

// ---------------------------------------------------------------------------------------------------------------
// Layout

var TB_H = 17;
var TB_GAP = 3;
var PAD_TOP = 14; // a strip for the labels above the top line
var PAD_BOTTOM = 5;
var PAD_X = 5; // keeps the end points whole and grabbable

function viewSize() {
    if (typeof mgraphics !== "undefined" && mgraphics.size) {
        return [mgraphics.size[0], mgraphics.size[1]];
    }
    var r = box.rect;
    return [r[2] - r[0], r[3] - r[1]];
}

function presetLabel() {
    var i = S.preset[view.pattern][S.edit];
    return i >= 0 ? PRESETS[S.edit][i].name : "Custom";
}

function layout(w, h) {
    var dispH = h - 2 * (TB_H + TB_GAP);
    var r1 = dispH + TB_GAP;
    var r2 = r1 + TB_H + TB_GAP;
    var L = { w: w, h: h, disp: { x: 0, y: 0, w: w, h: dispH }, buttons: [] };
    var x = 0;
    function add(id, label, bw, row, kind, on, enabled) {
        L.buttons.push({ id: id, label: label, x: x, y: row, w: bw, h: TB_H, kind: kind || "button", on: !!on,
            enabled: enabled !== false });
        x += bw + 2;
    }
    add("time", "Time", 34, r1, "button", S.edit === 0);
    add("vol", "Volume", 46, r1, "button", S.edit === 1);
    x += 6;
    add("pprev", "<", 14, r1);
    add("pname", presetLabel(), 108, r1, "field", view.overlay);
    add("pnext", ">", 14, r1);
    x += 6;
    add("point", "Point", 36, r1, "button", S.tool === 0);
    add("draw", "Draw", 34, r1, "button", S.tool === 1);
    x = 0;
    add("gprev", "<", 14, r2);
    add("grid", "Grid " + GRIDS[S.grid].name, 58, r2, "field");
    add("gnext", ">", 14, r2);
    x += 4;
    add("sprev", "<", 14, r2);
    add("swing", "Swing " + S.swing + "%", 62, r2, "field");
    add("snext", ">", 14, r2);
    x += 6;
    add("copy", "Copy", 34, r2);
    add("paste", "Paste", 36, r2, "button", false, hasClip());
    add("clear", "Clear", 34, r2);
    add("undo", "Undo", 34, r2, "button", false, undoStack.length > 0);
    add("redo", "Redo", 34, r2, "button", false, redoStack.length > 0);
    return L;
}

function curLayout() {
    var s = viewSize();
    return layout(s[0], s[1]);
}

function toX(L, x) {
    return L.disp.x + PAD_X + x * (L.disp.w - 2 * PAD_X);
}

function toY(L, u) {
    return L.disp.y + PAD_TOP + u * (L.disp.h - PAD_TOP - PAD_BOTTOM);
}

function fromX(L, px) {
    return clamp((px - L.disp.x - PAD_X) / (L.disp.w - 2 * PAD_X), 0, 1);
}

function fromY(L, py) {
    return clamp((py - L.disp.y - PAD_TOP) / (L.disp.h - PAD_TOP - PAD_BOTTOM), 0, 1);
}

function inRect(r, x, y) {
    return x >= r.x && x < r.x + r.w && y >= r.y && y < r.y + r.h;
}

function hitButton(L, x, y) {
    for (var i = 0; i < L.buttons.length; i++) {
        if (inRect(L.buttons[i], x, y)) {
            return L.buttons[i];
        }
    }
    return null;
}

function hitPoint(L, pts, x, y) {
    var best = -1;
    var bestD = 36;
    for (var i = 0; i < pts.length; i++) {
        var dx = toX(L, pts[i].x) - x;
        var dy = toY(L, pts[i].u) - y;
        var d = dx * dx + dy * dy;
        if (d <= bestD) {
            bestD = d;
            best = i;
        }
    }
    return best;
}

// Curve handles sit at the middle of segments wide enough to grab and not flat.
function handlePos(L, pts, s) {
    var a = pts[s];
    var b = pts[s + 1];
    if (toX(L, b.x) - toX(L, a.x) < 14 || Math.abs(b.u - a.u) < 0.01) {
        return null;
    }
    var xm = (a.x + b.x) / 2;
    return { x: toX(L, xm), y: toY(L, evalSeg(pts, s, xm)) };
}

function hitHandle(L, pts, x, y) {
    for (var s = 0; s < pts.length - 1; s++) {
        var h = handlePos(L, pts, s);
        if (h && (h.x - x) * (h.x - x) + (h.y - y) * (h.y - y) <= 25) {
            return s;
        }
    }
    return -1;
}

function overlayItems(L) {
    var list = PRESETS[S.edit];
    var cols = 3;
    var iw = Math.floor((L.disp.w - 12) / cols);
    var ih = 13;
    var items = [];
    for (var i = 0; i < list.length; i++) {
        items.push({ i: i, label: list[i].name, x: 6 + (i % cols) * iw, y: 6 + Math.floor(i / cols) * ih, w: iw - 2,
            h: ih - 1 });
    }
    return items;
}

function hitOverlay(L, x, y) {
    var items = overlayItems(L);
    for (var i = 0; i < items.length; i++) {
        if (inRect(items[i], x, y)) {
            return items[i].i;
        }
    }
    return -1;
}

// ---------------------------------------------------------------------------------------------------------------
// Mouse

function pressButton(id) {
    if (id === "time") {
        setedit(0);
    } else if (id === "vol") {
        setedit(1);
    } else if (id === "pprev") {
        prevpreset();
    } else if (id === "pnext") {
        nextpreset();
    } else if (id === "pname") {
        view.overlay = !view.overlay;
        redraw();
    } else if (id === "point") {
        settool(0);
    } else if (id === "draw") {
        settool(1);
    } else if (id === "gprev") {
        setgrid(S.grid - 1);
    } else if (id === "gnext" || id === "grid") {
        setgrid(S.grid >= GRIDS.length - 1 && id === "grid" ? 0 : S.grid + 1);
    } else if (id === "sprev") {
        setswing(S.swing - 5);
    } else if (id === "snext") {
        setswing(S.swing + 5);
    } else if (id === "swing") {
        setswing(0);
    } else if (id === "copy") {
        docopy();
    } else if (id === "paste") {
        dopaste();
    } else if (id === "clear") {
        doclear();
    } else if (id === "undo") {
        doundo();
    } else if (id === "redo") {
        doredo();
    }
}

function paintAt(L, px, py, free, erase) {
    var c = cellAt(fromX(L, px));
    var u = erase ? 0 : snapU(fromY(L, py), free);
    if (drag.lastCell === c.k && drag.lastU === u) {
        return;
    }
    drag.lastCell = c.k;
    drag.lastU = u;
    S.pats[drag.p][drag.e] = setRange(S.pats[drag.p][drag.e], c.a, c.b, u);
    S.preset[drag.p][drag.e] = -1;
    drag.moved = true;
    writeEnv(drag.p, drag.e);
}

// Shift: no snapping. Alt: delete a point, reset a curve, or erase with the draw tool. Double-click a point: delete.
function onclick(x, y, but, cmd, shift, capslock, option, ctrl) {
    var L = curLayout();
    if (view.overlay) {
        var item = hitOverlay(L, x, y);
        view.overlay = false;
        if (item >= 0) {
            dopreset(item);
        }
        redraw();
        return;
    }
    var b = hitButton(L, x, y);
    if (b) {
        if (b.enabled) {
            pressButton(b.id);
        }
        return;
    }
    if (!inRect(L.disp, x, y)) {
        return;
    }
    var p = view.pattern;
    var e = S.edit;
    var pts = S.pats[p][e];
    drag = null;
    if (S.tool === 1) {
        pushUndo(p, e);
        drag = { kind: "draw", p: p, e: e, moved: false, lastCell: -1, lastU: -1, erase: !!option };
        paintAt(L, x, y, shift, option);
        redraw();
        return;
    }
    var hi = hitPoint(L, pts, x, y);
    if (hi >= 0) {
        if (option) {
            deletePoint(hi);
            return;
        }
        pushUndo(p, e);
        drag = { kind: "point", idx: hi, p: p, e: e, moved: false };
        redraw();
        return;
    }
    var hs = hitHandle(L, pts, x, y);
    if (hs >= 0) {
        pushUndo(p, e);
        if (option) {
            pts[hs].c = 0;
            S.preset[p][e] = -1;
            writeEnv(p, e);
            changed();
            redraw();
            return;
        }
        drag = { kind: "curve", idx: hs, p: p, e: e, moved: false };
        return;
    }
    var nx = snapX(fromX(L, x), shift);
    var nu = snapU(fromY(L, y), shift);
    pushUndo(p, e);
    var idx;
    if (nx <= 0) {
        idx = 0;
    } else if (nx >= 1) {
        idx = pts.length - 1;
    } else {
        if (pts.length >= MAX_PTS) {
            undoStack.pop();
            return;
        }
        idx = 0;
        while (idx < pts.length && pts[idx].x <= nx) {
            idx++;
        }
        pts.splice(idx, 0, pt(nx, nu, 0));
    }
    pts[idx].u = nu;
    S.preset[p][e] = -1;
    drag = { kind: "point", idx: idx, p: p, e: e, moved: true };
    writeEnv(p, e);
    redraw();
}

function ondrag(x, y, but, cmd, shift, capslock, option, ctrl) {
    if (!drag) {
        return;
    }
    if (!but) {
        finishDrag();
        return;
    }
    var L = curLayout();
    var pts = S.pats[drag.p][drag.e];
    var was = drag.moved;
    drag.moved = false;
    if (drag.kind === "point") {
        var i = drag.idx;
        var n = pts.length;
        var nx = snapX(fromX(L, x), shift);
        var nu = snapU(fromY(L, y), shift);
        if (i === 0) {
            nx = 0;
        } else if (i === n - 1) {
            nx = 1;
        } else {
            nx = clamp(nx, pts[i - 1].x, pts[i + 1].x);
        }
        if (nx !== pts[i].x || nu !== pts[i].u) {
            pts[i].x = nx;
            pts[i].u = nu;
            drag.moved = true;
        }
    } else if (drag.kind === "curve") {
        var a = pts[drag.idx];
        var c = curveForMid(a.u, pts[drag.idx + 1].u, fromY(L, y));
        if (c !== a.c) {
            a.c = c;
            drag.moved = true;
        }
    } else {
        paintAt(L, x, y, shift, drag.erase);
    }
    if (drag.moved) {
        S.preset[drag.p][drag.e] = -1;
        writeEnv(drag.p, drag.e);
        redraw();
    }
    drag.moved = drag.moved || was;
}

function finishDrag() {
    var d = drag;
    drag = null;
    if (d.moved) {
        S.pats[d.p][d.e] = cleanEnv(S.pats[d.p][d.e]);
        writeEnv(d.p, d.e);
        changed();
    } else {
        undoStack.pop();
    }
    redraw();
}

function deletePoint(i) {
    var p = view.pattern;
    var e = S.edit;
    var pts = S.pats[p][e];
    if (i <= 0 || i >= pts.length - 1) {
        return;
    }
    pushUndo(p, e);
    pts.splice(i, 1);
    S.pats[p][e] = cleanEnv(pts);
    S.preset[p][e] = -1;
    writeEnv(p, e);
    changed();
    redraw();
}

function ondblclick(x, y, but, cmd, shift, capslock, option, ctrl) {
    var L = curLayout();
    if (view.overlay || !inRect(L.disp, x, y) || S.tool !== 0) {
        return;
    }
    var hi = hitPoint(L, curPts(), x, y);
    if (hi >= 0) {
        deletePoint(hi);
    }
}

function hoverTarget(x, y) {
    var L = curLayout();
    if (view.overlay) {
        var item = hitOverlay(L, x, y);
        return item >= 0 ? "item:" + item : "";
    }
    var b = hitButton(L, x, y);
    if (b) {
        return "btn:" + b.id;
    }
    if (!inRect(L.disp, x, y) || S.tool !== 0) {
        return "";
    }
    var hi = hitPoint(L, curPts(), x, y);
    if (hi >= 0) {
        return "pt:" + hi;
    }
    var hs = hitHandle(L, curPts(), x, y);
    return hs >= 0 ? "hd:" + hs : "";
}

function onidle(x, y, but, cmd, shift, capslock, option, ctrl) {
    var h = hoverTarget(x, y);
    if (h !== view.hover) {
        view.hover = h;
        redraw();
    }
}

function onidleout() {
    if (view.hover !== "") {
        view.hover = "";
        redraw();
    }
}

function onresize(w, h) {
    redraw();
}

// ---------------------------------------------------------------------------------------------------------------
// Drawing

var COL = {
    bg: [0.11, 0.11, 0.11, 1],
    disp: [0.15, 0.15, 0.15, 1],
    frame: [0.32, 0.32, 0.32, 1],
    gridMinor: [1, 1, 1, 0.05],
    gridBeat: [1, 1, 1, 0.13],
    gridBar: [1, 1, 1, 0.3],
    waveIn: [0.45, 0.45, 0.45, 0.5],
    waveOut: [0.55, 0.75, 0.9, 0.35],
    env: [[0.45, 0.78, 1.0], [1.0, 0.68, 0.32]],
    point: [0.96, 0.96, 0.96, 1],
    playhead: [1, 1, 1, 0.85],
    text: [0.86, 0.86, 0.86, 1],
    textDim: [0.55, 0.55, 0.55, 1],
    btn: [0.23, 0.23, 0.23, 1],
    btnHover: [0.3, 0.3, 0.3, 1],
    accent: [0.45, 0.78, 1.0, 1],
    accentText: [0.07, 0.07, 0.07, 1],
    overlay: [0.09, 0.09, 0.09, 0.96]
};

function rgba(g, c, alpha) {
    g.set_source_rgba(c[0], c[1], c[2], alpha === undefined ? (c.length > 3 ? c[3] : 1) : alpha);
}

function textAt(g, s, x, y, c) {
    rgba(g, c);
    g.move_to(x, y);
    g.show_text(s);
}

function textWidth(g, s) {
    var m = g.text_measure(s);
    return m[0];
}

function vline(g, L, x) {
    var gx = Math.round(toX(L, x)) + 0.5;
    g.move_to(gx, L.disp.y);
    g.line_to(gx, L.disp.y + L.disp.h);
}

function drawGrid(g, L) {
    var d = L.disp;
    var n = gridCells();
    var patQ = patQuarters();
    var qpb = quartersPerBar();
    var k;
    g.set_line_width(1);
    if (n > 0 && n <= 96) {
        rgba(g, COL.gridMinor);
        for (k = 1; k < lineCount(n); k++) {
            vline(g, L, gridLine(k, n));
        }
        g.stroke();
    }
    rgba(g, COL.gridBeat);
    for (k = 1; k < patQ; k++) {
        vline(g, L, k / patQ);
    }
    g.stroke();
    if (patQ > qpb) {
        rgba(g, COL.gridBar);
        for (k = 1; k * qpb < patQ; k++) {
            vline(g, L, (k * qpb) / patQ);
        }
        g.stroke();
    }
    // Level guides: the grid's offsets for the time envelope, quarters for volume.
    var step = (S.edit === 0 && n > 0 && n <= 32) ? 1 / n : 0.25;
    rgba(g, COL.gridMinor);
    for (k = 1; k * step < 1 - 1e-9; k++) {
        var gy = Math.round(toY(L, k * step)) + 0.5;
        g.move_to(d.x, gy);
        g.line_to(d.x + d.w, gy);
    }
    g.stroke();
}

function drawWave(g, L, peaks, offset, c) {
    var d = L.disp;
    var mid = d.y + d.h / 2;
    var half = d.h / 2 - 2;
    var i;
    rgba(g, c);
    g.move_to(toX(L, 0), mid);
    for (i = 0; i < PEAK_BINS; i++) {
        var a = Math.min(1, Math.abs(peaks[offset + i])) * half;
        g.line_to(toX(L, i / PEAK_BINS), mid - a);
        g.line_to(toX(L, (i + 1) / PEAK_BINS), mid - a);
    }
    for (i = PEAK_BINS - 1; i >= 0; i--) {
        var b = Math.min(1, Math.abs(peaks[offset + i])) * half;
        g.line_to(toX(L, (i + 1) / PEAK_BINS), mid + b);
        g.line_to(toX(L, i / PEAK_BINS), mid + b);
    }
    g.close_path();
    g.fill();
}

function tracePath(g, L, pts) {
    g.move_to(toX(L, pts[0].x), toY(L, pts[0].u));
    for (var i = 0; i < pts.length - 1; i++) {
        var a = pts[i];
        var b = pts[i + 1];
        if (a.c === 0 || near(a.u, b.u) || b.x - a.x <= 0) {
            g.line_to(toX(L, b.x), toY(L, b.u));
        } else {
            for (var s = 1; s <= 24; s++) {
                var t = s / 24;
                g.line_to(toX(L, a.x + (b.x - a.x) * t), toY(L, a.u + (b.u - a.u) * shape(t, a.c)));
            }
        }
    }
}

function drawEnvelope(g, L, e, editing) {
    var pts = S.pats[view.pattern][e];
    var c = COL.env[e];
    var d = L.disp;
    if (editing) {
        // A light fill on the side that is "not normal": below the line for time, above it for volume.
        tracePath(g, L, pts);
        var edgeY = e === 0 ? d.y + d.h : d.y;
        g.line_to(toX(L, 1), edgeY);
        g.line_to(toX(L, 0), edgeY);
        g.close_path();
        rgba(g, c, 0.07);
        g.fill();
    }
    tracePath(g, L, pts);
    rgba(g, c, editing ? 1 : 0.3);
    g.set_line_width(editing ? 1.5 : 1);
    g.stroke();
    if (!editing || S.tool !== 0) {
        return;
    }
    for (var s = 0; s < pts.length - 1; s++) {
        var h = handlePos(L, pts, s);
        if (h) {
            var hot = view.hover === "hd:" + s || (drag && drag.kind === "curve" && drag.idx === s);
            rgba(g, c, hot ? 1 : 0.45);
            g.set_line_width(1);
            g.ellipse(h.x - 3, h.y - 3, 6, 6);
            g.stroke();
        }
    }
    for (var i = 0; i < pts.length; i++) {
        var on = view.hover === "pt:" + i || (drag && drag.kind === "point" && drag.idx === i);
        var r = on ? 3.5 : 2.5;
        rgba(g, on ? COL.accent : COL.point);
        g.rectangle(toX(L, pts[i].x) - r, toY(L, pts[i].u) - r, 2 * r, 2 * r);
        g.fill();
    }
}

function drawDisplay(g, L) {
    var d = L.disp;
    rgba(g, COL.disp);
    g.rectangle(d.x, d.y, d.w, d.h);
    g.fill();
    drawGrid(g, L);
    var peaks = readPeaks();
    if (peaks) {
        drawWave(g, L, peaks, 0, COL.waveIn);
        drawWave(g, L, peaks, PEAK_BINS, COL.waveOut);
    }
    drawEnvelope(g, L, 1 - S.edit, false);
    drawEnvelope(g, L, S.edit, true);
    if (view.phase >= 0) {
        var px = Math.round(toX(L, view.phase)) + 0.5;
        rgba(g, COL.playhead, view.active === view.pattern ? 0.85 : 0.3);
        g.set_line_width(1);
        g.move_to(px, d.y);
        g.line_to(px, d.y + d.h);
        g.stroke();
    }
    g.select_font_face("Arial");
    g.set_font_size(9);
    var left = "P" + (view.pattern + 1) + "  " + ENV_NAMES[S.edit];
    if (view.phase >= 0 && view.active !== view.pattern) {
        left += "   (playing P" + (view.active + 1) + ")";
    }
    textAt(g, left, d.x + 4, d.y + 11, COL.textDim);
    var right = presetLabel();
    textAt(g, right, d.x + d.w - textWidth(g, right) - 4, d.y + 11, COL.text);
    rgba(g, COL.frame);
    g.set_line_width(1);
    g.rectangle(d.x + 0.5, d.y + 0.5, d.w - 1, d.h - 1);
    g.stroke();
}

function drawToolbar(g, L) {
    g.select_font_face("Arial");
    g.set_font_size(9);
    for (var i = 0; i < L.buttons.length; i++) {
        var b = L.buttons[i];
        var hot = view.hover === "btn:" + b.id;
        if (b.on) {
            rgba(g, COL.accent);
        } else {
            rgba(g, hot && b.enabled ? COL.btnHover : COL.btn);
        }
        g.rectangle_rounded(b.x, b.y, b.w, b.h, 4, 4);
        g.fill();
        var tw = textWidth(g, b.label);
        var tc = b.on ? COL.accentText : (b.enabled ? COL.text : COL.textDim);
        textAt(g, b.label, b.x + Math.max(3, (b.w - tw) / 2), b.y + b.h - 5, tc);
    }
}

function drawOverlay(g, L) {
    var items = overlayItems(L);
    var last = items[items.length - 1];
    rgba(g, COL.overlay);
    g.rectangle_rounded(2, 2, L.disp.w - 4, last.y + last.h + 6, 5, 5);
    g.fill();
    g.select_font_face("Arial");
    g.set_font_size(9);
    var cur = S.preset[view.pattern][S.edit];
    for (var i = 0; i < items.length; i++) {
        var it = items[i];
        var hot = view.hover === "item:" + it.i;
        if (it.i === cur || hot) {
            rgba(g, it.i === cur ? COL.accent : COL.btnHover);
            g.rectangle_rounded(it.x, it.y, it.w, it.h, 3, 3);
            g.fill();
        }
        textAt(g, it.label, it.x + 4, it.y + it.h - 3, it.i === cur ? COL.accentText : COL.text);
    }
}

function paint() {
    var g = mgraphics;
    var L = curLayout();
    rgba(g, COL.bg);
    g.rectangle(0, 0, L.w, L.h);
    g.fill();
    drawDisplay(g, L);
    drawToolbar(g, L);
    if (view.overlay) {
        drawOverlay(g, L);
    }
}

if (typeof mgraphics !== "undefined") {
    mgraphics.init();
    mgraphics.relative_coords = 0;
    mgraphics.autofill = 0;
}
