# Research notes: verified API surface, platform observations and detailed rules

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

## Experimental single-pen support: details

The README has the short version. This is the full behavior and the evidence so far.

The tray has an **Experimental single pen** switch in Settings, off by default. Turn it on and hover or glide
your haptic pen over Settings once; **Ready** lists `Pen` when its controller is captured. Use only one pen:
Windows exposes a pen-input signature here, not the identity of the individual pen. Toggle the option off/on
to select and prime another pen. Turning Haptics Off or removing any input device clears the pen route and
requires priming again. A lost pen controller also stays silent until primed again.

Pen feedback uses the existing interpreter, policy and output queue, but a separate manually primed controller:
pen events cannot select the mouse/touchpad, and touch-promoted events remain silent. Unsupported discrete pen
sensations fall back to the pen's supported Click waveform, recorded as a fallback; mouse/touchpad behavior is
unchanged. This is not exact per-pen routing or a validated multiple-pen mode. The Showcase does not opt into it.
Background delivery and coexistence need operator checks on each setup.

To establish a pen's foreground baseline, quit other Haptics Everywhere instances and run
`haptics-lab run EXP-010 --target pen`. Use only the pen over the dark pad and check the live
`device=Pen` / `controller=present` status before starting the randomized pulse/no-pulse trials.
Wrong-type or missing controllers cannot send, and those trials cannot establish a baseline.
Leave the native-haptics confirmation unchecked unless you have actually felt the pen's native tactile feedback.

One operator run on a Surface Pro 12in 2nd Ed Snapdragon felt 6/6 Click pulses vs 0/6 silent controls
([evidence ledger](EVIDENCE_LEDGER.md)); the exact pen model and stable controller identity were not established.
This tests the current-thread acquisition route, not every possible Windows pen API. A missing controller is not
proof that the pen cannot vibrate. `EXP-030 --target pen` records passive input-source counts.
`EXP-020 --target pen` compares cached-controller and temporary-override background output using one explicitly
primed pen and pen-class signatures, not per-pen identity. Both routes passed one blinded Click test at intensity 1.0;
this does not validate integrated tray feedback or distinguish hover from screen contact.
Using the tray is not a substitute for a blinded background trial.

An unblinded operator check felt pen hover feedback in the tray's own Settings window (last recorded output:
Click at 0.80), but reported skipped cues and no feedback in other apps. Background tray reliability remains
unresolved despite the separate successful background route trial. Do not rely on this experimental mode for
consistent cross-app pen feedback yet. Settings Details exposes input class, output intensity/fallback and routing
notes; API acceptance is not evidence that a pulse was felt.

## Related Slim Pen 2 HID path (upstream report; not validated here)

[Slim Pen Tool](https://github.com/stephenlwaldron/windows-slim-pen-2-tool) is complementary: it sends continuous,
per-stroke drawing textures through direct HID, while this project uses the Windows haptics API for discrete UI cues.
Its [README](https://github.com/stephenlwaldron/windows-slim-pen-2-tool/blob/master/README.md) and
[HID implementation](https://github.com/stephenlwaldron/windows-slim-pen-2-tool/blob/master/HapticEngine.cs) describe:

- Selecting the pen's digitizer HID collection (VID `0x045E`, PID `0x0C0F`, usage page `0x0D`, 7-byte output report);
  the project's author says only that collection supports haptics among the pen's five collections.
- Reading the waveform table from feature report `0x42` (little-endian durations followed by waveform IDs, mapped
  to ordinals starting at 3) and triggering waveforms with output report `0x41`.
- Checking Windows' Bluetooth-link state before reading the waveform table, because a query against a sleeping pen
  can block for seconds; sending writes on a dedicated worker and reopening the same collection to retry a failed write.

The author reports in [Issue #40 — Slim Pen 2 notes from a related project (HID haptics, wake behaviour)](https://github.com/shanselman/haptic-outside-labs/issues/40)
that the first write after idle failed with error `1167`, then succeeded after reopen/retry in about 16 ms on a
Surface Pro 9. This is an upstream observation, not a result reproduced by Haptics Everywhere. It is a useful
lead for investigating pen-controller idle/wake behavior, but the HID handle-reopen behavior does not establish
that the Windows haptics API controller can be recovered the same way.

Our current path already classifies HRESULT `0x8007048F` (`ERROR_DEVICE_NOT_CONNECTED`, Win32 error `1167`) as a
lost controller in [`ControllerHealth`](../src/Haptics.Windows/ControllerHealth.cs), then revokes the pen route in
[`ObservationPipeline`](../src/Haptics.Windows/ObservationPipeline.cs); the pen stays silent until it is primed
again. The upstream retry reopens a HID file handle, not a `SimpleHapticsController`. Reusing that retry pattern
on the Windows API object would be ungrounded. Before adding automatic recovery, test whether fresh Windows API
controller acquisition after real pen input recovers from idle/wake, and whether it preserves the explicit
single-pen selection safely.

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
lifecycle/architecture evidence rather than add another opt-in switch. The work was initially kept on `main`;
the subsequent v0.4.0 tester release includes it without changing the evidence classification or pending checks.
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

## How each style decides: detailed rules

The README has the short version and the styles table. These are the exact rules the shared engine applies in
both the tray and the Showcase.

Focused uses explicit UIA navigation/menu roles; a generic Button does not qualify from dropdown capability,
class name, or geometry. Everything retains broader Button and option hover coverage.

Everything uses the existing dense ceiling of 25 pulses per second and a 40 ms reentry cooldown in both tray and
Showcase. These are ceilings, not continuous feedback; the intensity slider still controls strength.
The separate **More is better** setting is off by default and applies the same dense limits to Quiet/Focused.
Neither bypasses dwell, the selected style, Off, password protections or app exclusions.

Close hover is removed from Focused pending a replacement implementation. It is off in local tray settings and
Showcase; native caption probing stays disabled. Taskbar buttons are available only in Everything, not Focused/Quiet.
Showcase starts Focused at 80% intensity with touchpad boost on and preserves these choices after settings changes.

Examples are inspection targets, not guarantees of accessibility coverage in every app/version.
"Showcase" means the app's playground. Labels below describe examples for the user; the classifier never reads them.

| Event | Trigger | Example to inspect | Waveform |
|---|---|---|---|
| Navigation tabs | Dwell on an unselected TabItem inside Tab | Showcase: hover Insert while Home is selected; or an inactive browser tab | Hover |
| Inline text links | Dwell on Hyperlink directly under Text/Document/Edit; also enabled in Quiet | Showcase: demo hyperlink | Hover |
| Text-sized Group links | Hyperlink at most 24 logical px tall, fully inside a Group at least twice its height; not enabled in Quiet | Captured Chrome assignment-title link | Hover |
| Dropdown pickers | Dwell on ComboBox | Showcase: hover the Compact/Comfortable/Spacious picker before opening it | Hover |
| Menu launchers | Dwell on a menu-bar MenuItem, submenu MenuItem, or SplitButton in a supported context | Showcase: hover File in the menu bar | Hover |
| Ordinary toolbar Buttons | Dwell on a toolbar Button; dropdown capability alone does not make it a Focused menu launcher | Showcase: hover an ordinary toolbar command | Everything: Hover |
| Checkboxes and toggle buttons/switches | Observe Off-to-On or On-to-Off after a primary-button click; no hover pulse | Showcase: click the Bold checkbox or toolbar B on, then off | Step |
| Slider thumb hover | Dwell on the draggable thumb without dragging; 30 ms for a small thumb, 50 ms for a large one | Showcase: rest on the slider's round control before pressing | Hover |
| Slider detents | Cross a quantized interval while dragging | Showcase: drag the slider from 50 through 60 and 70 | Step |
| Slider endpoints | Newly reach minimum or maximum while dragging | Showcase: drag the slider to 0 or 100 | Collide |
| Cooperating-app events | App explicitly requests a supported waveform, subject to policy | An integrated app explicitly reports completion with Success; not inferred from ordinary UI | Requested waveform |

Ordinary toolbar commands (Showcase +/arrows), recognized browser toolbar commands (Downloads/Extensions),
and open menu choices (File > Open/Save) are silent in Focused and remain available in Everything.
Dropdown/submenu launchers are distinct from leaf menu choices; opening them adds no extra click pulse.
Buttons are not promoted to Focused menu launchers from dropdown capability, CSS class names, or geometry. This
conservative rule may leave real application chevrons silent when UIA exposes them only as generic Buttons.
Everything still includes those Buttons.
Links outside the inline/text-sized Group rules and dropdown options are Everything-only. Taskbar icons, selected tabs,
radio buttons and content cards remain silent in Focused.

Quiet is a subset of Focused, not an alternative set of effects: it keeps eligible tab/inline-link Hover and slider drag
Steps/Collide, but adds no radio-option, group-entry or strip-edge pulses. Explicit cooperating-app requests still
use the common policy. These rules are in the shared engine used by both tray and Showcase.
Everything is a superset of Focused's scenarios and uses the same waveforms for shared scenarios. All automatic
landings use Hover regardless of target size, including tab landings in Quiet. Checkbox hover and strip-exit
feedback are disabled in every style. Everything restores toggle-button Hover (including Showcase B/I/U);
checkbox/toggle click-confirmed Step is retained in Focused and Everything.
Explicit Click previews and cooperating-app requests are unchanged.

Focused uses existing role/one-parent evidence: TabItem under Tab; ComboBox; MenuItem under MenuBar; expandable
MenuItem under Menu/MenuItem; or SplitButton in ToolBar/Menu/MenuBar/MenuItem. Generic Buttons are not promoted
based on dropdown capability, CSS class names or geometry. Hyperlinks directly
under Text/Document/Edit qualify in Focused and Quiet.
Focused additionally accepts a Hyperlink directly under Group when its height is at most 24 logical pixels,
its bounds are fully inside the parent bounds, and that parent is at least twice as tall. Both rectangles and
DPI must be available and finite. Missing geometry, ListItem/ToolBar parents and full-parent linked cards do not
qualify for this exception. Quiet does not use this heuristic. Compact button-like links with the same geometry
may still qualify: this is not proof of visual underlining or purpose. Other links and direct ComboBox options
remain Everything-only. No link text or URL is inspected.
Recognized browser toolbar commands are excluded, including panel toggles.
Expand/collapse capability alone cannot make a people/content card eligible. Unknown navigation rails stay silent;
there are no new tree scans or content reads. Actual Teams participant metadata has not been captured.
Direct Text/Image children of tabs and menu items resolve to their owning control using the single parent hop.
The owner supplies identity, bounds and selection state, so moving between its label/icon and padding neither
cancels dwell nor creates another landing. Focused still requires an expandable owner for a menu-label landing;
leaf menu choices and text/images in generic content controls remain silent.

**Size-aware dwell:** all tabs require 50 ms. Other targets require 50 ms if at least 56 logical pixels tall or
at least 48 by 48 logical pixels; smaller targets require 30 ms. Dimensions are DPI-normalized, and sliders use
the thumb rather than track bounds. Missing bounds use 50 ms. Fresh metadata may extend a visit's dwell but
never shorten it; a new visit reclassifies size. Approach speed never bypasses dwell.
Small movement inside the target does not restart the timer. Leaving before completion cancels it;
returning to the same target starts a fresh dwell even if UIA never observed another identity between visits.
If an accepted lookup arrives after dwell elapsed, feedback can be decided immediately using the same containment
and freshness checks. Per-app density thinning is off by default in every style. Global safety limits
(40 ms spacing, 12 pulses/sec in Quiet/Focused and 25 in Everything), selected-target silence and same-target cooldown still apply.
This is not a requirement for a perfectly motionless pixel; UIA sampling can still miss a brief visit.
Late replies for a crossed target cannot consume movement to the final target. Tab-to-tab transitions use the
reported tab bounds and dwell, not the old five-pixel neighbor hold, so resting just inside a tab still lands on it.

Slider quantization is unchanged: the demo's 0-100 slider has detents every 10; other sliders use their exposed
`LargeChange`, or ten equal range divisions when it is unavailable. Keyboard/programmatic slider changes stay silent.
Focused and Everything slider hover requires a directly observed UIA Thumb and size-aware dwell inside its reported bounds,
not the entire slider track. The worker preserves the slider owner's identity and range while retaining the
thumb's bounds and restrictive safety flags using one parent hop. Providers without a distinct Thumb stay silent
on hover; no thumb position is guessed from the value. Leaving the thumb cancels dwell; returning starts a fresh
dwell without resetting range history. Pressing cancels hover, entering during a drag cannot arm it, and release
adds no hover. Drag detents/endpoints continue normally in all styles.
Checkbox/toggle state must be known before the click and observed changed within 150 ms after release; no confirmation is
invented from mouse-down alone.
Checkbox Text/Image children resolve to their direct checkbox owner, preserving identity, state and safety flags
across the box and label without reading a grandparent. Checkbox dwell never arms an arrival in any style.
Everything toggle-button hover cancels on press, preventing a pending hover from competing with click confirmation.

Coverage depends on structural metadata, not visible labels. Chromium toolbar recognition uses an exact native-host,
role and class allowlist, not button names or arbitrary "toolbar" substrings. Downloads opens a panel, not a setting
toggle; it stays silent in Focused rather than gaining click-confirmed Step feedback. Hyperlinks use one Hover
after size-aware dwell: inline links in every style, text-sized Group links in Focused/Everything, other links in
Everything. Direct Text/Image children of Buttons and Hyperlinks resolve to their owner,
retaining identity and restrictive safety flags without a grandparent read. No URLs, text or image content are read.
Live Teams app-rail sampling found toggle Buttons under Group and Image hits inside them. Everything supports both
padding and direct icon/label hits; the classifier does not depend on Teams names, labels or CSS classes.

In Focused, navigation rails must expose eligible tabs or menu launchers; Everything also admits ordinary/toggle
buttons and links. Arbitrary list rows are never guessed to be navigation.
Unsupported or ambiguous controls stay silent. Legacy caption/host-detection helpers remain for diagnostics and
other-style compatibility, not as active Focused effects.
Caption queries are restricted to UIA caption roles or a loose OS-reported caption region; the region itself never
identifies Close. This includes Explorer's reported Close rectangle even when hit testing says client content.
Copilot's observed caption helper resolves only to its same-process Tauri owner, not arbitrary owned windows.
Quiet/Focused reject primary and secondary taskbars before UIA. Everything restores taskbar Button/SplitButton
hover using cached geometry, refreshed at most every 1.5 s by a single background scan in the isolated worker.
Cold or stale caches stay silent; an initial sweep may be needed to warm the cache. Host geometry/PID changes
invalidate it. A scan exceeding 2 s reports an error rather than trusting its result or spawning replacement
threads. Ordinary app lookups do not wait for shell discovery. No names/text, icon contents or app identities
are read; preview windows are not inferred. Old taskbar settings remain ignored: the style is the opt-in.
