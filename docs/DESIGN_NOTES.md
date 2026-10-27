# Design notes — the thinking behind this implementation

These notes explain *why* the code looks the way it does: the bets, the trade-offs, and the things that were
deliberately left out. The spec ([`SPEC.md`](SPEC.md)) says what to build; this says how we interpreted it.

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
  `ElementFromPoint` stops at that window.
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
1. *Land, don't pass.* Entering a control while moving faster than the landing speed is a pass, so it's held as a
   pending arrival. It fires if the pointer slows down or dwells (80 ms) on it, and is dropped (`passed-through`) if it
   leaves first. A stopped pointer sends no input, so the interpreter needs a clock (`OnTick`). The landing speed adapts
   to 2.5× the median speed just before your last clicks (clamped to 400–2500 px/s): fast users get a higher bar.
2. *Group first.* Entering a toolbar, ribbon group, menu, or tab strip you haven't been in for 1.5 s is a Step. It
   orients you before any land. Moving within the group refreshes the memory, so working in one toolbar doesn't
   re-detent.
3. *Choices are detents.* Tabs and radio buttons are Step, not Hover: you're choosing between siblings. The one
   that's already selected is silent, since you can't go to where you already are.
4. *Size in logical px.* Big targets (≥56 logical px tall, or ≥48 both ways: Office's large ribbon buttons) land with
   Click; tiny ones (<10 logical px) are silent. Physical pixels would make every 150% display "big".
5. *Edges.* Leaving a toolbar or tab strip off its long-axis end is a Collide: "end of the line".
6. *Thin out in busy apps.* N lands within 2 s from one app (10 by default, 6 for browsers, chat, and IDEs) thins
   its lands to N per 2 s, evenly spaced, for 4 s. Structure (detents, edges) is never thinned. This used to *pause*
   lands for 4 s, making the app "Quiet" exactly when it was too dense, but the taskbar is all lands and no
   structure: sweeping across it ticked, then stopped, and felt broken (2026-10). Thinning never goes silent.
7. *Skip inline links* (Hyperlink inside Document/Text/Edit): a paragraph of links would be a rattle.

**Styles replaced profiles.** Quiet (structure only), Focused (default), Everything (no landing filter, no density
limit, looser rate caps; for demos). One question, "how much do you want?", instead of a matrix of knobs.

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

**Things we chose not to do:** per-pixel "texture" while moving (the device can't localize it, and it reads as
vibration); Grow on drag (we can't tell a drag from a selection reliably without reading content); scroll texture
(Windows already does wheel detents on the mouse).
