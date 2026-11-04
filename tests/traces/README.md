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
| `toolbar-slow-sweep` | Everything with group pulses disabled: 5 Hover lands after dwell |
| `toolbar-pixel-crawl` | Hover, Hover (1 px steps must not look stationary) |
| `toolbar-fast-flick` | ≤ 1 (buttons flown over are `passed-through`, not landed on) |
| `boundary-jitter` | 1 (rest `hysteresis-hold`) |
| `same-element-stationary` | 1 (redraw under a still pointer is silent) |
| `menu-nested` | 1 Hover on the menu launcher, leaf choices silent; Quiet: 0 |
| `slider-discrete` | Focused: 1 Hover, 8 Step, 1 Collide; Quiet omits Hover; release then value change is silent |
| `slider-thumb-hover` | Focused/Everything: Hover at 132/732, Step at 1002, Collide at 1302; track, fly-by, drag/release and stationary thumb movement add no hover. Quiet keeps Step/Collide |
| `quiet-subset-focused` | Focused: 3 Hover, 2 Step, 1 Collide; Quiet: 1 Hover, 2 Step, 1 Collide only for retained scenarios; pressing cancels pending slider hover |
| `focused-toggle-changes` | 4 Steps: checkbox On/Off and toggle-button Off/On; hover, programmatic changes and failed clicks are silent |
| `style-inclusion-checkbox-repeat` | Focused: inline Hover at 35 plus 4 alternating checkbox Steps; Everything adds picker-option Hover at 335; Quiet: inline Hover only |
| `everything-toggle-links-options` | Everything: Hover at 52/902/1652/1952 and click-confirmed Step at 330; icon/padding continuity, no press/release or checkbox hover. Focused: Step and inline Hover at 1652. Quiet: inline Hover only |
| `focused-navigation-hover` | 2 Hovers on tab/picker; picker options, toolbar links, ordinary toolbar and leaf menu choices silent, along with selected tabs and disabled targets |
| `focused-content-filter` | 3 Hovers on tab/menu-launcher/picker; authored people/content cards, picker options, toolbar commands and leaf menu choices silent |
| `focused-browser-links` | Focused/Quiet: 3 large inline-link Hovers at 952/1252/1552; Everything also hovers on other links and browser buttons, including toggle-capable panels; unsafe/tiny links stay silent |
| `size-aware-inline-dwell` | Quiet: Hover at 30/350/650/930/1250; Focused adds thumb Hover at 2130; Everything also adds non-inline link Hover at 1530. Exact 30/50 ms boundaries, DPI scaling, every tab at 50 ms and a silent 29 ms fly-by |
| `focused-group-link-filter` | Quiet: inline Hover at 930; Focused adds contained text-sized Group-link Hover at 30; Everything also emits on the ListItem rail link at 350 and full-parent card at 650. Group-link fly-by silent. Roles/dimensions based on captured metadata; timing/coordinates authored |
| `universal-dwell-reentry` | Focused/Everything: 2 small-target Hovers at 32 and 361 ms; same-ID reentry starts a new dwell, fly-by silent. Quiet: 0 |
| `tab-delayed-landing` | 3 Hover arrivals in every style at 70, 370 and 690 ms despite late crossed-tab replies, near-edge stops and brief edge oscillation |
| `focused-header-continuity` | 2 Hovers at 32 and 650 ms: menu/tab label and padding snapshots share owner identity and dwell; no duplicate after cooldown; selected/disabled/leaf-menu negatives silent |
| `focused-browser-sweep` | 13 Hovers: twelve browser tabs without density thinning, then a fast arrival silent at 49 ms and landing at 50 ms dwell |
| `focused-native-caption` | 0: native Close removed from Focused, along with all other captions |
| `scroll-at-limit` | 0 |
| `uia-stale-late` | 1 Hover at 570 ms from valid delayed metadata; the earlier 319 ms-old lookup is discarded |
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
| `transient-error-button` | 1 Hover on a picker fixture; a lookup error inside the same target does not manufacture another arrival |
| `transient-error-slider` | 1 Hover in Focused/Everything. A lookup error preserves identity/range history without a second arrival |
| `dwell-lookup-40ms` | 1 Hover at 50 ms; provider latency completes the elapsed 30 ms small-target dwell without restarting it |
| `dwell-lookup-150ms` | 1 Hover at 160 ms; valid delayed metadata completes the elapsed dwell immediately |
| `strip-edge-destination-safety` | Everything: 6 Hover arrivals (including group entry); every strip exit is silent, including safe destinations |
| `cooperative-context-safety` | 1 safe Align; protected/disabled/offscreen/excluded/error contexts stay silent |
| `caption-close-hover` | 0: geometry-only Close removed from Focused, even with its legacy setting On |

Run one: `haptics-lab replay tests/traces/menu-nested.jsonl --profile Quiet`. Run all as an experiment: `haptics-lab run EXP-070`.
EXP-070 uses Focused except for `strip-edge-destination-safety` and the five toolbar hover traces
(`toolbar-slow-sweep`, `toolbar-pixel-crawl`, `toolbar-fast-flick`, `boundary-jitter`, `same-element-stationary`).
Those use Everything; the toolbar-only cases disable group pulses to isolate hover. Each result records its profile.
Replay ticks the interpreter every 10 ms while an arrival is pending, the same way the live pipeline ticks every 15 ms:
a pointer that stops sends no input, so "landed by dwelling" needs a clock. Vocabulary rules that are easier to state
as unit tests (large targets, tab detents, edges, explicit density configuration, cooperative) live in
`tests/Haptics.Core.Tests/VocabularyTests.cs`.

Changing a rule must change these expectations explicitly (`Exp070Replay.Expectations`, `ReplayTests`).
EXP-070 fails if any trace lacks an expectation or any expectation lacks its trace, including an empty directory.

Old pointer lines default to `origin: "Replay"`, `handle: 0`, and `inputClass: "Handleless"`.
A nonzero handle defaults to `"Handle"`; `"Promoted"` can be authored explicitly. Origin and input class
are independent attribution metadata, not permission to emit or evidence of physical input. Explicit
unknown enum names fail replay instead of silently changing attribution. A `foreground` event supplies
the default foreground PID for following pointer lines; an explicit `fg` still wins. Without a
`foreground` event, omitted `fg` keeps the legacy zero default.

An element's optional `generation` is the interaction token captured when its lookup was requested,
not when answered. Omission means a request in the current generation. Foreground changes, device
handle/class changes and explicit invalidation advance the token; old answers are rejected even at
the same point and timestamp. A device switch cancels dwell but retains the current identity so
staying over the same element cannot manufacture another arrival.

Focused metadata on an `element`: `toggle` means a toggle-state provider exists; `on` is true/false or omitted/null
for an unknown/indeterminate state. `dropdown` indicates expand/collapse capability (not sufficient to enable hover),
`buttonOwner` identifies a Button resolved from a direct Text/Image child without a grandparent read,
`browserToolbar` identifies a recognized browser command (silent in Focused, not a setting toggle),
`headerOwner` identifies a TabItem/MenuItem resolved from its direct Text/Image child without a grandparent read,
`thumbBounds` is the directly observed slider Thumb rectangle (owner `bounds` and identity remain unchanged),
and `caption` is `None`, `Close`, `Minimize` or `Maximize` from a native hit test. These are authored observations,
not inferred from a label or screen location.
