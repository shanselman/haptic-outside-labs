# Research notes: verified API surface and platform observations

Labels follow SPEC §2: `[DOC]` documented, `[BUILD]` verified at build/activation, `[OBSERVED]` measured at runtime,
`[PHYSICAL]` felt, `[UNVERIFIED]` not yet tested. Sources are the SPEC §20 references (S01–S32).

## Toolchain

- `[BUILD]` .NET SDK 10.0.402 rejects `net10.0-windows10.0.28000.0` (NETSDK1140: unknown Windows SDK version).
- `[BUILD]` Workaround used repo-wide (`Directory.Build.props`): target `net10.0-windows10.0.26100.0` and set
  `WindowsSdkPackageVersion=10.0.28000.87`. NuGet `Microsoft.Windows.SDK.NET.Ref` 10.0.28000.87 contains
  `Windows.Devices.Haptics.InputHapticsManager`. Only the 26100 native Windows SDK was installed; it is not needed
  for the C# projection.
- `[BUILD]` Builds are AnyCPU; verified native ARM64 on a Snapdragon Surface Laptop 8. x64 is exercised by CI.

## `Windows.Devices.Haptics` as projected by 10.0.28000.87 `[BUILD]`

Reflected from the reference assembly (not copied from docs):

```text
InputHapticsManager
  static bool IsSupported()
  static bool IsHapticDevicePresent()
  static InputHapticsManager GetForCurrentThread()
  static InputHapticsManager TryGetForThread(uint threadId)
  bool TrySendHapticWaveform(ushort waveform, ushort fallback)
  bool TrySendHapticWaveform(ushort waveform, ushort fallback, double intensity)
  bool TrySendHapticWaveformForDuration(ushort, ushort, double, TimeSpan)
  bool TrySendHapticWaveformForPlayCount(ushort, ushort, double, int, TimeSpan)
  bool TryStopFeedback()
  HapticsControllerOverrideToken SetOverrideHapticsController(HapticDeviceType, SimpleHapticsController)
  void ClearOverrideHapticsController(HapticsControllerOverrideToken)
  SimpleHapticsController CurrentHapticsController { get; }
  HapticDeviceType CurrentHapticsControllerDeviceType { get; }
  uint ThreadId { get; }
HapticDeviceType: None=0, Generic=1, Pen=2, Touchpad=3, Mouse=4
SimpleHapticsController: Id, SupportedFeedback, IsIntensitySupported, IsPlayCountSupported,
  IsPlayDurationSupported, IsReplayPauseIntervalSupported, SendHapticFeedback(fb[, intensity]),
  SendHapticFeedbackForDuration, SendHapticFeedbackForPlayCount, StopFeedback
```

## Runtime observations on Surface Laptop 8 / Windows 26H1 build 28000.3231 / ARM64

| Observation | Label | Source |
|---|---|---|
| `ApiInformation.IsTypePresent("…InputHapticsManager")` = true; `UniversalApiContract` v19 present | `[OBSERVED]` | EXP-000 |
| `IsSupported()` = true, `IsHapticDevicePresent()` = true (the machine has a haptic touchpad; EXP-000 saw one raw-input mouse device and no Microsoft-VID mouse, i.e. no Surface Mouse paired at the time) | `[OBSERVED]` | EXP-000 |
| Named discrete waveforms Click, Hover, Press, Release, Success, Error, Align, Collide, Step, Grow all resolve (plus continuous Brush/Buzz/Chisel/Eraser/GalaxyPen/Ink/Marker/Pencil/Rumble) | `[OBSERVED]` | EXP-000, PowerShell probe |
| `GetForCurrentThread()` from a console thread with no input: manager OK, `CurrentHapticsController` = null, device type `None` | `[OBSERVED]` | EXP-000 |
| `TryGetForThread(A)` from thread B returns a manager object whose `ThreadId` = A | `[OBSERVED]` | EXP-023 |
| Reading `CurrentHapticsControllerDeviceType` or calling `TryStopFeedback()` on thread A's manager from thread B throws `RPC_E_WRONG_THREAD (0x8001010E)` | `[OBSERVED]` | EXP-023 |
| `TryGetForThread` for a thread with no manager, thread 0, or a bogus id returns null (no throw) | `[OBSERVED]` | EXP-023 |
| UI thread receiving only posted (non-device) clicks: device `None`, controller absent, `TrySendHapticWaveform` returns false | `[OBSERVED]` | Lab waveform playground via UI automation |
| A WinForms window created by a background process via bare `CreateProcess` did not become topmost; `SetWindowPos(HWND_TOPMOST)` returned TRUE without setting `WS_EX_TOPMOST` | `[OBSERVED]` | EXP-040 debugging (fixture now launched via ShellExecute) |
| UIA `ElementFromPoint` + `CacheRequest` in a worker process: 23/23 WinForms fixture controls correct; p50 3.5–4.9 ms, p95 4.4–6.0 ms across three runs | `[OBSERVED]` | EXP-040 |
| WinForms `TrackBar` (msctls_trackbar32 via proxy) is a UIA `Slider` supporting only `ValuePattern`; `IsRangeValuePatternAvailable` = false | `[OBSERVED]` | EXP-040 + live probe |

## Answered by physical runs (2026-10-23, Surface Mouse 2nd Edition)

- `[PHYSICAL]` Foreground `GetForCurrentThread().TrySendHapticWaveform` buzzes the Surface Mouse (EXP-010, 12/12 vs 0/6).
- `[OBSERVED]` Background `WM_INPUT` (`RIM_INPUTSINK`) does **not** give the receiving thread a `CurrentHapticsController`:
  0/288 samples, device type `None`, `TrySendHapticWaveform` returned false 6/6 (EXP-020 R2).
- `[PHYSICAL]` A `SimpleHapticsController` captured on the UI thread during our own foreground input can be used from
  the raw-input thread while another app is foreground, both directly (R3a) and via `SetOverrideHapticsController` on
  that thread's manager (R3b). Each felt 6/6 vs 0/6 controls, with no exceptions.

## Still unknown

- `[UNVERIFIED]` How long a captured controller stays valid (idle, sleep/resume, Bluetooth re-pair, device switch) — EXP-082.
- `[UNVERIFIED]` Whether the haptic touchpad behaves the same way (rerun EXP-010/020 with `--target touchpad`).
- `[UNVERIFIED]` Whether `GetForCurrentThread()` routing differs on other OS builds or architectures.

## Experimental acquisition without window input (2026-10-27)

`[OBSERVED]` On the development machine, `Windows.Internal.Devices.Input.HapticDevice` is registered to
`InputHost.dll` 10.0.28000.3231. Its factory's `IInspectable.GetIids` advertises
`IHapticDeviceStatics` IID `0e929425-9118-5f26-a73d-73b879f9de3d`.
The matching [Microsoft public symbols](https://msdl.microsoft.com/download/symbols/InputHost.pdb/27E0B45D1E00EDD7C6881F3E7B7B43BA1/InputHost.pdb)
identify the interface's sole additional method:
`GetControllerForPointerDevice(unsigned __int64, ISimpleHapticsController**)`, after the six IInspectable slots.

A fresh native ARM64 console process, with no window, input registration, or synthetic input, obtained a
controller for one of two enumerated raw mouse handles. Its eight discrete waveforms and intensity support
were readable. The public thread manager had no controller before or after lookup. Null-handle, touchscreen,
pen, and the other raw mouse handle controls returned S_OK with null controllers. No waveforms were sent.
This is an ad-hoc acquisition observation, **not PHYSICAL evidence**, and does not update the evidence ledger.
The separate documented `VibrationDevice.FindAllAsync` probe failed with `0x80070005` (access denied); that
does not prove no devices exist, and no permissions or privileges were changed to get past it.

An additional production-code probe acquired one controller on a background thread, left the public thread
manager empty, and verified that the bank's manual fallback stayed null. The self-contained ARM64 tray then
started with no priming window. In an **unblinded operator smoke check**, the operator reported ticks from the
Surface Mouse over other apps without opening Settings. This is encouraging usability feedback, not a blinded
PHYSICAL result; the touchpad and other Windows builds have not been established by this check.

A subsequent off/on check stopped every tray and Showcase process: the operator reported the added ticks
disappeared. A new self-contained tray process started with no priming window, and the operator reported the
ticks returned over the same controls without opening Settings. This remains an unblinded functional check;
cold-boot and reconnect behavior are not established.

**Lifecycle follow-up:** an unblinded Windows Sleep/wake check passed in the same automatically primed tray
process without Settings interaction. A BLE power-cycle initially failed: the mouse's hashed interface path
stayed the same but its raw handle changed, and the startup-only map was not refreshed. Manual priming restored
ticks without a restart. After adding device-arrival rediscovery, a repeat power-cycle changed the handle again
and the operator reported automatic recovery in the same process. These are first-case functional observations;
see the [test plan's session record](AUTOMATIC_PRIMING_TEST_PLAN.md#operator-session-2026-10-27).

The tray's `ControllerDiscovery` now tries this interface on a background task, gated by the public API's
presence and the exact private IID. Missing classes/interfaces and platform errors produce a diagnostic in
Settings > Details, not success. The normal priming window remains available and opens after a failed lookup
or a two-second wait; a late successful lookup can still be used. Nothing alters pointer input or other apps.
The existing policy and output path are unchanged.

The private interface returns no device type. Automatic controllers therefore have type Generic and are
mapped only to the exact nonzero raw device handle, never the fallback for unknown/handleless input.
Normal priming can later establish the device type and replace the entry. Lookup runs at startup and after raw
mouse device-arrival notifications. Removal invalidates the old mapping; arrivals coalesce into one in-flight
lookup, and results are discarded if the device set changed while they were pending. Automatic recovery does
not reactivate an existing settings window, and a newly shown fallback window does not activate itself.
Touchpads, unavailable devices, and output failures without a device notification retain manual priming.
Availability across builds/architectures, reconnect behavior, and physical feedback through this acquisition
path still need operator validation. Do not treat this private interface as a supported Windows contract.

**Prototype decision:** keep lookup enabled on `main`, retain manual priming, and collect the missing
lifecycle/architecture evidence rather than add another opt-in switch. No release accompanies this decision.
[AUTOMATIC_PRIMING_TEST_PLAN.md](AUTOMATIC_PRIMING_TEST_PLAN.md) records the pending cold-boot, sleep/resume,
reconnect, x64 and unhealthy-platform checks, plus TODOs for recovering when an acquired controller later fails
without a device notification. The two-second wait is still not native-call cancellation or process isolation.

## Design-relevant documentation facts (from SPEC §2.1, rechecked)

- `[DOC]` Raw Input with `RIDEV_INPUTSINK` delivers background input to a registered HWND; only one window per
  device class per process receives raw input. The lab therefore never runs two raw-input consumers in one process
  (the dashboard's Live Observer and experiments run in separate processes).
- `[DOC]` Applications must call `DefWindowProc` after processing `WM_INPUT` so the system can clean up.
- `[DOC]` UIA clients that inspect the whole desktop should use a windowless MTA thread; providers may not raise all events.
- `[DOC]` Microsoft's haptics guidance: signal over noise, < 50 ms cause-to-feedback, tie feedback to visible state.
