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
| UIA `ElementFromPoint` + `CacheRequest` in a worker process: 23/23 WinForms fixture controls correct; p50 ≈ 3.8 ms, p95 ≈ 6 ms | `[OBSERVED]` | EXP-040 |
| WinForms `TrackBar` (msctls_trackbar32 via proxy) is a UIA `Slider` supporting only `ValuePattern`; `IsRangeValuePatternAvailable` = false | `[OBSERVED]` | EXP-040 + live probe |

## Still unknown

- `[UNVERIFIED]` Whether background `WM_INPUT` (RIM_INPUTSINK) gives the receiving thread a `CurrentHapticsController`
  for the Surface Mouse — EXP-020 measures this directly (`rawThreadBackgroundSamplesWithController`) as well as
  physically.
- `[UNVERIFIED]` Whether a `SimpleHapticsController` captured on one thread can be used from another
  (`MarshalingBehavior` of the controller class is not asserted here; EXP-020 R3a records any exception).
- `[UNVERIFIED]` Any physical sensation at all from this code. No `PHYSICAL` evidence exists yet.

## Design-relevant documentation facts (from SPEC §2.1, rechecked)

- `[DOC]` Raw Input with `RIDEV_INPUTSINK` delivers background input to a registered HWND; only one window per
  device class per process receives raw input. The lab therefore never runs two raw-input consumers in one process
  (the dashboard's Live Observer and experiments run in separate processes).
- `[DOC]` Applications must call `DefWindowProc` after processing `WM_INPUT` so the system can clean up.
- `[DOC]` UIA clients that inspect the whole desktop should use a windowless MTA thread; providers may not raise all events.
- `[DOC]` Microsoft's haptics guidance: signal over noise, < 50 ms cause-to-feedback, tie feedback to visible state.
