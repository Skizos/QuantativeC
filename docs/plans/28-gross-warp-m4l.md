# 28 — Gross Warp: a Gross Beat-style Max for Live audio effect

- **Status:** built 2026-10-20 at the owner's request ("plan and create this for me using Max for Live on Ableton",
  with a screenshot of the Gross Warp device). Not part of QuantAnalyst: it lives on its own under `m4l/gross-warp/`
  and shares no code, build or runtime with the trading app.
- **Gate:** the DSP and the editor are tested headless (node, no network, no Max): the gen~ code runs in a small
  GenExpr simulator, the jsui script runs against stubs of Max's `mgraphics`/`Buffer`. Max and Live are not available
  in this container, so the device itself is **not** loaded here; the owner's first load in Live is the real check
  (see "Not verified here").

## Why

Gross Beat's idea: two envelopes drawn over a bar and locked to the song. The **time** envelope says how far behind
"now" the audio plays (top = live, lower = later), so its slope is a playback speed: flat = normal, a 45° fall = a
stop, steeper = reverse, a staircase = repeats/rolls. The **volume** envelope gates. Eight patterns, switchable in time.

## What

| Area | Behaviour |
|---|---|
| Time envelope | Offset = u × Depth × pattern length, u ∈ [0, 1] (0 = top). Flat = delay, slope 1 = stopped, steeper = reverse, steps = repeats. Up to 128 points, per-segment curve. |
| Volume envelope | Gain = 1 − u × Depth. |
| Patterns | 8, each with a time and a volume envelope. `Pattern` (live.tab, automatable) selects; `Switch` quantizes the change: Instant / 1/16 / Beat / Pattern end. |
| Length | 1 beat, 2 beats or 1 bar per pattern (4/4). |
| Sync | `[phasor~ 1n @lock 1]` follows Live's transport; the tempo is derived from its slope. Transport stopped ⇒ pass-through. |
| Smooth | Jumps in the time envelope cross-fade (equal power) between the old and the new read head over Smooth ms; the volume envelope is slewed by the same time. No clicks at steps. |
| Depth, Dry/Wet | Scale both envelopes; dry/wet mix. At "Normal" the wet signal equals the dry one, sample for sample (no comb filtering). |
| Editor (jsui) | Waveform of the input (and output) over the pattern, playhead, the edited envelope with points and curve handles, the other one ghosted. Point tool (add, drag, Alt-click delete, curve handles), Draw tool (paint steps per grid cell), grid 1/4…1/32 + triplets with swing, Shift = no snap. Preset shapes per envelope (overlay list + ◂ ▸), copy/paste (shared across instances), clear, undo/redo. |
| Presets | Time: Normal, Half Time, Half Time ½, Tape Stop, Tape Stop Bar, Tape Start, Reverse ½, Reverse Beats, 1/16 Roll, Stutter ¼, Roll Build, Scratch, Slow→Fast, Delay ⅛. Volume: Full, Gate ⅛, Gate 1/16, Trance Gate, Pump ¼, Fade In, Fade Out, Off-Beat, Chop Last Beat, Swell ½. The 8 patterns start with useful pairs. |
| State | The editor's data is a jsui blob parameter (Stored Only), saved with the Live set; Live is notified on discrete edits only (mouse-up), never during drags or playback, so the undo history is not flooded. |

## Design

```
plugin~ L,R ──► gen~ (gross_warp.genexpr) ──► plugout~
phasor~ 1n @lock 1 ──► gen~ in3          ├─ out3 pattern phase ─► snapshot~ ─► jsui (playhead, redraw)
live.tab/menus/dials ─► prepend ─► gen~  └─ out4 active pattern ─► snapshot~ ─► jsui
jsui ──writes──► buffer~ ---gwenv  ◄──reads── gen~      gen~ ──writes──► buffer~ ---gwpeaks ◄──reads── jsui
```

- `---` names make the two buffers private to each device instance; the substituted names reach gen~ and the jsui by
  `[symbol ---x] → [prepend …]` at load (object-box substitution, the documented case).
- **Envelope buffer** (1 channel, 16 blocks × 385 frames): block = pattern × 2 + env; frame 0 = point count, then
  (x, u, curve) triples. The jsui writes points first and the count last. gen~ evaluates the breakpoints exactly per
  sample (cached segment index, bounded advance loop), so steps stay steps and ramps stay smooth.
- **Curve:** `f(t) = t^(1+3c)` for c ≥ 0, `1 − (1−t)^(1+3|c|)` for c < 0 — c = 1/3 is exactly a linear speed ramp
  (tape stop), c = −1/3 its mirror (tape start). The same formula in gen~ and in the jsui (a test checks they agree).
- **Ring buffer** 2²⁰ frames × 2 (≈ 21.8 s at 48 kHz); offsets are clamped to it. Reads use 4-point Hermite, linear
  within 1 sample of "now" (so no unwritten sample is ever read).
- **gen~ rules followed** (from Cycling '74 forum findings, docs host not reachable here): `peek` always assigned on its
  own line; no `break`/`+=`; loop exit in the `for` condition; every variable initialised at top level before any block;
  History read at the top and written at the end (unambiguous semantics); no names that are gen operators
  (`wave`, `rate`, `delta`, `e`, …). A lint test enforces these.

## Files

| Path | Role |
|---|---|
| `m4l/gross-warp/src/gross_warp.genexpr` | gen~ DSP (embedded into the device's gen~ codebox at build) |
| `m4l/gross-warp/src/gw_ui.js` | jsui editor/display (ES5; ships next to the device) |
| `m4l/gross-warp/tools/build.js` | builds the patcher JSON and writes `dist/Gross Warp/Gross Warp.amxd` (+ `gw_ui.js`) |
| `m4l/gross-warp/tools/gensim.js` | GenExpr-subset simulator used by the tests |
| `m4l/gross-warp/test/*.test.js` | DSP, editor, patcher and lint tests (`node --test`; 54 tests, ~30 s) |
| `m4l/gross-warp/tools/preview.js` | renders `docs/preview.svg`: the real editor painting, the simulated DSP's waveform, mocked Live controls |
| `m4l/gross-warp/README.md` | install, use, freeze, troubleshooting |
| `.github/workflows/ci.yml` | job `m4l`: runs the Node tests (no Max, no network) |
| `.gitattributes` | `*.amxd binary` (the container has a binary header) |

`.amxd` format: chunks `ampf`(4)=`aaaa`, `meta`(4)=0, `ptch`(n)=patcher JSON + NUL — as parsed by Ableton's own
`maxdevtools/maxdiff/amxd_textconv.py` (commit 5b39dec9a203d2747ff1d08e10c849742a4e654a) and seen in its audio-effect
test devices.

## Tests

- DSP (simulated gen~ fed by the real jsui writing the envelope buffer): Normal = bit-exact pass-through; constant
  offset = exact delay; Half Time halves a sine's frequency; Reverse ½ plays backwards; 1/16 Roll repeats the slice;
  gate silences off-steps; transport stopped = pass-through; quantized pattern switch lands on the boundary; steps do
  not click (bounded sample-to-sample change); peaks buffer is filled.
- Editor: envelope evaluation equals gen~'s; every preset is valid; serialize ⇄ deserialize; add/drag/snap/delete,
  curve handles, draw tool, undo/redo, copy/paste, overlay; buffer layout; channel-base detection; paint runs.
- Patcher: every line joins existing boxes on valid inlets/outlets; parameter names unique; the `.amxd` in `dist/` is
  byte-identical to a fresh build; the amxd parses with the chunk reader above.
- Lint: the gen~ rules above; the jsui uses ES5 only.

## Not verified here (first load in Live)

1. gen~ compiles the codebox (syntax kept to constructs seen in real codeboxes; see rules above).
2. gen~ retargets `Buffer gwenv` on the message `gwenv <name>`.
3. jsui `parameter_enable` blob persistence through `getvalueof`/`setvalueof`.
4. The Buffer channel base in Max's JS (detected at runtime: 1-based tried first, 0-based fallback).

If one fails, Max's console names the object; the README has a troubleshooting section for each.

## Out of scope (later)

- "Pitch lock" (time-stretch without pitch change: needs a granular/phase-vocoder engine).
- MIDI-triggered patterns (Max Audio Effects get no MIDI; Live automation of `Pattern` covers the use).
- Pattern lengths over one bar, non-4/4 meters.
