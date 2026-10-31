# Session handoff: v0.4.0 review and follow-up

Recorded 2026-10-27. This repository document, not a chat canvas or session-local todo database, is the
cross-session handoff. Recheck the current tree before acting and update these items as work lands.

## Baseline

At handoff, `main` was clean at `425cf04` (Version 0.4.0), tagged `v0.4.0`.
The review fixes below are already committed in `a6b6796`; reconnect recovery is in `9de9f5d`.
The open "No-wiggle startup diff" canvas is an earlier code snapshot, not an outstanding patch to apply.
The fixes are part of the v0.4.0 source history. This handoff itself is a later documentation change.

## Review findings already addressed

- [x] **Core context safety through transitions and pending work.** Interaction generations now revoke
  old UIA answers after foreground/device changes and explicit `Invalidate(reason)` calls. Request tokens
  stay client-side through UIA coalescing, failures and disposal; device switches cancel dwell without
  re-arrival on the same target. Pending arrivals refresh newer same-ID safety metadata, cancel on unsafe
  or failed lookups, and expire past their fixed dwell deadline plus 30 ms tolerance instead of rewriting
  cause time. Dwell starts after the target is known so slow valid providers do not create expired work.
  Strip-edge Collide checks a separate destination, and cooperative events carry even non-interactive
  or failed lookup context. Everything's density-off setting overrides per-app thinning.
  `ContextGenerationTests`, `UiaGenerationTests`, `PendingArrivalTests`, `DestinationContextTests` and eight
  new golden traces cover these rules; the original 13 traces are unchanged. Output-device routing and
  lifecycle wiring remain owned by their coordinated lanes, not proved by these Core tests.

- [x] **Narrator switch changes were not applied or saved.** UI Automation's toggle action changes
  `IsChecked` and raises `Checked`/`Unchecked`, but not `Click`. All four tray switches now use
  `OnToggled`, guarded by `_syncing`, in `src/Haptics.Tray/SettingsWindow.xaml.cs`.
  `tests/Haptics.Windows.Tests/TrayWindowTests.cs` exercises all four through
  `ToggleButtonAutomationPeer` / `IToggleProvider.Toggle()`, rather than synthesizing a click.
  Keep this regression coverage and the refresh guard.
- [x] **Exceptions in WPF handlers could terminate the WinForms-hosted tray.**
  `src/Haptics.Tray/Program.cs` now registers `DispatcherUnhandledException`, displays the error and marks
  it handled. `TrayContext.OpenShowcase` also catches `Win32Exception` from `Process.Start`.
  The original review reproduced the mixed WPF/WinForms exception path; that reproduction is historical
  evidence, not a newly executed test in this handoff session.

## Outstanding review TODO

- [x] **Device-specific baseline eligibility.** New EXP-010 runs persist `method.targetControllerIdHash`
  only when all valid answered pulses/controls identify the same known controller. EXP-020 compares
  that hash as well as machine, waveform and intensity; every valid answered pulse in each route must
  match the captured target (including the raw-thread R2 controller). Missing, mixed or unknown identities cannot
  make "not felt" a FAIL. Historical entries are never rewritten: their recorded PASS/PHYSICAL verdict
  still displays as recorded, but without an identity they do **not** satisfy a device-specific baseline.
  Rerun EXP-010 with the target device to establish one. Priming device counts now use a list of hashed
  device identities instead of a VID/PID dictionary, so two identical models remain separate.
- [x] **Regression net: pipeline clock and recording output.** `PipelineOptions.Clock` defaults to the
  monotonic clock; optional `IPipelineOutputSink` replaces queueing/sending only. `PipelineOutputTests`
  uses a manually drained queue and fake clock, never starts the pipeline, and records inert controller
  identities without calling Windows haptics. `PipelineCharacterizationTests` also runs against the
  pre-seam pipeline. Single-mouse Hover/Click/Step/Collide, deadline/dwell boundaries, intensity boost
  and policy admission are pinned. The tests marked "current behavior; lane C/E changes this" deliberately
  retain latest-input rerouting, unknown/handleless `Latest` fallback and queued output after Off.
  Update those assertions with the later routing/lifecycle fixes; do not treat them as desired behavior.
- [x] **Regression net: trace coverage and caption baseline.** Optional input-class, origin, device-handle,
  arrival/removal and foreground trace fields are available without changing the 13 golden traces.
  EXP-070 now fails on unlisted traces and missing expected traces. `CaptionCharacterizationTests`
  pins today's Step on the first TitleBar caption arrival (Close, Minimize, Maximize) and Hover within
  the group. Lane F changes Close deliberately; Minimize and Maximize must retain their baseline.
  Routing, context-safety and lifecycle fixes are still pending; the data shape does not implement them.
- [x] **UIA startup/budget failures strand requests.** `UiaClient` returns explicit client failures for
  failed starts, exhausted budgets and pre-ready exits. Failed starts consume the rolling restart budget;
  the watchdog retries after backoff/budget refill without waiting for pointer movement. Startup and query
  deadlines remain separate. `UiaLifecycleTests` covers failure and refill; EXP-043 checks delivered IDs/counts.
- [x] **UIA acquires unnecessary properties.** The worker's production cache excludes AutomationId and
  all range properties; fixture requests opt into a separate AutomationId cache. Only an identified Slider
  (including its Thumb/Button parts) gets a second, read-only range cache. One-hop parent type/bounds and
  selection metadata remain intact. `UiaCacheTests` pins acquisition, not just serialized output.

- [ ] **Report non-cancellation Showcase launch errors.** `src/Haptics.Tray/TrayContext.cs`,
  `OpenShowcase`, currently swallows every `Win32Exception` as if it were cancellation at a security prompt.
  Handle native error 1223 as user cancellation; surface other launch failures through the existing tray
  error UI rather than silently ignoring them. Add focused regression coverage for cancellation versus a
  genuine launch failure, and ensure the tray remains usable. Do not bypass SmartScreen or security prompts.
  This is recorded work only; no launch-error behavior was changed during this handoff.

## Automatic priming: retain the existing checklist

The authoritative pending tasks and operator observations are in
[AUTOMATIC_PRIMING_TEST_PLAN.md](AUTOMATIC_PRIMING_TEST_PLAN.md); the verified acquisition mechanism and
limitations are in [RESEARCH.md](RESEARCH.md#experimental-acquisition-without-window-input-2026-10-27).
Do not replace those records with this summary.

- Keep automatic discovery enabled with manual priming as fallback. Automatic controllers are matched only
  to their exact raw-device handle, never used as the fallback for unknown/handleless input.
- One unblinded ARM64 sleep/wake check and one post-fix BLE power-cycle succeeded. The reconnect bug was
  a changed raw handle; arrival-triggered rediscovery now removes stale mappings and rejects stale results.
- Cold boot/sign-in, native x64 hardware, other Windows builds, touchpads/mixed devices, repeated reconnects,
  long idle, broader sleep/session transitions and unhealthy-platform cases remain on the existing checklist.
- Recovery TODOs REC-1, REC-2b, REC-3 and REC-4 remain open: runtime output failure to manual fallback,
  remaining lifecycle recovery, stalled native-call containment and remaining failure-state coverage.
  REC-2a (device-arrival recovery) is implemented.
- The two-second fallback is not native-call cancellation or process isolation. The acquisition interface
  is undocumented. Functional/operator observations must not be promoted to blinded PHYSICAL evidence.
  Disruptive operator checks require an available, consenting operator.

## Next-session entry point

Start with the outstanding launch-error TODO above, or select an unchecked task from the automatic-priming
test plan. Existing focused UI coverage runs with
`dotnet test tests\Haptics.Windows.Tests --filter TrayWindowTests`.
Do not infer authorization to commit, push or release from this handoff.
