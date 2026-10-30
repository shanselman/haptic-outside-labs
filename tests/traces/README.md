# Golden traces

Privacy-safe, hand-authored JSON Lines that drive the **real** `SemanticInterpreter` + `HapticPolicy` with a fake
clock (SPEC §12.3). They prove logic only — not OS delivery, UIA timing, or tactile perception.

Each line is one event:

| `type` | Fields | Meaning |
|---|---|---|
| `comment` | `text` | Ignored; the first line states the expectation in words. |
| `process` | `pid`, `name` | Process name for allow/deny lists and native suppression. |
| `pointer` | `t`, `x`, `y`, `fg`, `primary`, optional `handle`, `origin`, `inputClass` | An authored pointer observation (ms, screen px, foreground pid, primary button); never physical proof. |
| `element` | `t`, `req`, `x`, `y`, `pid`, `controlType`, `rid`, `bounds[x,y,w,h]`, `parentType`, `parentRid`, `parentBounds[x,y,w,h]`, `enabled`, `password`, `offscreen`, `selected`, `scale`, `range{value,min,max,small,large}`, `error` | A UIA snapshot answered at `t` for a request made at `req` for point `x,y`. `scale` is monitor DPI / 96 (sizes are judged in logical px). |
| `device` | `t`, `available` or `arrived`, optional `handle` | Availability or device arrival/removal. `arrived` takes precedence; replay still uses today's global availability policy. |
| `foreground` | `t`, `pid` | Foreground change without pointer movement; reuses the interpreter's existing foreground transition. |
| `wheel` | `t`, `delta` | Wheel input (ignored: scroll texture is off by default). |
| `coop` | `t`, `pid`, `waveform` | A cooperating app asked for a waveform over the local pipe. |
| `tick` | `t`, optional `skipIntermediate` | Explicit timer callback; `skipIntermediate: true` models a scheduler stall by omitting the usual 10 ms ticks since the last event. |

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
| `generation-stale-context` | 1 fresh Hover; old foreground/device answers are discarded |
| `device-change-pending` | 0; switching cancels the pending arrival without re-arrival |
| `foreground-change-stationary` | 1 Hover; click-to-activate without movement does not re-arrive |
| `device-removed-stationary-tap` | 1 Hover; removal and a later stationary tap from a present device do not re-arrive |
| `pending-metadata-safety` | 1 later safe Hover; pending protected/disabled/offscreen/error arrivals cancel |
| `pending-late-tick` | 0; a five-second callback delay expires instead of rewriting cause time |

Run one: `haptics-lab replay tests/traces/menu-nested.jsonl --profile Quiet`. Run all as an experiment: `haptics-lab run EXP-070`.
Replay ticks the interpreter every 10 ms while an arrival is pending, the same way the live pipeline ticks every 15 ms:
a pointer that stops sends no input, so "landed by dwelling" needs a clock. Vocabulary rules that are easier to state
as unit tests (large targets, tab detents, edges, density back-off, adaptive landing, cooperative) live in
`tests/Haptics.Core.Tests/VocabularyTests.cs`.

Changing a rule must change these expectations explicitly (`Exp070Replay.Expectations`, `ReplayTests`).
EXP-070 fails if any trace lacks an expectation or any expectation lacks its trace, including an empty directory.

Old pointer lines default to `origin: "Replay"`, `handle: 0`, and `inputClass: "Handleless"`.
A nonzero handle defaults to `"Handle"`; `"Promoted"` can be authored explicitly. Origin and input class
are independent attribution metadata, not permission to emit or evidence of physical input. Explicit
unknown enum names fail replay instead of silently changing attribution. A `foreground` event supplies
the default foreground PID for following pointer lines; an explicit `fg` still wins. Without a
`foreground` event, omitted `fg` keeps the legacy zero default. The 13 existing traces are unchanged.

An element's optional `generation` is the interaction token captured when its lookup was requested,
not when answered. Omission means a request in the current generation. Foreground changes, device
handle/class changes and explicit invalidation advance the token; old answers are rejected even at
the same point and timestamp. A device switch cancels dwell but retains the current identity so
staying over the same element cannot manufacture another arrival.
