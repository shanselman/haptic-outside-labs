# Design notes — the thinking behind this implementation

These notes explain *why* the code looks the way it does: the bets, the trade-offs, and the things that were
deliberately left out. The spec ([`SPEC.md`](SPEC.md)) says what to build; this says how we interpreted it.

**Current style decisions (2026-11-04):** SPEC section 8.5 supersedes the older experiments below.
Inline-link Hover is enabled in all styles. Focused also accepts Hyperlinks directly under Group at most 24 logical
px tall, fully contained in a parent at least twice their height. This uses existing metadata, not content reads
or CSS matching; it is a heuristic and can admit compact button-like links. Quiet remains inline-only.
Other links and dropdown options are Everything-only.
Dwell is 30 ms for small controls and 50 ms for all tabs or targets at least 56 logical px tall or 48 by 48.
Slider size means thumb size; missing bounds use 50 ms. Fresh geometry may extend, never shorten a visit's dwell.
Checkbox hover stays disabled, but Everything restores
toggle-button Hover (including formatting buttons and Teams app-rail buttons). Live rail metadata exposed
toggle Buttons and Image parts; direct Text/Image parts now share their immediate Button/Hyperlink owner's
identity and restrictive safety flags without a second parent read. Taskbar discovery, caching, pacing and
settings are removed; primary/secondary taskbars are rejected by native root class before UIA lookup.

## 1. The whole project hinges on one sentence in the docs

`InputHapticsManager` *"triggers haptic feedback on the input device that most recently delivered input to the
associated thread"* and *"must be used on the same thread on which it was created"*. That design is perfect for an
app that wants to buzz the mouse the user is pointing at *its own* window with. It is ambiguous for us: our
process is never the one the user is pointing at.

So the first question is not "what should a ribbon feel like", it is "does a thread that receives background
`WM_INPUT` (via `RIDEV_INPUTSINK`) count as *the associated thread* for that device?" Everything else is
pointless if the answer is no and there is no other supported route. That is why the build order is:

1. instruments that can **tell the difference** between "the API said yes" and "a hand felt it" (EXP-010/020);
2. a semantic layer that is useful *either way* (observation, Gate B), running in dry-run;
3. a tray that **cannot** turn on until (1) says a background route physically works on this machine.

## 2. Three candidate output routes, tested side by side

Rather than guess, EXP-020 interleaves three routes and silent controls in a single blinded session, all fired
*inside the `WM_INPUT` handler* of our raw-input thread (so "recent input" is literally microseconds old):

| Route | Mechanism | Why it might work | Why it might not |
|---|---|---|---|
| **R2** | `GetForCurrentThread()` on the raw-input thread | The thread *did* just receive device input | The OS may only credit focus/pointer-routed input, not sink input |
| **R3a** | Reuse a `SimpleHapticsController` captured from `CurrentHapticsController` while our window had mouse input | A controller object represents a device, not a focus state | It may be bound to the capturing thread, or invalidated when focus leaves |
| **R3b** | `SetOverrideHapticsController(captured)` on the raw-input thread's manager | The docs name background-thread marshaling as an intended use | Override may still need the thread to own recent input |

R3 controllers are captured only from **our own** foreground interaction (the "prime" pad). We never touch another
process's threads; EXP-023 already shows the API rejects cross-thread calls (`RPC_E_WRONG_THREAD`).

## 3. Blinding is the core of the lab, not a nice-to-have

"Did you feel it?" is extraordinarily easy to fool: expectation, the click of the mouse button, the haptic
touchpad on the same laptop, native Snap haptics. The trial design:

- **Balanced, seeded, shuffled schedule** of pulse trials per arm plus *silent controls* that go through the
  exact same code path minus the call, with the identical `NOW` cue.
- The operator answers **before** knowing the trial type. `Unsure` is a first-class answer.
- **Provenance per trial**: raw-input code must be `RIM_INPUTSINK`, input must come from the *primed* device
  handle (so the touchpad can't sneak in), the lab must not be foreground, the pointer must not be over the lab.
  Invalid trials are kept in the record but excluded from statistics.
- **Decision rule** (`PhysicalEvaluator`): hit rate ≥ 0.8, false-alarm rate ≤ 0.2, one-sided Fisher exact p ≤ 0.01,
  ≥ 5 answered trials per side, ≤ 30% unsure. With 6 pulses and 6 controls a perfect run gives p ≈ 0.001.
- **FAIL requires a baseline.** "Not felt" is only a `FAIL` if EXP-010 already passed on this machine; otherwise it
  is `INDETERMINATE`. A broken mouse must not masquerade as a platform limitation.

The answer panel is a `WS_EX_NOACTIVATE` tool window that also returns `MA_NOACTIVATE`, with label-based
"buttons" that can't take focus — answering a trial never changes the foreground app under test. When the
session ends we deliberately *don't* restore the minimized window (`SW_RESTORE` activates).

## 4. Evidence can't be upgraded by accident

`ExperimentResult.Finalize()` is called on every write and enforces, mechanically: physical claims can't be `PASS`
without `PHYSICAL` evidence; `PHYSICAL_CONFIRMED` can't appear on non-physical evidence; example records can't
pass; a failing assertion can't sit under a `PASS`. The tray reads the same ledger, keyed by a salted machine
fingerprint, so a PASS on Scott's Surface Laptop says nothing about anyone else's PC.

## 5. Observation: cheap on the hot path, isolated on the cold path

- The **raw-input thread** does almost nothing per event: read the packet, `GetCursorPos`, `GetForegroundWindow`,
  update the interpreter, and hand the latest point to the UIA client. No UIA, no disk, no process lookups.
  (`WindowFromPoint` is avoided on the hot path because it can send `WM_NCHITTEST` to a hung window.)
- **UIA lives in a worker process** (the same exe with `--uia-worker`) on a windowless MTA thread. The client keeps
  exactly one request in flight; new pointer positions *replace* the pending one (latest-wins), so a 1000 Hz mouse
  can never create a queue. A watchdog kills and restarts a worker that exceeds 250 ms and quarantines the
  foreground app for 10 s. Responses from a killed worker can't be delivered (identity check on the process).
- The worker requests **only** control type, bounds, enabled/offscreen/password flags, process id, runtime-id
  (hashed), one parent hop for group context, and `RangeValue` numbers for sliders. No `Name`, no `Value`/`Text`
  patterns. Thumb → Slider normalization happens in the worker so the interpreter sees the slider.

## 6. Semantics: fewer, truer signals

The interpreter (`SemanticInterpreter`) emits only transitions it can defend:

- `EnterControl` for enabled, on-screen controls you can *do* something with: Button, SplitButton, MenuItem (menus and
  ribbons), TabItem, CheckBox, RadioButton, Hyperlink, ComboBox, Slider. Content rows (list/tree/grid items, headers)
  are excluded as noise. The Windows 11 taskbar's XAML buttons are found by drilling into `Shell_TrayWnd`, because
  `ElementFromPoint` stops at that window. Enumerating that host costs 100–600 ms (seconds while explorer reflows),
  which is longer than the 250 ms watchdog, so a background thread in the worker refreshes a cached snapshot of
  the host's buttons and lookups answer from it without waiting. Scanning on the request path killed the worker
  every refresh, quarantined the foreground app and exhausted the restart budget, which silenced everything.
- `CrossGroupBoundary` when the parent group (ToolBar, Menu, Tab, List…) changes → a `Step`; the hover from the same
  transition is coalesced so you feel one thing, not two.
- `RangeStep` / `RangeLimit` for sliders **only while the primary button is physically down**, so keyboard or
  programmatic value changes stay silent.
- Jitter on a shared edge is held by 5 px hysteresis; UI that changes under a *stationary* pointer is silent;
  snapshots older than 250 ms or for a point the pointer has left are discarded; intents older than 45 ms are dropped.

It never emits success, failure, or drop-accepted — the generic layer cannot know outcomes. Drag-and-drop and
scroll texture are intentionally **not** implemented (SPEC §7 marks them adapter-only / off by default).

## 7. Policy defaults (starting values, not truths)

`PolicyConfig` mirrors SPEC §8.3: intensity scale 0.25, ≤4 pulses/s, ≤120/min, ≥120 ms spacing, 500 ms same-element
re-entry cooldown, 45 ms intent expiry, hover suppressed above 4000 px/s, disabled controls silent, opt-in
allowlist by default, password managers and credential UIs excluded, PowerPoint and title-bar drags left to native
haptics. Profiles: **Quiet** (boundaries + sliders), **Balanced** (+ light hover), **Experimental** (looser caps).

## 8. Things that surprised us while building

- The .NET 10 SDK doesn't recognise a `10.0.28000` TFM yet. Targeting `net10.0-windows10.0.26100.0` with
  `WindowsSdkPackageVersion=10.0.28000.87` pulls the 28000 projection that contains `InputHapticsManager`.
- `KnownSimpleHapticsControllerWaveforms` on this build already has Align/Collide/Step/Grow plus continuous ink/pencil
  waveforms; the lab only ever sends discrete ones.
- A window launched by a *background* process via bare `CreateProcess` could not be made topmost on build 28000
  (`SetWindowPos(HWND_TOPMOST)` returned success, style unchanged). The fixture is therefore launched via
  ShellExecute, and EXP-040 verifies with `WindowFromPoint` that the fixture is really on top at every probe point
  — otherwise it reports `INDETERMINATE` instead of a bogus UIA accuracy number. The first dev run *did* report 0/23
  because a maximized window covered the fixture; the pid check caught it.
- Posted (non-device) clicks into our own window leave `CurrentHapticsController` null and `TrySendHapticWaveform`
  returns false — good news for honesty (automation can't fake routing), bad news for automated physical testing.

## 9. Deliberately not built (yet)

- **No synthetic input anywhere.** EXP-031 (synthetic-vs-physical audit) would need a fixture-only `SendInput`
  helper; it isn't worth the risk until there's a physical route to compare against.
- **No DLL injection, UIAccess, browser debugging protocols, OCR, or private HID reports.** These are research-only
  tiers that need their own review (ADR 0004).
- **No WinEvent subscriptions yet.** Latest-wins hit-testing on movement is enough for the first physical tests;
  WinEvents become valuable for cache invalidation once we cache across movements (EXP-042).
- **No WPF/WinUI/WebView2 fixtures yet.** The WinForms fixture exercises Win32 + MSAA proxies; a static ARIA page is
  in `fixtures/browser` for manual Edge checks.
- **No installer, signing, or auto-start.** The tray is a simple sample (see §11).

## 10. Open questions we are recording, not blocking on (SPEC §17)

> **Answered 2026-10-23 (EXP-020):** R2 fails because the raw-input thread never acquires a controller. R3a and R3b
> both pass with the Surface Mouse. The product architecture is therefore "prime once, then drive the captured
> controller from the background". Question 1 below (controller lifetime) is now the critical open risk.

1. If R2 fails but R3a passes, how long does a captured controller stay valid across idle, sleep, re-pair?
2. With both a haptic touchpad and a haptic mouse, which device does `GetForCurrentThread()` choose under sink input?
3. Do native Snap/PowerPoint haptics and ours ever double up in practice (EXP-075)?
4. Is hover worth having at all, or are boundaries + detents the whole product (EXP-090)?

## 11. After EXP-020: the tray becomes a simple sample (2026-10-23)

Once background output was physically proven (R3a/R3b), Scott asked for the tray to be "more aggressive… a sample,
we don't need all the checks and balances". The tray now starts On with route R3a in every app at Strong intensity,
primes itself (a box at startup, plus any mouse movement over its menu), and uses loose limits: ≈12 pulses/s,
40 ms spacing, 200 ms re-entry, a 150 ms freshness window because real apps' UIA is slower than our fixture, and no
fast-flick suppression. Removed: consent dialog, evidence-locked On, allowlist, profiles. Kept because they cost
nothing and are the thesis: passive-only observation, password silence, default exclusions, one-click Off.
The lab keeps all its rigor; ADR 0002's gating now applies to the lab's gates, not the sample.

## 12. v0.2: a vocabulary, and being choosy (2026-10-24)

Once background output worked, the problem became *taste*. A buzz on every crossing is noise; the goal is that each
sensation means one thing, and fires only when it carries information.

**Four meanings, fixed waveforms.** Land = Hover, Land · big = Click, Detent = Step, Edge = Collide. The device's
richer waveforms (Align, Success, Error) are reserved for apps that actually *know* an outcome, via the cooperative
pipe. The generic layer still never fakes outcomes.

**Choosiness rules, in order of payoff:**
1. *Land, don't pass.* Every style now requires 50 ms continuously inside a target, regardless of approach speed.
   Leaving before completion cancels it; returning to the same identity rearms after an unsampled departure.
   A stopped pointer sends no input, so the interpreter needs a clock (`OnTick`). A valid delayed lookup can finish
   an already elapsed dwell through the same containment/freshness checks. Speed learning no longer gates hover.
2. *Group first.* Entering a toolbar, ribbon group, menu, or tab strip you haven't been in for 1.5 s is a Step. It
   orients you before any land. Moving within the group refreshes the memory, so working in one toolbar doesn't
   re-detent.
3. *Choices are detents.* Tabs and radio buttons are Step, not Hover: you're choosing between siblings. The one
   that's already selected is silent, since you can't go to where you already are.
4. *Size in logical px.* Big targets (≥56 logical px tall, or ≥48 both ways: Office's large ribbon buttons) land with
   Click; tiny ones (<10 logical px) are silent. Physical pixels would make every 150% display "big".
5. *Edges.* Leaving a toolbar or tab strip off its long-axis end is a Collide: "end of the line".
6. *No app-specific hover thinning by default.* Earlier revisions thinned busy apps after N landings.
   Current defaults disable this in every style so a confirmed dwell behaves consistently across applications;
   global rate limits and same-target cooldown still protect against noise.
7. *Skip inline links* (Hyperlink inside Document/Text/Edit) in Quiet/Everything. Focused excludes all hyperlinks after
   the user found the initial link-hover experiment too noisy.

**Styles replaced profiles.** Quiet (structure only), Focused (default), Everything (more control roles and looser
rate caps). All share the same dwell rule; Everything no longer means every fly-by.

**Touchpad boost.** The haptics API has no x/y, so a touchpad pulse is the whole pad, and it feels lighter than the
mouse at the same intensity. We multiply intensity by 1.3 for touchpad controllers (tray option).

**The lab becomes the Showcase.** With EXP-010/020 physically passed, the evidence machinery stops being the front
door. `haptics-lab` opens a Showcase: feel each sensation on demand, try it on a demo surface (menu, toolbar, ribbon,
tabs, choices, a WPF slider that exposes RangeValue, and a row of things that should stay silent), and read the
*reason* behind every tick or skip. The experiments live on a Lab tab, and nobody needs to run them to use the app.
EXP-090 (blind style comparison) is the first experiment about *taste* rather than capability.

**The Showcase gets the tray's Fluent shell (2026-10).** `ShowcaseWindow` uses the same WPF-UI 4.3.0 theme,
rounded cards, typography and shared icon as the tray; light/dark changes follow Windows. The demo now uses real
WPF menus, toolbars, groups, tabs, options and a RangeValue slider, rather than painted stand-ins. The silent row
retains disabled/tiny targets, a password control and a deliberately non-interactive painted surface. Inspector
and Lab tables remain embedded WinForms surfaces, and experiment/fixture windows and CLI runners are unchanged.
The WinForms message loop hosts the WPF shell, retaining real-pointer priming on the same UI thread and the
existing per-device controller bank. No routing or policy changes were needed. A running tray still pauses the
Showcase engine. During blind comparisons, settings, live decisions and Inspector are hidden; Stop and Engine
off remain available. Closing the window stops timers and unfinished trials without inventing results.
Expected filesystem and native startup failures are reported at the operation boundary, not swallowed by a
blanket dispatcher handler. Failed engine startup deauthorizes and disposes its uncommitted pipeline, turns the
switch off, and reports the error. Comparison artifacts are created before changing the style or hiding controls.
Unexpected programming errors still propagate.

## 13. Focused becomes selective (2026-10-28; revised 2026-11-02)

Current rule: Quiet is a subset of Focused, so radio/group/strip cues cannot return in Quiet when Focused suppresses
them. Quiet keeps eligible tab detents and slider steps/limits. Focused also includes hyperlink and slider hover
after 50 ms; slider drag cancels pending hover but preserves detents/limits. Tray and Showcase use this shared policy.

Latest evaluation removes Close, ordinary toolbar commands, recognized browser toolbar commands and leaf open-menu
choices from Focused. Tabs, pickers/options, menu launchers, click-confirmed toggles and slider steps/limits remain.
The removed command roles stay available in Everything. Close is off in the local apps, awaiting a replacement.
Universal 50 ms dwell replaces speed-dependent landing in all styles and fixes same-identity re-entry after an
unsampled pass-through. Per-app density thinning is disabled by default. The following records earlier iterations.

The user first paused taskbar and Close hover to prioritize performance, then restored only the lightweight
original Close rule: UIA Button directly under TitleBar with right edges within 2 logical pixels.
Local Close hover is now On; taskbar hover remains Off. The live pipeline never enables native caption probing,
even when Close hover is On. Unsupported custom caption layouts stay silent instead of using extra native queries.
The earlier native implementation remains available to explicit worker diagnostics and authored replays.
The subsequent command-versus-content filter uses the existing one-parent metadata and defaults to silence.
Tabs require a Tab parent; toolbar buttons/split buttons require ToolBar; menu items require a menu parent.
Explicit ComboBox pickers/options and the verified browser commands remain eligible. Expand/collapse capability
alone no longer promotes content cards or ambiguous buttons to navigation; menu launchers require toolbar/menu
context. This deliberately trades some custom-navigation coverage for less noise, without Teams-specific rules,
extra tree scans or content reads. Semantic evidence records the qualifying rule. Authored people-card regressions
do not establish the actual Teams participant provider shape; live coverage remains to be confirmed.

User-directed local tuning: Focused keeps deliberate landings on window Close (not minimize/maximize), taskbar app
icons, toolbar navigation/commands, unselected tabs, and dropdown/menu targets. Group entries and inferred strip exits
are silent in this style. Slider detents and endpoint limits keep their existing quantization (10 in the demo).
Checkboxes and toggle buttons no longer land in Focused: a mouse-associated, observed change in either direction
(Off-to-On or On-to-Off) emits one Step. Unknown/indeterminate state, keyboard/programmatic changes without a mouse
click, and failed clicks are silent. Per the November 2 revision, toolbar commands, tabs and dropdown/menu targets
always use Hover, even when large. Tabs now share landing cooldown and density rules instead of detent rules.
The later tuning removes browser density throttling and reduces the default fast-crossing dwell from 80 to 50 ms.
The same-target cooldown and global safety caps remain; deliberate browser tab sweeps no longer slow down after six lands.
Quiet/Everything keep the original vocabulary and rules; Focused taskbar buttons retain the DPI-aware large-target
Click rule. Close retains main's single light Hover and its default-on Close hover setting, never a large-target Click.
Native caption classification takes precedence over geometric fallback.

This requires read-only toggle state and dropdown-capability metadata, approved by the user. We use property
identifiers, not action-pattern objects; the forbidden-type scanner still bans TogglePattern and ExpandCollapsePattern.
The hovered toggle is refreshed to keep a recent pre-click state, with bounded faster observation after release.
Refreshes preserve the originating input route and interaction generation without advancing raw-input freshness.
Device/input-class changes and explicit invalidation cancel click confirmation.
A click can correlate with a state change, not prove its cause; this is never evidence of application success.
Caption identification uses bounded WM_NCHITTEST / WM_GETTITLEBARINFOEX queries in the UIA worker, using the reported
button kind or Close rectangle rather than assuming Close is the rightmost button. Unknown custom chrome/navigation layouts
remain silent rather than using labels, screenshots or broad content-row heuristics.

The initial hyperlink-hover refinement was too noisy in use; the user chose to turn all hyperlink hover off.
The extra text/image-to-link parent normalization was removed too. Live Chrome metadata exposed Downloads as `PinnedActionToolbarButton`, a toggle
under a Pane, while other toolbar commands exposed dropdown capability. An exact native-host/role/class allowlist treats
these recognized browser commands as Hover-only navigation, not preference toggles. Taskbar preview probes returned
empty shell hosts or underlying app elements; the user deferred preview support rather than guessing from popup geometry.

Earlier Close probing showed why UIA-role gating is insufficient: Chrome can supply native caption hit codes without any
accessible caption buttons. The retained native path checks caption identity independently of UIA type and retains native
availability plus observed sensitive/disabled flags. When no usable rectangle is reported, the window/button identity
remains stable and dwell requires fresh confirmation at the current point rather than an invented hit box.
Later probes found Explorer returning HTCLIENT over valid caption rectangles, and Copilot's separate
`GitHubCopilotCaptionHost` returning HTTRANSPARENT while its same-process Tauri owner reports the real button kind.
A loose DWM caption region enables the reported-rectangle fallback without messaging every hovered window.
Only the observed helper/owner class pair, with matching PID, can use owner resolution. The DWM region never identifies
an individual button. Taskbar discovery now filters interactive roles with a geometry-only cache, then refreshes the
chosen target's safety/identity metadata rather than acquiring every property and pattern for every descendant.
Warm taskbar lookups still cost several milliseconds, so per-pixel reads can occupy the worker continuously.
Same-icon Focused queries now coalesce to one per 16 ms with a latest-request timer, without delaying arrival on
another icon or changing raw-input timestamps, foreground/device generations, or hover filtering.

**Things we chose not to do:** per-pixel "texture" while moving (the device can't localize it, and it reads as
vibration); Grow on drag (we can't tell a drag from a selection reliably without reading content); scroll texture
(Windows already does wheel detents on the mouse).

## 13. v0.6: seeing the haptics (2026-11)

Most people who see a demo don't have a haptic mouse in their hand. The Showcase header's **Visual demo** switch
runs the engine Observe-only (dry-run, no output route, no output lease) and draws a ripple at the pointer wherever
policy would have played a cue, labelled with the cue's name (Land, Land big, Detent, Edge). The **Stage** tab shows
the same cues full-size. The ripple is a transparent, topmost WPF window with `WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW
| WS_EX_NOACTIVATE`, so it never takes a click, focus or a taskbar slot and keeps the "we never steer anything"
rule. The tray gets the same overlay as an opt-in **Show ripples** setting (off by default) that draws alongside
real haptics, for screen recordings. Only decisions policy actually emits draw a ripple. A ripple shows intent,
never that a pulse was felt, so neither mode produces evidence.
