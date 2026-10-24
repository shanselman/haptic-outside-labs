# Golden traces

Privacy-safe, hand-authored JSON Lines that drive the **real** `SemanticInterpreter` + `HapticPolicy` with a fake
clock (SPEC §12.3). They prove logic only — not OS delivery, UIA timing, or tactile perception.

Each line is one event:

| `type` | Fields | Meaning |
|---|---|---|
| `comment` | `text` | Ignored; the first line states the expectation in words. |
| `process` | `pid`, `name` | Process name for allow/deny lists and native suppression. |
| `pointer` | `t`, `x`, `y`, `fg`, `primary` | A physical pointer observation (ms, screen px, foreground pid, primary button). |
| `element` | `t`, `req`, `x`, `y`, `pid`, `controlType`, `rid`, `bounds[x,y,w,h]`, `parentType`, `parentRid`, `parentBounds[x,y,w,h]`, `enabled`, `password`, `offscreen`, `selected`, `scale`, `range{value,min,max,small,large}`, `error` | A UIA snapshot answered at `t` for a request made at `req` for point `x,y`. `scale` is monitor DPI / 96 (sizes are judged in logical px). |
| `device` | `t`, `available` | Haptic device availability change. |
| `wheel` | `t`, `delta` | Wheel input (ignored: scroll texture is off by default). |
| `coop` | `t`, `pid`, `waveform` | A cooperating app asked for a waveform over the local pipe. |

| Trace | Expected haptics |
|---|---|
| `toolbar-slow-sweep` | Step (entering the toolbar group), then 4 Hover lands |
| `toolbar-pixel-crawl` | Step, Hover (1 px steps must not look stationary) |
| `toolbar-fast-flick` | ≤ 1 (buttons flown over are `passed-through`, not landed on) |
| `boundary-jitter` | 1 (rest `hysteresis-hold`) |
| `same-element-stationary` | 1 (redraw under a still pointer is silent) |
| `menu-nested` | Step (menu bar), Step (submenu group), Hover; Quiet style: Step, Step |
| `slider-discrete` | 1 Hover, 8 Step, 1 Collide; release then value change is silent |
| `scroll-at-limit` | 0 |
| `uia-stale-late` | 0 (`stale-snapshot-discarded`, `stale-intent`) |
| `device-disconnect` | 1 then `device-unavailable` |
| `native-duplicate-suppression` | 0 (`native-suppression:*`) |
| `security-password` | 0 (`protected-context`) |
| `excluded-app` | 0 (`process-excluded`) |

Run one: `haptics-lab replay tests/traces/menu-nested.jsonl --profile Quiet`. Run all as an experiment: `haptics-lab run EXP-070`.
Replay ticks the interpreter every 10 ms while an arrival is pending, the same way the live pipeline ticks every 15 ms:
a pointer that stops sends no input, so "landed by dwelling" needs a clock. Vocabulary rules that are easier to state
as unit tests (large targets, tab detents, edges, density back-off, adaptive landing, cooperative) live in
`tests/Haptics.Core.Tests/VocabularyTests.cs`.

Changing a rule must change these expectations explicitly (`Exp070Replay.Expectations`, `ReplayTests`).
