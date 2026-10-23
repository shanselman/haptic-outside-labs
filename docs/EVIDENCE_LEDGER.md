# Evidence ledger

Accumulated, attributable results (SPEC §13). Each entry names the run, the code commit, the machine fingerprint,
and what it does **and does not** prove. Entries are appended, never rewritten. Generate entries with
`haptics-lab evidence summarize artifacts\runs\<run-id>`; hypothesis promotions are human decisions recorded in
[`HYPOTHESES.yaml`](HYPOTHESES.yaml).

## Current position (2026-10-23)

| Question | Answer so far | Class |
|---|---|---|
| Is the haptics API present and guardable on the bench machine? | Yes | `BUILD` |
| Does anything we send make a mouse buzz in the foreground? | **Not tested** (no Surface Mouse at the bench yet) | — |
| …while another app is foreground (the go/no-go)? | **Not tested** | — |
| Can UIA identify controls under a point quickly, in an isolated worker? | Yes, on our WinForms fixture: 23/23, p95 ≈ 6 ms | `OBSERVED` |
| Does a hung provider freeze us? | No: cut off at ≈ 235 ms, worker restarted, no late delivery | `OBSERVED` |
| Do the semantic rules make the intended decisions? | Yes, 13/13 golden traces | `OBSERVED` (logic only) |
| Do shipped assemblies avoid input-synthesis/focus/hook/injection APIs? | Yes (5 assemblies scanned) | `OBSERVED` |

**No `PHYSICAL` evidence exists yet. Haptic output is UNVERIFIED.** Next: [`TESTING_WITH_SURFACE_MOUSE.md`](TESTING_WITH_SURFACE_MOUSE.md).

## Bench machine

`f10947f6a858b89f` — Surface Laptop 15" 8th Edition (Snapdragon), Windows 26H1 build 28000.3231, ARM64 process,
.NET 10. Haptic touchpad present. One raw-input mouse device at the time of the runs; no Microsoft-VID mouse (no
Surface Mouse paired).

## Entries

### 2026-10-23 · EXP-000 · `PASS` [BUILD]

- Run `20261023-040106-EXP-000-74e670`, commit `1050c15`, machine `f10947f6a858b89f`
- `InputHapticsManager` type present, Universal API Contract v19 present, `IsSupported()=True`,
  `IsHapticDevicePresent()=True`; all 10 discrete named waveforms resolve; all probed methods present.
- Console thread manager: OK, `CurrentHapticsController = null`, device type `None` (no input on that thread).
- Labels: `NOT_TESTED_ON_HARDWARE`. Does **not** show that the target mouse can be driven.
- Promotion: none (H1 stays `UNVERIFIED`).

### 2026-10-23 · EXP-023 · `PASS` [OBSERVED]

- Run `20261023-040106-EXP-023-91e4d7`, commit `1050c15`, machine `f10947f6a858b89f`
- Own-process only, no waveform sent:
  - `TryGetForThread(A)` from thread B → object with `ThreadId = A`.
  - Reading `CurrentHapticsControllerDeviceType` / calling `TryStopFeedback()` on A's manager from B →
    `COMException 0x8001010E RPC_E_WRONG_THREAD`.
  - `TryGetForThread` for a thread without a manager, for 0, and for a bogus id → `null` (no throw).
- Meaning: the documented thread-ownership rule is enforced at runtime; routes must be executed on the thread that
  owns the manager (which is how EXP-020 is built).
- Promotion: informs H2 design; no state change.

### 2026-10-23 · EXP-040 · `PASS` [OBSERVED]

- Run `20261023-040108-EXP-040-8236ed`, commit `1050c15`, machine `f10947f6a858b89f`
- Isolated UIA worker (separate process, MTA) hit-tested the centre of 23 fixture controls (buttons, disabled
  button, checkbox, radios, toolbar buttons incl. disabled, menu items, tab items, list items, slider, password edit,
  custom-painted canvas): **23/23 correct** on type, PID, AutomationId, bounds, enabled flag; password flagged;
  painted "buttons" correctly reported as a non-interactive `Pane`.
- UIA lookup latency: p50 4.9 ms, p95 6.0 ms, max 6.4 ms (lightweight WinForms fixture).
- Group context: toolbar buttons → parent `ToolBar`, menu items → `MenuBar`, tabs → `Tab`, list items → `List`.
- Additional observation: the WinForms `TrackBar` is exposed as `Slider` with **ValuePattern only, no
  RangeValuePattern**. Because the lab never reads ValuePattern strings, slider detent rules correctly abstain on
  WinForms trackbars; they need providers that expose RangeValue (e.g. WPF, Chromium `<input type=range>` — untested).
- Promotion: H3 → `CONFIRMED_ON[f10947f6a858b89f]` **for the fixture only**; H4 (real apps) still `UNVERIFIED`.
- Dev-iteration note: earlier development builds reported 0/23 and then `INDETERMINATE` because a maximized window
  covered the fixture; the harness now verifies the fixture is the top window at each probe point and launches it so
  it can come to the front. Those dev runs were discarded. All entries here were re-run on commit `1050c15` after the code-review fixes
  (an earlier run with identical outcomes on `7f1273d` was superseded).

### 2026-10-23 · EXP-043 · `PASS` [OBSERVED]

- Run `20261023-040106-EXP-043-0d2e4f`, commit `1050c15`, machine `f10947f6a858b89f`
- Fault-injected worker (lab-only opt-in): `hang` cut off after 234 ms (timeout 200 ms) and the worker restarted with a
  new PID; `slow` (600 ms) discarded, not delivered late; `crash` reported as failure; every subsequent `ping`
  succeeded. Worker restarts: 3.
- Promotion: supports H3's containment requirement.

### 2026-10-23 · EXP-070 · `PASS` [OBSERVED] — replay only

- Run `20261023-040106-EXP-070-77d03d`, commit `1050c15`, machine `f10947f6a858b89f`
- 13/13 golden traces produced the expected haptic decisions (see `tests/traces/README.md`).
- Labels: `NOT_TESTED_ON_HARDWARE`. Proves rules, not timing or feel.

### 2026-10-23 · EXP-081 · `PASS` [OBSERVED]

- Run `20261023-040106-EXP-081-6ef015`, commit `1050c15`, machine `f10947f6a858b89f`
- Metadata scan of `Haptics.Core`, `Haptics.Windows`, `haptics-lab`, and `haptics-tray` (Debug + Release) found no
  forbidden P/Invokes (SendInput, SetCursorPos, SetForegroundWindow, SetFocus, hooks, CreateRemoteThread,
  WriteProcessMemory, SystemParametersInfo, …) and no UIA action/value/text pattern references.

### 2026-10-23 · Ad-hoc observation (not a numbered experiment) · [OBSERVED]

- Commit `1050c15`, machine `f10947f6a858b89f`. In the dashboard's waveform playground, a click delivered by UI
  automation (posted, not device input) produced `TrySendHapticWaveform(Click) = false` with device `None` and no
  controller on the UI thread. Haptic routing depends on real device input; synthetic/posted input cannot stand in
  for physical tests (relevant to H6).

## Pending physical entries

| Experiment | Who | Status |
|---|---|---|
| EXP-010 foreground baseline | operator + Surface Mouse | `NOT_RUN` |
| EXP-020 background routes R2 / R3a / R3b | operator + Surface Mouse | `NOT_RUN` |
| EXP-030 raw input attribution | operator | `NOT_RUN` |
