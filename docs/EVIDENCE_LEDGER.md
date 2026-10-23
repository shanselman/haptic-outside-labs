# Evidence ledger

Accumulated, attributable results (SPEC §13). Each entry names the run, the code commit, the machine fingerprint,
and what it does **and does not** prove. Entries are appended, never rewritten. Generate entries with
`haptics-lab evidence summarize artifacts\runs\<run-id>`; hypothesis promotions are human decisions recorded in
[`HYPOTHESES.yaml`](HYPOTHESES.yaml).

## Current position (2026-10-23)

| Question | Answer so far | Class |
|---|---|---|
| Is the haptics API present and guardable on the bench machine? | Yes | `BUILD` |
| Does anything we send make a mouse buzz in the foreground? | **Yes** — Surface Mouse 2nd Edition, Click and Step, 12/12 felt vs 0/6 controls (EXP-010) | `PHYSICAL` |
| …while another app is foreground (the go/no-go)? | **Yes, via R3a/R3b** (captured controller, 6/6 felt vs 0/6 each). **No via R2** (background raw-input thread never gets a controller) | `PHYSICAL` |
| Can UIA identify controls under a point quickly, in an isolated worker? | Yes, on our WinForms fixture: 23/23, p95 ≈ 4.4 ms | `OBSERVED` |
| Does a hung provider freeze us? | No: cut off at ≈ 237 ms, worker restarted, no late delivery | `OBSERVED` |
| Do the semantic rules make the intended decisions? | Yes, 13/13 golden traces | `OBSERVED` (logic only) |
| Do shipped assemblies avoid input-synthesis/focus/hook/injection APIs? | Yes (5 assemblies scanned) | `OBSERVED` |

**Gates A1 and A2 are physically confirmed on this machine for the Surface Mouse: background haptics work via a controller captured from our own foreground input (R3a/R3b). The plain background raw-input route (R2) does not.** Next: tray dogfood, EXP-030, live semantics in real apps (EXP-070 physical), EXP-075, EXP-080, EXP-082.

## Bench machine

`f10947f6a858b89f` — Surface Laptop 15" 8th Edition (Snapdragon), Windows 26H1 build 28000.3231, ARM64 process,
.NET 10. Haptic touchpad present. Raw-input mice at the time of the final runs: the built-in Surface HID touchpad
mouse collection and a USB mouse (VID_3434&PID_D030). No Surface Mouse was paired for the automated runs; for the physical runs a Surface Mouse 2nd Edition was paired over Bluetooth LE (VID_045E&PID_0880).

## Entries

### 2026-10-23 · EXP-020 · `PASS` [PHYSICAL] — background output works via a captured controller

- Run `20261023-200151-EXP-020-4b9071`, commit `2c36831`, machine `f10947f6a858b89f`
- Device: Surface Mouse 2nd Edition (BLE, `VID_045E&PID_0880`), locked from priming (233 prime events, all from
  this device). Captured controller: type `Mouse`, intensity supported. Waveform Click, intensity 1.0, message-only
  raw-input window. Foreground: another app (Notepad). Raw events: 7,927 background vs 235 foreground (priming).
- Blinded, seeded: 6 pulses per route × 3 routes + 6 silent controls; 24/24 answered; **0 invalid trials**
  (all `RIM_INPUTSINK`, lab never foreground, pointer never over the lab).

| Route | Felt | Controls felt | Fisher p | API | Verdict |
|---|---|---|---|---|---|
| **R3a** cached `SimpleHapticsController`, used from the raw-input thread | **6/6** | 0/6 | 0.0011 | 6/6 accepted | **PASS** |
| **R3b** `SetOverrideHapticsController` on the raw-input thread's manager | **6/6** | 0/6 | 0.0011 | 6/6 accepted | **PASS** |
| **R2** `GetForCurrentThread()` on the raw-input thread | 0/6 | 0/6 | 1.0 | 0/6 accepted | **FAIL** |

- API-level finding: the raw-input thread's manager had **no controller in 0/288** background samples (device type
  `None` throughout). Background `WM_INPUT` via `RIDEV_INPUTSINK` does **not** count as "recent input" for
  `InputHapticsManager` routing on this build.
- Input→call p50 0.23 ms, p95 2.8 ms (software, sent inside the `WM_INPUT` handler).
- R2's FAIL is a true FAIL: EXP-010 passed for Click at intensity 1.0 on this machine.
- Promotion: **H1 → `FALSIFIED_ON[f10947f6a858b89f]`**; **H2 → `CONFIRMED_ON[f10947f6a858b89f]`** (R3a and R3b).
  Gate A2 confirmed → tray **On** unlocks here with R3a (needs a one-time "Prime haptic mouse").
- Limitations: single session, one device and OS build. Controller lifetime across idle, sleep, re-pair, and
  device switching is untested (EXP-082). Answers were given by clicking the non-activating panel with the same mouse.

### 2026-10-23 · EXP-010 · `PASS` [PHYSICAL] — first felt result

- Run `20261023-195826-EXP-010-6c99e9`, commit `2c36831`, machine `f10947f6a858b89f`
- Device: **Surface Mouse 2nd Edition** (Bluetooth LE, VID 045E PID 0880), target `mouse`; every trial's UI-thread
  haptic device type was `Mouse`. Intensity 1.0; route R1 (documented `GetForCurrentThread()` on our focused window).
- Blinded, seeded schedule (seed 1298865452): 6 Click + 6 Step pulses + 6 silent controls, all 18 answered, 0 invalid.
  - `R1-foreground-manager:Click`: felt **6/6**, controls 0/6, one-sided Fisher p = 0.0011 → PASS
  - `R1-foreground-manager:Step`: felt **6/6**, controls 0/6, p = 0.0011 → PASS
- API accepted 12/12 sends. Input→call p50 3.9 ms; call duration p95 0.16 ms (software timestamps, not physical onset).
- Limitation: the operator did not tick "felt native haptics first"; irrelevant here since the pulses were felt.
- Promotion: Gate A1 → **confirmed on `f10947f6a858b89f`** for the Surface Mouse. This also makes a "not felt" in
  EXP-020 (Click or Step, intensity ≤ 1.0) a genuine FAIL rather than INDETERMINATE.

### 2026-10-23 · EXP-000 · `PASS` [BUILD]

- Run `20261023-040512-EXP-000-d08397`, commit `d9c88c3`, machine `f10947f6a858b89f`
- `InputHapticsManager` type present, Universal API Contract v19 present, `IsSupported()=True`,
  `IsHapticDevicePresent()=True`; all 10 discrete named waveforms resolve; all probed methods present.
- Console thread manager: OK, `CurrentHapticsController = null`, device type `None` (no input on that thread).
- Labels: `NOT_TESTED_ON_HARDWARE`. Does **not** show that the target mouse can be driven.
- Promotion: none (H1 stays `UNVERIFIED`).

### 2026-10-23 · EXP-023 · `PASS` [OBSERVED]

- Run `20261023-040512-EXP-023-9cf0da`, commit `d9c88c3`, machine `f10947f6a858b89f`
- Own-process only, no waveform sent:
  - `TryGetForThread(A)` from thread B → object with `ThreadId = A`.
  - Reading `CurrentHapticsControllerDeviceType` / calling `TryStopFeedback()` on A's manager from B →
    `COMException 0x8001010E RPC_E_WRONG_THREAD`.
  - `TryGetForThread` for a thread without a manager, for 0, and for a bogus id → `null` (no throw).
- Meaning: the documented thread-ownership rule is enforced at runtime; routes must be executed on the thread that
  owns the manager (which is how EXP-020 is built).
- Promotion: informs H2 design; no state change.

### 2026-10-23 · EXP-040 · `PASS` [OBSERVED]

- Run `20261023-040514-EXP-040-c79e41`, commit `d9c88c3`, machine `f10947f6a858b89f`
- Isolated UIA worker (separate process, MTA) hit-tested the centre of 23 fixture controls (buttons, disabled
  button, checkbox, radios, toolbar buttons incl. disabled, menu items, tab items, list items, slider, password edit,
  custom-painted canvas): **23/23 correct** on type, PID, AutomationId, bounds, enabled flag; password flagged;
  painted "buttons" correctly reported as a non-interactive `Pane`.
- UIA lookup latency: p50 3.5 ms, p95 4.4 ms, max 4.8 ms (earlier runs: p95 6.0 ms) (lightweight WinForms fixture).
- Group context: toolbar buttons → parent `ToolBar`, menu items → `MenuBar`, tabs → `Tab`, list items → `List`.
- Additional observation: the WinForms `TrackBar` is exposed as `Slider` with **ValuePattern only, no
  RangeValuePattern**. Because the lab never reads ValuePattern strings, slider detent rules correctly abstain on
  WinForms trackbars; they need providers that expose RangeValue (e.g. WPF, Chromium `<input type=range>` — untested).
- Promotion: H3 → `CONFIRMED_ON[f10947f6a858b89f]` **for the fixture only**; H4 (real apps) still `UNVERIFIED`.
- Dev-iteration note: earlier development builds reported 0/23 and then `INDETERMINATE` because a maximized window
  covered the fixture; the harness now verifies the fixture is the top window at each probe point and launches it so
  it can come to the front. Those dev runs were discarded. All entries here were re-run on commit `d9c88c3` after the code-review fixes and the UIA ready-handshake fix
  (earlier runs with the same outcomes on `7f1273d` and `1050c15` were superseded).

### 2026-10-23 · EXP-043 · `PASS` [OBSERVED]

- Run `20261023-040512-EXP-043-7e6bcb`, commit `d9c88c3`, machine `f10947f6a858b89f`
- Fault-injected worker (lab-only opt-in): `hang` cut off after 237 ms (timeout 200 ms) and the worker restarted with a
  new PID; `slow` (600 ms) discarded, not delivered late; `crash` reported as failure; every subsequent `ping`
  succeeded. Worker restarts: 3.
- Promotion: supports H3's containment requirement.

### 2026-10-23 · EXP-070 · `PASS` [OBSERVED] — replay only

- Run `20261023-040512-EXP-070-1333c3`, commit `d9c88c3`, machine `f10947f6a858b89f`
- 13/13 golden traces produced the expected haptic decisions (see `tests/traces/README.md`).
- Labels: `NOT_TESTED_ON_HARDWARE`. Proves rules, not timing or feel.

### 2026-10-23 · EXP-081 · `PASS` [OBSERVED]

- Run `20261023-040512-EXP-081-bccf0a`, commit `d9c88c3`, machine `f10947f6a858b89f`
- Metadata scan of `Haptics.Core`, `Haptics.Windows`, `haptics-lab`, and `haptics-tray` (Debug + Release) found no
  forbidden P/Invokes (SendInput, SetCursorPos, SetForegroundWindow, SetFocus, hooks, CreateRemoteThread,
  WriteProcessMemory, SystemParametersInfo, …) and no UIA action/value/text pattern references.

### 2026-10-23 · Ad-hoc observation (not a numbered experiment) · [OBSERVED]

- Commit `d9c88c3`, machine `f10947f6a858b89f`. In the dashboard's waveform playground, a click delivered by UI
  automation (posted, not device input) produced `TrySendHapticWaveform(Click) = false` with device `None` and no
  controller on the UI thread. Haptic routing depends on real device input; synthetic/posted input cannot stand in
  for physical tests (relevant to H6).

## Pending physical entries

| Experiment | Who | Status |
|---|---|---|
| EXP-010 foreground baseline | operator + Surface Mouse | `PASS` [PHYSICAL] 2026-10-23 |
| EXP-020 background routes R2 / R3a / R3b | operator + Surface Mouse | `PASS` [PHYSICAL] 2026-10-23 (R3a, R3b pass; R2 fails) |
| EXP-030 raw input attribution | operator | `NOT_RUN` |
