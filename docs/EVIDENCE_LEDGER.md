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
| Can UIA identify controls under a point quickly, in an isolated worker? | Yes, on our WinForms fixture: 23/23, p95 ≈ 4.4 ms | `OBSERVED` |
| Does a hung provider freeze us? | No: cut off at ≈ 237 ms, worker restarted, no late delivery | `OBSERVED` |
| Do the semantic rules make the intended decisions? | Yes, 13/13 golden traces | `OBSERVED` (logic only) |
| Do shipped assemblies avoid input-synthesis/focus/hook/injection APIs? | Yes (5 assemblies scanned) | `OBSERVED` |

**No `PHYSICAL` evidence exists yet. Haptic output is UNVERIFIED.** Next: [`TESTING_WITH_SURFACE_MOUSE.md`](TESTING_WITH_SURFACE_MOUSE.md).

## Bench machine

`f10947f6a858b89f` — Surface Laptop 15" 8th Edition (Snapdragon), Windows 26H1 build 28000.3231, ARM64 process,
.NET 10. Haptic touchpad present. Raw-input mice at the time of the final runs: the built-in Surface HID touchpad
mouse collection and a USB mouse (VID_3434&PID_D030). No Microsoft-VID external mouse, i.e. no Surface Mouse paired yet.

## Entries

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
| EXP-010 foreground baseline | operator + Surface Mouse | `NOT_RUN` |
| EXP-020 background routes R2 / R3a / R3b | operator + Surface Mouse | `NOT_RUN` |
| EXP-030 raw input attribution | operator | `NOT_RUN` |
