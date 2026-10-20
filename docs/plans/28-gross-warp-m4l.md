# 28 — Gross Warp: a Gross Beat-style Max for Live audio effect

- **Status:** built 2026-10-20 at the owner's request ("plan and create this for me using Max for Live on Ableton",
  with a screenshot of the Gross Warp device); the same day, the follow-up "fix what's not done, and a download link
  and instructions to install it into Ableton" added pitch lock, MIDI-triggered patterns, 2- and 4-bar patterns, any
  time signature, and the zip. Not part of QuantAnalyst: it lives on its own under `m4l/gross-warp/` and shares no
  code, build or runtime with the trading app.
- **Gate:** the DSP, the editor and the devices' Max-object logic are tested headless (node, no network, no Max): the
  gen~ code runs in a small GenExpr simulator, the jsui script against stubs of Max's `mgraphics`/`Buffer`, and the
  built patchers' cords in a message-level simulator. Max and Live are not available in this container, so the devices
  themselves are **not** loaded here; the owner's first load in Live is the real check (see "Not verified here").

## Why

Gross Beat's idea: two envelopes drawn over a bar and locked to the song. The **time** envelope says how far behind
"now" the audio plays (top = live, lower = later), so its slope is a playback speed: flat = normal, a 45° fall = a
stop, steeper = reverse, a staircase = repeats/rolls. The **volume** envelope gates. Eight patterns, switchable in time.

## What

| Area | Behaviour |
|---|---|
| Time envelope | Offset = u × Depth × pattern length, u ∈ [0, 1] (0 = top). Flat = delay, slope 1 = stopped, steeper = reverse, steps = repeats. Up to 128 points, per-segment curve. |
| Volume envelope | Gain = 1 − u × Depth. |
| Patterns | 8, each with a time and a volume envelope. `Pattern` (live.tab, automatable) selects; `Switch` quantizes the change: Instant / 1/16 / Beat / Bar / Pattern end, with a 40 ms catch-up window after each grid line. |
| Length | 1 beat, 2 beats, 1, 2 or 4 bars, in the song's time signature (beats = quarter notes, as Live's song time). |
| Sync | `[phasor~ 4n @lock 1]` gives the beat phase (its slope gives the tempo); gen~ counts beats and re-syncs the count from the Live API's `current_song_time` (nearest whole beat to position − phase, so a late value is harmless); `signature_numerator/denominator` give the bar. Transport stopped ⇒ pass-through. |
| Smooth | Jumps in the time envelope cross-fade (raised cosine, gains sum to 1) between the old and the new read head over Smooth ms; the volume envelope is slewed by the same time. No clicks at steps. |
| Pitch Lock, Grain | Two grains (sin² windows, half a grain apart) play at normal speed from the time envelope's position — backwards where it reverses; on jumps both restart in step with the new position. |
| MIDI | Companion MIDI effect *Gross Warp MIDI* (native Max objects only — `js` would defer to the low-priority thread): notes Base…Base+7 → patterns 1–8, Hold or Latch, sent on link A–D; the effect's *MIDI* menu picks the link (`[switch 4]`). Events are `trigon`/`trigoff` = pattern + 8 × parity + 16 × mode; gen~ applies the mode. |
| Depth, Dry/Wet | Scale both envelopes; dry/wet mix. At "Normal" the wet signal equals the dry one, sample for sample. |
| Editor (jsui) | Waveform of the input and output over the pattern, playhead, the edited envelope with points and curve handles, the other one ghosted; beat and bar lines. Point tool, Draw tool, grid 1/4…1/32 + triplets with swing (fractional cell counts for odd meters), Shift = no snap. Preset shapes per envelope, copy/paste across devices, clear, undo/redo. |
| State | The editor's data is a jsui blob parameter (Stored Only), saved with the Live set; Live is notified on discrete edits only (mouse-up), never during drags or playback. |
| Info View | Every control has an info text. |
| Download | `dist/GrossWarp.zip` (stored, fixed date, byte-reproducible) with the folder `Gross Warp/`: both devices, `gw_ui.js`, `INSTALL.txt`. |

## Design

```
plugin~ L,R ──► gen~ (gross_warp.genexpr) ──► plugout~
phasor~ 4n @lock 1 ──► gen~ in3            ├─ out3 pattern phase ─► snapshot~ ─► jsui (playhead, redraw)
live.thisdevice ─► live.path live_set ─► live.observer ×3 ─► gen~ (songbeats, signum, sigden), jsui (meter)
live.tab/menus/dials ─► prepend ─► gen~    └─ out4 active pattern ─► snapshot~ ─► jsui
r grosswarp_A..D ─► switch 4 (MIDI menu) ─► gen~ (trigon, trigoff)
jsui ──writes──► buffer~ ---gwenv  ◄──reads── gen~      gen~ ──writes──► buffer~ ---gwpeaks ◄──reads── jsui
```

- `---` names make the two buffers private to each device instance; the substituted names reach gen~ and the jsui by
  `[symbol ---x] → [prepend …]` at load (object-box substitution, the documented case). The MIDI links use global
  names on purpose, to connect devices.
- **Envelope buffer** (1 channel, 16 blocks × 385 frames): block = pattern × 2 + env; frame 0 = point count, then
  (x, u, curve) triples. The jsui writes points first and the count last. gen~ evaluates the breakpoints exactly per
  sample (cached segment index, bounded advance loop), so steps stay steps and ramps stay smooth.
- **Curve:** `f(t) = t^(1+3c)` for c ≥ 0, `1 − (1−t)^(1+3|c|)` for c < 0 — c = 1/3 is exactly a linear speed ramp
  (tape stop), c = −1/3 its mirror (tape start). The same formula in gen~ and in the jsui (a test checks they agree).
- **Ring buffer** 2²¹ frames × 2 (≈ 44 s at 48 kHz); offsets are clamped to half of it, so a grain's distance behind
  the write head is never ambiguous. Reads use 4-point Hermite, linear within 1 sample of "now".
- **gen~ rules followed** (from Cycling '74 forum findings, docs host not reachable here): `peek` always assigned on its
  own line; no `break`/`+=`; loop exit in the `for` condition; every variable initialised at top level before any block;
  History read at the top and written at the end; no names that are gen operators. A lint test enforces these.
- **Live API wiring** follows Ableton's CPU Reporter device: `path live_set` → `live.path` → `[t b l]` → id into the
  observer's right inlet, then `property …` into its left; the observer outputs the bare value (CPU Reporter feeds it
  straight into `[t b f]`), so `[prepend songbeats]` makes the Param message.

## Files

| Path | Role |
|---|---|
| `m4l/gross-warp/src/gross_warp.genexpr` | gen~ DSP (embedded into the device's gen~ codebox at build) |
| `m4l/gross-warp/src/gw_ui.js` | jsui editor/display (ES5; ships next to the device) |
| `m4l/gross-warp/src/INSTALL.txt` | install notes, packed into the zip |
| `m4l/gross-warp/tools/build.js` | builds both patchers, the `.amxd` containers and the zip into `dist/` |
| `m4l/gross-warp/tools/gensim.js` | GenExpr-subset simulator used by the tests |
| `m4l/gross-warp/tools/maxsim.js` | Max-message simulator (trigger, unpack, gate, switch, split, counter, arithmetic, prepend, send/receive, message boxes) |
| `m4l/gross-warp/tools/preview.js` | renders `docs/preview.svg`: the real editor painting, the simulated DSP's waveform, mocked Live controls |
| `m4l/gross-warp/test/*.test.js` | DSP, editor, patcher/MIDI-chain and lint tests (`node --test`; 74 tests, ~2 min) |
| `m4l/gross-warp/README.md` | download, install, use, troubleshooting |
| `.github/workflows/ci.yml` | job `m4l`: runs the Node tests (no Max, no network) |
| `.gitattributes` | `*.amxd binary` (the container has a binary header) |

`.amxd` format: chunks `ampf`(4)=`aaaa`/`mmmm`, `meta`(4)=0, `ptch`(n)=patcher JSON + NUL — as parsed by Ableton's own
`maxdevtools/maxdiff/amxd_textconv.py` (commit 5b39dec9a203d2747ff1d08e10c849742a4e654a), which reads both built files
("Audio Effect Device", "MIDI Effect Device").

## Tests

- DSP (simulated gen~ fed by the real jsui writing the envelope buffer): Normal = bit-exact pass-through; constant
  offset = exact delay; Half Time halves the speed; Reverse ½ plays backwards; 1/16 Roll repeats the slice; Tape Stop's
  speed falls linearly; gates; stopped transport = pass-through; Depth 0; every Switch grid incl. 2- and 4-bar
  patterns and the catch-up window; 3/4, 6/8, 7/8 bars; a late song position puts the pattern in the right bar;
  pitch lock keeps a 480 Hz tone at 480 Hz under Half Time, Reverse and Tape Stop (and grains run backwards when
  reversing), passes Normal through, and toggling it or rolling under it does not click; MIDI hold/latch semantics and
  grid timing; steps do not click; peaks; gen~ and editor evaluate envelopes identically.
- Editor: presets, blob round-trip, buffer layout, channel-base detection, mouse editing, draw tool, undo/redo,
  copy/paste across devices, toolbar, overlay, 4-bar and 7/8 grids, paint with real mgraphics calls only.
- Devices: containers, every cord on both devices and in gen~, message names vs gen~ Params and jsui functions,
  buffer sizes and load order, the Live API chain, the MIDI device's note logic (parity, base note, range, links,
  thru), the effect's link switch, a note → pattern end-to-end through both patchers into the simulated gen~,
  parameters and banks, info texts, layout, the zip (contents, CRCs) and `dist/` being current.
- Lint: the gen~ rules above; the jsui uses ES5 only (also parsed with acorn `--ecma5` once).

## Not verified here (first load in Live)

1. gen~ compiles the codebox (syntax kept to constructs seen in real codeboxes; see rules above).
2. gen~ retargets `Buffer gwenv` on the message `gwenv <name>`.
3. jsui `parameter_enable` blob persistence through `getvalueof`/`setvalueof`.
4. The Buffer channel base in Max's JS (detected at runtime: 1-based tried first, 0-based fallback).
5. `midiparse` reports note-offs as velocity 0 (the MIDI device's on/off split relies on it).

If one fails, Max's console names the object; the README has a troubleshooting section.

## Limits (kept)

- A time-signature change mid-song: bars are counted from the song's start in the current meter.
- Pitch lock is a two-grain stretcher (the classic character), not a phase vocoder.
- The download is the unfrozen device plus its script (freezing makes one file; writing Max's frozen format by hand
  would risk an unloadable device).
