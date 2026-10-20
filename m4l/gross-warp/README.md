# Gross Warp — a Gross Beat-style Max for Live effect

Half time, tape stops, stutters, rolls, scratches, reverses and gates, drawn as two envelopes and locked to your song.
Eight patterns, 1 beat to 4 bars, any time signature, optional pitch lock, and patterns you can play from MIDI notes.

![Gross Warp (approximate render, see below)](docs/preview.svg)

*Rendered by `tools/preview.js` from the real editor script and the simulated DSP, with Live's own dials and menus
mocked; Live draws those slightly differently.*

## Download

**[⬇ GrossWarp.zip](https://github.com/Skizos/QuantativeC/raw/main/m4l/gross-warp/dist/GrossWarp.zip)**
(or [from this branch's copy](dist/GrossWarp.zip) → *Download raw file*). The zip holds one folder, `Gross Warp`:

| File | |
|---|---|
| `Gross Warp.amxd` | the effect (Max audio effect) |
| `Gross Warp MIDI.amxd` | optional: plays the effect's patterns from MIDI notes (Max MIDI effect) |
| `gw_ui.js` | the effect's editor — keep it in the same folder as `Gross Warp.amxd` |
| `INSTALL.txt` | these instructions, short |

## Install in Ableton Live

You need **Live 12 Suite**, or Live 12 Standard with the Max for Live add-on.

1. **Unzip** `GrossWarp.zip`. You get a folder called `Gross Warp`.
2. **Put the folder where Live can see it** — either:
   - move it into your **User Library**:
     macOS `~/Music/Ableton/User Library/`, Windows `C:\Users\<you>\Documents\Ableton\User Library\`
     (*Live → Settings → Library* shows where yours is); **or**
   - leave it anywhere, and in Live's browser click **Add Folder…** under *Places* and pick the `Gross Warp` folder.
3. In Live's browser open *User Library* (or the folder you added) → `Gross Warp`, and **drag `Gross Warp`** onto an
   audio track, a drum rack, a group or the Master.
4. **Press Play.** The effect follows Live's transport; while Live is stopped it passes the sound through untouched.

Keep the files together: `Gross Warp.amxd` loads `gw_ui.js` from its own folder. For a single self-contained file
(e.g. to share), click the wrench in the device's title bar (Max opens), click **Freeze** (the snowflake), and save it
under a new name.

### Optional: trigger patterns from MIDI

1. Make a **MIDI track** and drag **`Gross Warp MIDI`** onto it.
2. Set its **Link** (A–D), and set **MIDI** on Gross Warp to the same letter.
3. Notes from **Base** (default C1) up play patterns: C1 = 1, C♯1 = 2, … G1 = 8.
   - **Hold**: the pattern plays while its note is down, then the *Pattern* control takes over again.
   - **Latch**: a note switches to its pattern; the same note again releases it.
4. Draw the notes in a MIDI clip (or arm the track and play them). Triggers follow *Switch*, so notes on the beat
   switch on the beat. Several Gross Warps on one link all follow the same notes.

## How it works in one paragraph

The display shows one pattern. The **time envelope** (blue) says how far behind "now" the sound plays: at the top it is
live; lower plays later, down to one whole pattern late at the bottom. So its **slope is a speed**: flat = normal,
falling at 45° = stopped, steeper = backwards, rising = faster than live (catching up), and a **staircase repeats** a
slice (each step down by one grid step replays it). The **volume envelope** (orange) is a gain: top = full, bottom =
silent. Steps in the time envelope cross-fade over *Smooth* ms, so they don't click. With **Pitch Lock**, the sound is
played as short overlapping grains at normal speed from wherever the time envelope points, so slowing, stopping or
reversing keeps the original pitch.

## Controls

| Control | What it does |
|---|---|
| **1–8** (Pattern) | The pattern to play and edit. Automate it, or map it to Push / a controller. |
| **Switch** | When a new pattern takes over: *Instant*, next *1/16*, next *Beat*, next *Bar*, or the end of the *Pattern*. A change that lands up to 40 ms after a grid line (a note on the beat) still takes that line. |
| **Length** | 1 beat, 2 beats, 1 bar, 2 bars or 4 bars, in the song's time signature. |
| **Smooth** | Cross-fade time at steps in the time envelope, and slew of the volume envelope (0.1–50 ms). |
| **Depth** | Scales both envelopes (0 % = no effect). |
| **Dry/Wet** | Mix with the input. At *Normal* the wet signal is identical to the dry one, so mixing never phases. |
| **Pitch** Off / Lock | Lock: time effects keep the original pitch (granular). |
| **Grain** | Grain size for Pitch Lock (10–250 ms): short for drums, long for pads and vocals. |
| **MIDI** Off / A–D | Follow a *Gross Warp MIDI* device on the same link. |
| **Time / Volume** | Which envelope you edit (the other is shown faintly). |
| **< preset >** | Load a preset shape into the edited envelope; click the name for the full list. |
| **Point / Draw** | Editing tool (below). |
| **Grid** | Snap: Off, 1/4, 1/8, 1/16, 1/32, 1/8T, 1/16T. Click the name to cycle. |
| **Swing** | Moves every second grid line later (0–75 %). Click the value to reset. |
| **Copy / Paste** | The edited envelope; the clipboard is shared by every Gross Warp in the set. |
| **Clear / Undo / Redo** | Reset the edited envelope to neutral; undo/redo editor changes. |

Every control also explains itself in Live's Info View.

### Mouse

| Point tool | |
|---|---|
| Click empty space | Add a point (snapped to the grid; time offsets also snap vertically to grid steps). Keep dragging to place it. |
| Drag a point | Move it (it can't pass its neighbours; the first and last stay at the ends). Two points on one grid line make a step. |
| Drag a small circle | Bend that segment. **Alt**-click the circle: straight again. |
| **Alt**-click or double-click a point | Delete it. |
| **Shift** while clicking/dragging | No snapping. |

| Draw tool | |
|---|---|
| Click / drag | Paint flat steps, one grid cell at a time (1/32 notes with the grid off). |
| **Alt**-drag | Erase to neutral. |

### Presets

**Time:** Normal, Half Time, Half Time 1/2, Tape Stop, Tape Stop Bar, Tape Start, Reverse 1/2, Reverse Beats,
1/16 Roll, Stutter 1/4, Roll Build, Scratch, Slow > Fast, Delay 1/8.
**Volume:** Full, Gate 1/8, Gate 1/16, Trance Gate, Pump 1/4, Fade In, Fade Out, Off-Beat, Chop Last Beat, Swell 1/2.

The eight patterns start as: 1 Normal · 2 Half Time · 3 1/16 Roll · 4 Tape Stop · 5 Reverse 1/2 · 6 Gate 1/16 ·
7 Stutter 1/4 + Trance Gate · 8 Scratch + Pump 1/4. Preset steps are fractions of the pattern (made for 1-bar
patterns in 4/4), so they scale with *Length*.

Everything you draw is saved with the Live set. Save a Live device preset (the floppy icon) to keep a set of eight
patterns for other projects.

## Inside

```
plugin~ ─► gen~ (src/gross_warp.genexpr) ─► plugout~
phasor~ 4n @lock 1 (Live's beat) ─► gen~ ◄─ live.observer: song position, time signature (Live API)
editor (jsui, src/gw_ui.js) ─► buffer~ ---gwenv (envelope points) ─► gen~ ─► buffer~ ---gwpeaks ─► editor
Gross Warp MIDI: midiin ─► midiparse ─► note logic ─► send grosswarp_A..D ─► receive ─► switch (MIDI) ─► gen~
```

- **Timing:** gen~ gets a quarter-note phase locked to Live's transport, counts beats itself, and re-syncs the count
  from Live's song position (which arrives a little late; rounding to the nearest beat makes that harmless). Bars come
  from Live's time signature, so patterns stay on the song's bars in any meter, and 2- and 4-bar patterns start on
  bars 1, 3, … / 1, 5, … of the song.
- **Audio:** the input goes into a 2 × 2²¹-sample ring (≈ 44 s at 48 kHz; offsets up to half of it) read behind "now"
  with 4-point Hermite interpolation. Envelope points are evaluated exactly every sample (steps stay sharp, ramps
  smooth). A jump in the offset starts a raised-cosine cross-fade from the old read head (the gains sum to 1, so it
  never bumps); the volume is slewed.
- **Pitch Lock:** two grains with sin² windows half a grain apart (they sum to 1). Each grain starts where the time
  envelope points and plays at normal speed — backwards where the envelope reverses. On a jump both grains restart in
  step with the new position, under the same cross-fade.
- **MIDI:** Gross Warp MIDI is built from native Max objects only (a `js` object would delay notes to the low-priority
  thread). Each note becomes `trigon`/`trigoff` with *pattern + 8 × parity + 16 × mode*; the parity alternates so a
  repeated note is still seen; gen~ applies Hold/Latch.
- **Editor:** 8 × 2 envelopes of up to 128 points (x, level, curve), mirrored into the buffer on every change; saved as
  a hidden "blob" parameter in the Live set. Live is told once per edit (mouse-up), so its undo history gets one step
  per edit. `---` buffer names are unique per device, so several Gross Warps never share envelopes.

## Development

Plain Node (20+), no dependencies, no Max needed:

```
cd m4l/gross-warp
npm test                # 74 tests: DSP, editor, devices, MIDI chain, lint (~2 min)
npm run build           # rebuild dist/ (both devices, the script, the zip) after changing src/
node tools/preview.js   # re-render docs/preview.svg
```

- `tools/gensim.js` runs the gen~ codebox text itself (a small GenExpr simulator), fed by envelopes the real editor
  script wrote: *Half Time* reads at exactly half speed, *Normal* is bit-identical, pitch-locked Half Time keeps a
  480 Hz tone at 480 Hz, bars of 7/8 are 3.5 beats, a late song position still lands on the right beat.
- `tools/maxsim.js` runs the built patchers' own cords for the control logic made of Max objects: a note into
  Gross Warp MIDI comes out of the effect's gen~ as the right pattern.
- `tools/build.js` writes the devices (`ampf`/`aaaa` and `mmmm`, `meta`, `ptch` chunks — the layout Ableton's
  `maxdevtools` reads; its parser reads both files) and a stored zip with a fixed date, so a rebuild is byte-identical;
  `--check` fails if `dist/` is stale, and a test runs it.
- The lint tests hold the sources to what Max accepts: ES5 for `jsui`; for gen~, `peek` only as a plain assignment, no
  `break`/`+=`, no names that are gen~ operators, every variable first set at top level.

## If something is off on first load

These devices were built and tested headless; they have not been opened in Max or Live yet. Open Max's console
(wrench icon in the device title bar → *Window → Max Console*) and look for red lines:

| Symptom | Likely cause and fix |
|---|---|
| Grey/empty display, console says it can't find `gw_ui.js` | The script isn't next to the `.amxd`. Keep the folder together, or freeze the device. |
| A gen~ error, audio silent | The codebox didn't compile: double-click `gen~` in the Max editor, the error is shown under the code — please send it to me. |
| Editor works but the sound never changes | gen~ didn't pick up the envelope buffer (it is pointed at it by the message `gwenv <name>` at load). Send me the console line. |
| Patterns don't line up with Live's bars | The Live API values didn't arrive: check the console for `live.observer` errors. |
| Drawings aren't there after reopening the set | The jsui blob parameter didn't store. Device presets still keep them; please report it. |
| MIDI notes do nothing | Gross Warp's *MIDI* must be set to the same letter as Gross Warp MIDI's *Link*, and the notes must be in Base…Base+7. |
| Nothing happens while Live is stopped | By design: it passes audio through until the transport runs. |

## Limits

- A time-signature change in the middle of a song: bars are counted from the song's start in the current meter, as
  Live's own beat-locked objects do.
- Pitch Lock is a simple two-grain stretcher: transients smear a little at long grain sizes, and very slow speeds sound
  grainy — that's the classic character, not a phase vocoder.
- The download is the unfrozen device plus its script; freezing (above) makes one file.
