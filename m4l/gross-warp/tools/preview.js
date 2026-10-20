#!/usr/bin/env node
// Renders an approximate picture of the device as SVG, without Max: the real editor script paints through a recording
// mgraphics, the calls are turned into SVG, and Live's own controls (dials, tabs, menus) are mocked around it. The
// waveform comes from the real DSP (simulated gen~) playing a synthetic drum loop through pattern 3 (1/16 Roll).
//
//   node tools/preview.js [out.svg]      (default: docs/preview.svg)

"use strict";

const fs = require("fs");
const path = require("path");
const { makeRig } = require("../test/helpers");

const DEVICE_W = 540;
const DEVICE_H = 169;

function esc(s) {
    return String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

function color(c) {
    const ch = (v) => Math.round(Math.min(1, Math.max(0, v)) * 255);
    return { fill: `rgb(${ch(c[0])},${ch(c[1])},${ch(c[2])})`, opacity: c[3] === undefined ? 1 : c[3] };
}

// mgraphics calls -> SVG elements, offset by (ox, oy).
function callsToSvg(calls, ox, oy) {
    const out = [];
    let d = "";
    let src = color([0, 0, 0, 1]);
    let width = 1;
    let font = 9;
    let cur = [0, 0];
    const P = (x, y) => `${(x + ox).toFixed(2)} ${(y + oy).toFixed(2)}`;
    for (const [op, ...a] of calls) {
        switch (op) {
            case "set_source_rgba":
                src = color(a);
                break;
            case "set_line_width":
                width = a[0];
                break;
            case "set_font_size":
                font = a[0];
                break;
            case "move_to":
                d += `M${P(a[0], a[1])} `;
                cur = [a[0], a[1]];
                break;
            case "line_to":
                d += `L${P(a[0], a[1])} `;
                cur = [a[0], a[1]];
                break;
            case "close_path":
                d += "Z ";
                break;
            case "rectangle":
                d += `M${P(a[0], a[1])} h${a[2]} v${a[3]} h${-a[2]} Z `;
                break;
            case "rectangle_rounded": {
                const [x, y, w, h, rx] = a;
                const r = Math.min(rx / 2, w / 2, h / 2);
                d +=
                    `M${P(x + r, y)} h${w - 2 * r} a${r} ${r} 0 0 1 ${r} ${r} v${h - 2 * r} ` +
                    `a${r} ${r} 0 0 1 ${-r} ${r} h${-(w - 2 * r)} a${r} ${r} 0 0 1 ${-r} ${-r} v${-(h - 2 * r)} ` +
                    `a${r} ${r} 0 0 1 ${r} ${-r} Z `;
                break;
            }
            case "ellipse": {
                const [x, y, w, h] = a;
                d += `M${P(x, y + h / 2)} a${w / 2} ${h / 2} 0 1 0 ${w} 0 a${w / 2} ${h / 2} 0 1 0 ${-w} 0 Z `;
                break;
            }
            case "fill":
                out.push(`<path d="${d}" fill="${src.fill}" fill-opacity="${src.opacity}"/>`);
                d = "";
                break;
            case "stroke":
                out.push(
                    `<path d="${d}" fill="none" stroke="${src.fill}" stroke-opacity="${src.opacity}" ` +
                        `stroke-width="${width}"/>`,
                );
                d = "";
                break;
            case "show_text":
                out.push(
                    `<text x="${(cur[0] + ox).toFixed(1)}" y="${(cur[1] + oy).toFixed(1)}" font-size="${font + 1}" ` +
                        `fill="${src.fill}" fill-opacity="${src.opacity}">${esc(a[0])}</text>`,
                );
                break;
            default:
                break;
        }
    }
    return out.join("\n");
}

function dial(x, y, label, value, frac) {
    const cx = x + 27;
    const cy = y + 25;
    const r = 11;
    const a0 = Math.PI * 0.75;
    const a1 = a0 + Math.PI * 1.5 * frac;
    const pt = (ang) => `${(cx + r * Math.cos(ang)).toFixed(2)} ${(cy + r * Math.sin(ang)).toFixed(2)}`;
    const arc = (from, to, col, w) =>
        `<path d="M${pt(from)} A${r} ${r} 0 ${to - from > Math.PI ? 1 : 0} 1 ${pt(to)}" fill="none" stroke="${col}" ` +
        `stroke-width="${w}" stroke-linecap="round"/>`;
    return [
        `<text x="${cx}" y="${y + 9}" font-size="9.5" fill="#cfcfcf" text-anchor="middle">${esc(label)}</text>`,
        arc(a0, a0 + Math.PI * 1.5, "#555", 2.5),
        frac > 0 ? arc(a0, a1, "#73c7ff", 2.5) : "",
        `<line x1="${cx}" y1="${cy}" x2="${(cx + (r - 2) * Math.cos(a1)).toFixed(2)}" ` +
            `y2="${(cy + (r - 2) * Math.sin(a1)).toFixed(2)}" stroke="#ddd" stroke-width="1.5"/>`,
        `<text x="${cx}" y="${y + 46}" font-size="9.5" fill="#cfcfcf" text-anchor="middle">${esc(value)}</text>`,
    ].join("\n");
}

function liveControls(selected) {
    const parts = [];
    for (let i = 0; i < 8; i++) {
        const x = 4 + i * 22;
        const on = i === selected;
        parts.push(
            `<rect x="${x}" y="150" width="21" height="16" fill="${on ? "#73c7ff" : "#3b3b3b"}"/>`,
            `<text x="${x + 10.5}" y="161.5" font-size="9.5" text-anchor="middle" fill="${on ? "#111" : "#ccc"}">${i + 1}</text>`,
        );
    }
    const menu = (x, label, value) =>
        `<text x="${x - 40}" y="161.5" font-size="9.5" fill="#bbb">${label}</text>` +
        `<rect x="${x}" y="150" width="66" height="15" fill="#3b3b3b"/>` +
        `<text x="${x + 5}" y="161" font-size="9.5" fill="#ddd">${value}</text>` +
        `<path d="M${x + 57} 156 l3 4 l3 -4 Z" fill="#aaa"/>`;
    parts.push(menu(226, "Switch", "Pattern"), menu(340, "Length", "1 Bar"));
    parts.push(dial(414, 4, "Smooth", "4.00 ms", 0.08), dial(414, 58, "Depth", "100 %", 1));
    parts.push(dial(414, 112, "Dry/Wet", "100 %", 1));
    // the second column: Pitch Lock, Grain, MIDI link
    const label = (x, y, t) => `<text x="${x}" y="${y}" font-size="9.5" fill="#bbb">${t}</text>`;
    parts.push(label(476, 13, "Pitch"));
    ["Off", "Lock"].forEach((t, i) => {
        const x = 476 + i * 29;
        const on = i === 0;
        parts.push(
            `<rect x="${x}" y="18" width="28" height="15" fill="${on ? "#73c7ff" : "#3b3b3b"}"/>`,
            `<text x="${x + 14}" y="29" font-size="9" text-anchor="middle" fill="${on ? "#111" : "#ccc"}">${t}</text>`,
        );
    });
    parts.push(dial(478, 40, "Grain", "60.0 ms", 0.35));
    parts.push(label(476, 107, "MIDI"));
    parts.push(
        `<rect x="476" y="112" width="58" height="15" fill="#3b3b3b"/>` +
            `<text x="481" y="123" font-size="9.5" fill="#ddd">A</text>` +
            `<path d="M525 118 l3 4 l3 -4 Z" fill="#aaa"/>`,
    );
    return parts.join("\n");
}

// setup(ctx), if given, runs on the editor just before it paints (other states for a look).
function render(setup) {
    const rig = makeRig({ sr: 12000 });
    const bar = rig.barSamples;
    rig.gen.setParam("pattern", 2);
    // a drum loop: a decaying low thump on each beat, a short noisy tick on each eighth
    let seed = 7;
    const noise = () => {
        seed = (seed * 16807) % 2147483647;
        return seed / 1073741823.5 - 1;
    };
    rig.run(2 * bar + Math.round(0.42 * bar), (i) => {
        const pos = i % bar;
        const beat = pos % (bar / 4);
        const eighth = pos % (bar / 8);
        const kick = Math.exp(-beat / 900) * Math.sin(2 * Math.PI * 55 * (beat / 12000) * (1 + Math.exp(-beat / 300)));
        const hat = Math.exp(-eighth / 120) * noise() * 0.35;
        const v = 0.8 * kick + hat;
        return [v, v];
    });
    const { ctx, mg } = rig.ui;
    ctx.pattern(2);
    ctx.active(2);
    ctx.phase(0.42);
    if (setup) {
        setup(ctx);
    }
    mg.calls.length = 0;
    ctx.paint();
    const ui = callsToSvg(mg.calls, 4, 4);
    return [
        `<svg xmlns="http://www.w3.org/2000/svg" width="${DEVICE_W * 2}" height="${(DEVICE_H + 18) * 2}" ` +
            `viewBox="0 -18 ${DEVICE_W} ${DEVICE_H + 18}" font-family="Arial, Helvetica, sans-serif">`,
        `<rect x="0" y="-18" width="${DEVICE_W}" height="18" fill="#4a4a4a"/>`,
        `<circle cx="9" cy="-9" r="4" fill="#f2b84b"/>`,
        `<text x="18" y="-5.5" font-size="10" font-weight="bold" fill="#e8e8e8">Gross Warp</text>`,
        `<rect x="0" y="0" width="${DEVICE_W}" height="${DEVICE_H}" fill="#2f2f2f"/>`,
        ui,
        liveControls(2),
        "</svg>",
    ].join("\n");
}

if (require.main === module) {
    const out = process.argv[2] || path.join(__dirname, "..", "docs", "preview.svg");
    fs.mkdirSync(path.dirname(out), { recursive: true });
    fs.writeFileSync(out, `${render()}\n`);
    console.log(`wrote ${out}`);
}

module.exports = { render, callsToSvg };
