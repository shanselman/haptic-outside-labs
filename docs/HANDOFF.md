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
  or failed lookups, and expire past their fixed due time plus 45 ms tolerance instead of rewriting
  cause time. Due time is the later of pointer arrival plus dwell and first accepted lookup, preserving
  existing timing without letting slow providers create expired work. Transient errors inside the
  current element retain identity/range history rather than manufacturing another arrival.
  Strip-edge Collide checks a separate destination, and cooperative events carry even non-interactive
  or failed lookup context. Everything's density-off setting overrides per-app thinning.
  `ContextGenerationTests`, `UiaGenerationTests`, `PendingArrivalTests`, `PendingRegressionTests`,
  `DestinationContextTests` and twelve
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
  previously retained latest-input rerouting, unknown/handleless `Latest` fallback and queued output after Off.
  Lane C replaced the rerouting and fallback assertions with strict source-device routing. Lane E now drops
  queued output after Off, consent revocation, unverified output or Observe-only, without re-running policy.
  Stopping invalidates request routes and pending dwell before teardown. Tray Off takes effect before saving;
  settings failures use the tray error UI and same-folder replacement keeps the previous file intact.
- [x] **Regression net: trace coverage and caption baseline.** Optional input-class, origin, device-handle,
  arrival/removal and foreground trace fields are available without changing the 13 golden traces.
  EXP-070 now fails on unlisted traces and missing expected traces. `CaptionCharacterizationTests`
  pins Step on the first Minimize/Maximize arrival and Hover within the TitleBar group in Everything.
  Focused suppresses Minimize/Maximize and requires native Close classification. Recognized Close
  lands with one light Hover, not Step/Click; the tray's Close button hover setting defaults On, including
  existing settings files without the key. Quiet or disabling the setting makes Close silent.
  Routing, context-safety and lifecycle fixes are still pending; the data shape does not implement them.
- [x] **UIA startup/budget failures strand requests.** `UiaClient` returns explicit client failures for
  failed starts, exhausted budgets and pre-ready exits. Failed starts consume the rolling restart budget;
  the watchdog retries after backoff/budget refill without waiting for pointer movement. Startup and query
  deadlines remain separate. `UiaLifecycleTests` covers failure and refill; EXP-043 checks delivered IDs/counts.
- [x] **UIA acquires unnecessary properties.** The worker's production cache excludes AutomationId and
  all range properties; fixture requests opt into a separate AutomationId cache. Only an identified Slider
  (including its Thumb/Button parts) gets a second, read-only range cache. One-hop parent type/bounds and
  selection metadata remain intact. `UiaCacheTests` pins acquisition, not just serialized output.

- [x] **Report non-cancellation Showcase launch errors.** `TrayContext.OpenShowcase` now treats only
  native error 1223 as user cancellation. Other `Win32Exception` failures use the existing tray error UI,
  and a later launch remains usable. `ShowcaseLaunchTests` exercises cancellation, missing executable,
  access denied and unexpected bugs through an inert launch helper. No security prompt is bypassed.

## Lane G: exclusive output ownership

`OutputOwnership` uses a nonblocking, session-scoped named mutex shared by the Tray engine, Showcase engine,
and physical experiments. Ongoing sends are serialized with ownership release; EXP-020 stops new sends and
waits for an active send before finishing. User-triggered one-shot Showcase previews bypass the lease so
Feel it remains available while another owner holds it; previews can therefore overlap physical experiments.
EXP-010 and EXP-020 add the owner and grant state to method metadata and every trial's context. The Tray and
Showcase visibly report when their engines are paused; the Tray reacquires the lease and resumes while its
On setting remains enabled.

- [x] Wire TrayContext after Lane E's lost-controller change, preserving its passive defaults and output policy.
- [x] Validate ownership, paused state, bounded abandoned-owner recovery, pipeline denial, one-shot preview
  under contention and trial provenance with hardware-free tests. Physical output experiments were not run.
- [x] Rebase after any main changes, then check exact-head CI before READY.

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
- REC-1 runtime failure recovery now distinguishes explicit disconnected/closed/stale-controller results
  from unsupported waveforms and transient rejection. Only explicit loss revokes the affected mapping,
  updates tray readiness and requests one coalesced, silent rediscovery; fresh discovery or manual priming
  restores routing. Removal tombstones only the removed raw handle and its key-0 Touchpad/Generic alias,
  preserving other nonzero handles even with the same identity. Strong tombstones are pruned on map replacement;
  weak per-controller revocations reject stale map objects without retaining disconnected controllers.
  REC-2b now covers removals and repeated reconnect cycles in deterministic tests; resume without notifications,
  delayed availability and physical re-pairing still need operator evidence. REC-3 and remaining REC-4 checks stay open:
  stalled native-call containment and remaining failure-state coverage.
  REC-2a (device-arrival recovery) is implemented.
- The Showcase does not subscribe to `ControllerLostEvent`, so its bank stays stale until the next prime;
  the tray recovers on its own. Lane G owns Showcase changes; this is a documented limitation.
- The two-second fallback is not native-call cancellation or process isolation. The acquisition interface
  is undocumented. Functional/operator observations must not be promoted to blinded PHYSICAL evidence.
  Disruptive operator checks require an available, consenting operator.

## Lane C: safe device routing (2026-10-28)

[#13](https://github.com/shanselman/haptic-outside-labs/issues/13) (Lane C: route each haptic to its originating device) now routes output only through the input's exact mapped controller or a uniquely known, manually primed handleless touchpad. Discovery-only `Generic` entries route at their exact nonzero handle but do not establish handleless touchpad identity; removed devices and their same-identity aliases are revoked, while a newly captured same-identity controller may reconnect on a new or reused handle without reviving stale aliases. Cooperative intents require a latest eligible input from the same generation within 250 ms both when queued and immediately before dispatch.

`PipelineOutputTests` covers mouse-only, touchpad-only and real tray auto-priming with a recording sink, plus generation mismatch, alias removal/re-priming, same-identity reconnect, cooperative expiry, and Showcase reconnect. These tests are hardware-free; physical delivery and touchpad identification from discovery alone remain unverified. Keep Raw Input mouse-only and do not register the precision-touchpad collection; discovery-only touchpads stay silent until primed.

## Lane E: output lifecycle and bounded cooperation (2026-10-30)

[#15](https://github.com/shanselman/haptic-outside-labs/issues/15) (Lane E: Off, output authorization, controller health and pipe robustness): Off revokes queued output before persistence, explicit controller loss drops readiness and recovers silently, cooperative clients have a 256-character incremental line limit, 64-line cap and absolute one-second connection deadline, and Dispose closes the active client/listener and joins the pipe loop. `CooperativeServerTests` injects held/slow clients, unterminated oversized input and disposal; maximum valid LF/CR/CRLF messages remain accepted. These checks are hardware-free; neither API acceptance nor recording-sink output is PHYSICAL evidence.

## Taskbar lookups stop killing the UIA worker (2026-11-02)

Symptom: taskbar buttons gave no feedback, and soon after every app went silent. Cause: `FromPoint` on the
taskbar returns the `Shell_TrayWnd` pane, and the drill-down's enumeration of its buttons took 100–600 ms, longer
than the 250 ms hang timeout. Each refresh killed the worker, quarantined the foreground app and spent the
restart budget (6 per minute), so the client stopped answering. The worker now refreshes the host's buttons on a
background `uia-host-scan` thread with the same minimal cache request and answers from the last snapshot.
Measured: about 5 ms median, 0 timeouts and 0 restarts over a taskbar sweep. `TaskbarUiaTests` guards it on an
interactive desktop (skipped in CI).

Known limitations, not fixed:

- Quarantine after a timeout is keyed by the **foreground** PID, not the provider that hung, so a slow
  background surface (such as the taskbar) can mute the app you're actually using for 10 s.
- Teams (`msedgewebview2`) exposes one huge `MenuItem` covering most of the primary monitor, which can produce
  stray hover cues over Teams.

## Visual demo and tray ripples (2026-11-02)

The Showcase header's **Visual demo** switch runs Observe-only/dry-run without an output route or lease and draws
`CursorRippleOverlay` (now in `Haptics.Windows/HapticVisuals.cs`) at the pointer for each cue; the **Stage** tab
shows the cues full-size. `DecisionEvent` carries the observed pointer position. The tray's **Show ripples**
(menu and Settings, off by default) draws the same overlay for `Emit` decisions only, alongside real haptics; the
overlay is disposed when the setting turns off or the tray quits. Ripple placement across mixed-DPI monitors is
unverified.

## Pen integration: experimental single-pen route (2026-11-04)

The product goal now includes mouse, touchpad and pen. The existing router already handles exact mouse-device
mappings and uniquely identified, manually primed touchpads, with generation and removal/loss protection.
Extend that shared mechanism rather than adding an independent output path.

EXP-010 accepts `--target pen` and requires a present Pen controller before any pulse. A wrong-type controller,
missing controller or loss of foreground invalidates the trial and prevents output, for every target.
The live thread-manager readout is the first operator check. This only tests InputHapticsManager acquisition;
Windows also documents PenDevice.SimpleHapticsController, which is not yet wired here.
Operator run `20261104-072206-EXP-010-1cd790` passed: pen Click felt 6/6 vs controls 0/6, all trials type Pen.
See the evidence ledger. The exact pen model is unverified and every controller ID was `none`; the run cannot
supply a stable device-specific identity baseline.

EXP-030 run `20261104-072504-EXP-030-6db447` observed 763 background pen-signature events but no device handles.
Its formal result remains INDETERMINATE: the mouse-oriented nonzero-handle assertion cannot establish pen identity,
and its max callback exceeded 2 ms. The operator approved an experimental, explicitly primed **single-pen** mode.

Tray Settings now has **Experimental single pen**, off by default. Its ControllerBank stores one pen separately
from the raw-device map, so handle zero never overwrites the touchpad. Only fresh pen-class input over our UI can
capture a Pen controller; captures do not silently select a second pen. The pipeline routes pen signatures only to
that slot, invalidates queued output on changes and refuses stale lost/removed pen objects. Disabling the option
revokes before settings persistence. Haptics Off, input-device removal or explicit controller loss require re-priming.
Pen discrete-waveform misses can use a supported Click, recorded in attempt fallback metadata.

EXP-020/022 now accept `--target pen`: one explicitly primed Pen controller, pen-class input only, observed pointer
movement rather than raw mouse deltas, and R3a/R3b versus silent controls (R2 skipped). Waiting diagnostics show
which precondition is unmet without revealing pulse/control. Run `20261104-081454-EXP-020-677084` passed both routes:
6/6 felt versus 0/6 controls each, Click at 1.0. See the evidence ledger for fingerprint and identity limitations.

The operator then reported little/no hover feedback and persistent stray ink after touching an Explorer window
during trials. Contact and hover were not distinguished by this harness; do not call the result a hover pass or a
normal-input-behavior pass. After the experiment closed, neither Lab nor Tray was running, but Windows TabTip exposed
a full-screen "Shell Handwriting Canvas". Its snapshot did not reveal the stroke, so ownership/causality is unproven.
An authorized Escape dismissal was blocked by Windows integrity isolation, and manual Escape did not clear it.
The operator then ended TabTip.exe and confirmed the line disappeared, identifying Windows' handwriting overlay
as its owner. The operator suspects pen contact on Explorer's address bar offered a text/handwriting opportunity;
this is an unverified trigger hypothesis, not evidence that haptic output caused it. Defer further investigation
unless it recurs. Keep hover and contact separate in subsequent pen trials.

Subsequent unblinded tray check: the operator explicitly confirmed felt feedback while hovering over **our Settings
window only**, then reported no felt feedback outside it and some skipped cues. The screenshot showed Ready: Pen,
20 accepted calls, no rejected/expired calls, last output Pen: Click at 0.80, latest input Handleless and last routing
note skip:stale-device-generation. These are aggregate/last-event diagnostics, not a causal trace of each missed cue.
Do not describe this as validated cross-app tray hover support. The background trial sends during pen WM_INPUT;
the tray sends after semantic/UIA processing via its queue. Timing/context, class switching and actuator rate limits
are hypotheses to distinguish, not grounds to discard safety generations or attribute unmarked input to the pen.
The operator authorized releasing experimental support and deferring remaining checks; keep these limitations visible.

Deferred pen checks:
- Compare hover-only input/output over our Settings and safe controls in another app; record class changes,
  generations, waveform/intensity, foreground/background delivery and final send timing without sensitive UI data.
- Compare supported Hover with Click and sparse with dense cues before choosing any pen-specific intensity/rate change.
- Check mouse/touchpad coexistence, Off/re-prime, loss/removal and sleep/reconnect on hardware.
- Keep contact/hover trials separate; investigate Windows handwriting again only if the TabTip issue recurs.

Still pending: stable per-pen identity, cross-app tray feedback, mixed-device use, separate hover/contact trials,
ink-overlay investigation if it recurs, Settings touch/pen panning, and wider hardware/lifecycle testing. Settings Details now
shows latest input class, last output waveform/intensity/fallback and last routing note to help diagnose silence;
an accepted API call is not a felt pulse. The Showcase does not opt into pen mode. Raw Input remains mouse-only;
touch-promoted input remains silent. No foreground result is promoted into background PHYSICAL evidence.

## Next-session entry point

Select an unchecked operator task from the automatic-priming test plan. Existing focused UI coverage runs with
`dotnet test tests\Haptics.Windows.Tests --filter TrayWindowTests`.
Do not infer authorization to commit, push or release from this handoff.
