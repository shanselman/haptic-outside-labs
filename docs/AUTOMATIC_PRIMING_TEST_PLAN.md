# Automatic priming: proof-of-concept checklist

**Decision (2026-10-27):** keep automatic controller lookup enabled on `main`, with ordinary window-input
priming as the fallback. This is a proof of concept, not a Store submission or a new release. The Fluent
Showcase and this startup path are source-build changes; the existing v0.3.0 downloads are unchanged.
An undocumented API is not, by itself, a reason to hide the feature behind another switch.

The acquisition mechanism and its limitations are in [RESEARCH.md](RESEARCH.md#experimental-acquisition-without-window-input-2026-10-27).
This checklist separates what we have observed from what still needs testing and implementation.

## Established so far

- [x] A fresh ARM64 process obtained a controller without a window or input registration. Its public thread
  manager remained empty. Controller capabilities were readable; this probe sent no feedback.
- [x] The production discovery helper worked on a background thread and populated only the per-device map,
  leaving the manual fallback null.
- [x] A self-contained ARM64 tray started without a priming window. In an unblinded off/on check with the
  Surface Mouse, added ticks disappeared when all our engines stopped and returned after a fresh tray launch
  without opening Settings.
- [x] Automated coverage exercises missing controllers, access-denied diagnostics, zero/duplicate handles,
  the two-second startup fallback, late completion, Off/already-primed behavior, and unexpected exceptions.
- [x] Existing policy, replay, and static safety tests still pass.
- [x] One operator Sleep/wake/unlock check kept ticks working in the same tray process, without opening Settings.
- [x] A Bluetooth mouse power-cycle exposed a changed raw handle and lost ticks; manual priming recovered them.
  After adding arrival-triggered rediscovery, a repeat power-cycle recovered ticks without the wiggle.

These observations do not establish cold-boot behavior, every device/build, or blinded PHYSICAL evidence.
Do not promote them into the evidence ledger as such.

## Pending operator checks

Run disruptive lifecycle checks only when the operator is ready. Do not reboot, suspend, disconnect devices,
or change another person's Windows session as an unattended test.

| Done | Check | Procedure | What to establish |
| --- | --- | --- | --- |
| [ ] | Cold boot and sign-in | Test an actual cold boot separately from restarting the app. Start the tray manually and, separately, through its existing Start with Windows option. Test a connected mouse and one that wakes/connects later. | No prior app priming is required when lookup succeeds; unavailable devices have an understandable manual path. A saved Off preference stays Off. |
| [ ] | Sleep and resume | Test the sleep states the machine actually supports, including Modern Standby or hibernation where applicable. Try controls after resume before touching Settings. | Whether controllers survive; whether stale ones fail safely and manual priming restores feedback. |
| [ ] | Bluetooth/USB reconnect | Repeat power-cycles, test USB if available, and launch disconnected then connect. Record re-pairing separately if the operator chooses to try it. | Handle changes do not misroute output. One BLE power-cycle passed after the fix below; the broader combinations remain pending. |
| [ ] | Native x64 | Build/run on an Intel/AMD Windows machine, with and without supported haptic hardware. Record OS and InputHost.dll versions. | The native interface/ABI and fallback work on x64. Cross-compiling on ARM64 or passing hardware-free CI is not this check. |
| [ ] | Touchpad and mixed devices | Test touchpad alone, mouse alone, both, and an ordinary non-haptic mouse. Switch the device producing input. | Unknown/handleless input never buzzes an automatically discovered mouse. Manual priming identifies the device and restores type-specific behavior/boost. |
| [ ] | Long idle and repeated restarts | Leave the controller idle, restart the tray repeatedly, and try ordinary controls without opening Settings. | Controller lifetime, clean shutdown, no duplicate engines, and no growth in resources or repeated startup prompts. |
| [ ] | Lock/unlock and session changes | Test lock/unlock and, on a suitable test machine, remote/local session transitions. | No output on protected desktops or for unavailable devices; recovery stays passive and respects Off/exclusions. |
| [ ] | Other Windows builds | Exercise a build with the interface, one without it, and a build without the public haptics API if available. | Missing support is diagnosed honestly. A priming gesture must not be presented as a cure for an absent OS API. |
| [ ] | Off/on negative control | On each relevant setup, stop both tray and Showcase, check the same ordinary controls, then start a fresh tray without opening Settings. Avoid native Snap feedback as the test stimulus. | Added ticks correlate with our engine, not native feedback or a leftover process. This is still unblinded. |

## Unavailable or unhealthy Windows states

Use test doubles or a disposable test environment first. Do not patch DLLs, inject into Windows, change
security permissions, or deliberately break live input services to make a test fail.

- [ ] Simulate a missing activation class/interface, denied access, enumeration failure, and a device disappearing
  between enumeration and acquisition. Preserve the real failure reason and keep manual priming available.
- [ ] Hold discovery indefinitely and verify the UI, Off, and Quit stay responsive. The current two-second
  fallback only stops waiting to show the window: it does **not** cancel or isolate the native call.
- [ ] Return a controller successfully, then simulate a disconnected/stale controller or failed output call.
  This is not covered by the current automatic startup fallback; see the implementation TODOs below.
- [ ] Finish lookup after timeout, manual priming, Off, or shutdown. Do not reopen unwanted windows, overwrite
  a manually established controller, or enable output after Off.
- [ ] Verify rate limits, password/exclusion silence, and input passivity throughout failure and recovery.

## Recovery TODOs

- [ ] **REC-1: Runtime failure to wiggle fallback.** Distinguish a lost/unusable controller from an unsupported
  waveform or one transient rejection. Disable only the affected automatic mapping; explain the failure and
  offer ordinary priming. Do not flood the user with windows or silently route another device's input there.
- [x] **REC-2a: Device-arrival recovery.** Remove a disconnected handle's mapping and stale manual fallback,
  coalesce arrivals into one lookup at a time, and discard results from an older device set. Preserve other
  manually primed controllers, respect Off, and retain manual priming if lookup fails or is slow.
- [ ] **REC-2b: Remaining lifecycle recovery.** Exercise resume without device-change notifications, delayed
  availability after an arrival, repeated reconnects, and re-pairing. Add bounded retries only where observations
  show they are needed; controller failure without a device notification still needs REC-1.
- [ ] **REC-3: Stalled native lookup.** Measure real failure behavior before choosing stronger containment.
  A background task is not process isolation; a native crash cannot be made safe merely by catching HRESULTs.
- [ ] **REC-4: Remaining failure-state coverage.** Arrival coalescing, stale-result rejection, single in-flight
  lookup and Off behavior now have deterministic tests. Add the remaining REC-1/REC-2b cases, including
  mixed-device output failures. Keep absent OS support distinct from a device needing priming.

Startup and device-arrival lookup are implemented; the unchecked recovery items are not. These are follow-up
work, not claims that all failure paths already recover automatically and not a requirement to cut a release.

## Operator session: 2026-10-27

All observations below are unblinded functional checks on the ARM64 development machine with Surface Mouse
2nd Edition, not formal PHYSICAL results. The baseline was the code checked in as `a6b6796`; the reconnect fix
was tested from the subsequent working tree before its commit.

| Check | Observation |
| --- | --- |
| BLE power-cycle, before fix | The pointer returned but ticks did not. The hashed device path was unchanged, while the raw handle changed. The same tray process remained responsive. |
| Manual recovery, before fix | Moving over Settings restored ticks without restarting the tray. |
| Windows Sleep/wake | A fresh automatically primed tray ticked before and after sleep in the same process, with the same mouse handle and no Settings interaction. Other sleep states are not covered. |
| BLE power-cycle, after fix | The operator reported ticks returned without priming. A different raw handle was present, the same tray process remained responsive, and no Settings window was open at the follow-up check. |

## Recording a run

Record the commit (and whether the tree was dirty), architecture, OS build, InputHost.dll version, device model
and connection type, scenario, whether Settings received real input, Startup diagnostic, API outcome, and the
operator's separate felt/not-felt/unsure response. Avoid device serials, raw interface paths, and app content.
Include the stopped-app control and any recovery action needed.

Keep ad-hoc/unblinded results labeled as such. Formal physical evidence still needs an appropriate blinded
experiment, negative control, `ExperimentSession.Complete()`, and the normal evidence-summary workflow.
