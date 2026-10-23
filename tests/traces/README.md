# Golden traces

Privacy-safe, hand-authored JSON Lines that drive the **real** `SemanticInterpreter` + `HapticPolicy` with a fake
clock (SPEC §12.3). They prove logic only — not OS delivery, UIA timing, or tactile perception.

Each line is one event:

| `type` | Fields | Meaning |
|---|---|---|
| `comment` | `text` | Ignored; the first line states the expectation in words. |
| `process` | `pid`, `name` | Process name for allow/deny lists and native suppression. |
| `pointer` | `t`, `x`, `y`, `fg`, `primary` | A physical pointer observation (ms, screen px, foreground pid, primary button). |
| `element` | `t`, `req`, `x`, `y`, `pid`, `controlType`, `rid`, `bounds[x,y,w,h]`, `parentType`, `parentRid`, `enabled`, `password`, `offscreen`, `range{value,min,max,small,large}`, `error` | A UIA snapshot answered at `t` for a request made at `req` for point `x,y`. |
| `device` | `t`, `available` | Haptic device availability change. |
| `wheel` | `t`, `delta` | Wheel input (ignored: scroll texture is off by default). |

| Trace | Expected haptics |
|---|---|
| `toolbar-slow-sweep` | 5 Hover |
| `toolbar-fast-flick` | ≤ 1 (rest `fast-flick`) |
| `boundary-jitter` | 1 (rest `hysteresis-hold`) |
| `same-element-stationary` | 1 (redraw under a still pointer is silent) |
| `menu-nested` | Hover, Step (group boundary; hover coalesced), Hover |
| `slider-discrete` | 1 Hover, 8 Step, 1 Collide; release then value change is silent |
| `scroll-at-limit` | 0 |
| `uia-stale-late` | 0 (`stale-snapshot-discarded`, `stale-intent`) |
| `device-disconnect` | 1 then `device-unavailable` |
| `native-duplicate-suppression` | 0 (`native-suppression:*`) |
| `security-password` | 0 (`protected-context`) |
| `excluded-app` | 0 (`process-excluded`) |

Run one: `haptics-lab replay tests/traces/menu-nested.jsonl --profile Quiet`. Run all as an experiment: `haptics-lab run EXP-070`.
Changing a rule must change these expectations explicitly (`Exp070Replay.Expectations`, `ReplayTests`).
