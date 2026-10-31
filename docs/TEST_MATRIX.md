# Test matrix: what each layer can and cannot prove

| Layer | Where it runs | Command | Proves | Cannot prove |
|---|---|---|---|---|
| **Unit / evidence rules** | anywhere (CI) | `dotnet test tests/Haptics.Core.Tests` | API-true never becomes PHYSICAL; status combination; Fisher test; ledger gating per machine | anything about Windows |
| **Policy** | anywhere (CI) | same | Off/consent/exclusions/password/disabled/stale/rate caps/cooldowns; no generic Success/Error | real latency |
| **Vocabulary** | anywhere (CI) | `dotnet test tests/Haptics.Core.Tests` (`VocabularyTests`) | land-vs-pass (speed + dwell), adaptive landing speed, Click for large targets in logical px, Step detents for tabs/radios, silence for selected tabs/tiny targets/inline links, group memory, strip-edge Collide, per-app density thinning (a taskbar sweep never goes silent), Everything style, cooperative arbitration | whether it *feels* right (EXP-090) |
| **Golden trace replay (13 traces)** | anywhere (CI) | `haptics-lab run EXP-070` or tests | exact logical decisions for sweep, 1 px crawl, flick, jitter, stationary redraw, menus, slider detents, wheel-at-limit, late UIA, disconnect, native suppression, password, excluded app | OS delivery, UIA timing, feel |
| **Regression net: trace and captions** | Core anywhere; experiment coverage on Windows (CI) | `dotnet test HapticsEverywhere.slnx --filter "InputTraceTests\|CaptionCharacterizationTests\|ReplayCoverageTests"` | compatible input metadata; foreground and device trace fields; first caption arrival is Step and within-caption arrival is Hover; EXP-070 fails for unlisted or missing traces | safe device attribution, a Close-specific rule, hardware output |
| **Regression net: pipeline output** | Windows (CI, no hardware calls) | `dotnet test tests\Haptics.Windows.Tests --filter "PipelineCharacterizationTests\|PipelineOutputTests"` | fake-clock deadlines and dwell; recording sink receives single-mouse Hover/Click/Step/Collide, exact mapped or current fallback controller and boosted intensity; policy admission and attempt accounting; pins known-bad latest-input rerouting and queued output after Off | correct input attribution or Off revocation (later lanes), Windows delivery, physical feedback |
| **Interaction generation** | Core anywhere; request plumbing on Windows | `dotnet test HapticsEverywhere.slnx --filter "ContextGenerationTests\|GenerationChangesRejectOldAnswersAndCancelPendingArrivals"` | same-time/same-point stale foreground/device answers are rejected; handle/class switches cancel dwell without re-arrival; explicit invalidation clears all cached context; two new golden traces | correct output-device routing, lifecycle wiring (later lanes), hardware feedback |
| **Static safety scan** | anywhere (CI) | `haptics-lab run EXP-081`, `SafetyTests` | no SendInput/SetCursorPos/hooks/focus/injection/settings/UIA-action APIs in compiled assemblies; mouse-only raw input; tray doesn't reference lab | reflection/dynamic calls |
| **UIA worker containment** | Windows (CI ok) | `haptics-lab run EXP-043`, `RuntimeTests`, `UiaLifecycleTests` | hang/slow/crash are cut off; delivered response IDs/counts exclude failures; startup/budget failures complete explicitly; bounded watchdog recovery after refill; fault commands need opt-in | real provider behaviour |
| **Capability inventory** | target machine | `haptics-lab doctor` | API/contract/waveforms present, guards don't throw | that anything buzzes |
| **Thread affinity** | target machine | `haptics-lab run EXP-023` | own-process TryGetForThread semantics | cross-process anything (never attempted) |
| **UIA fixture** | interactive desktop | `haptics-lab run EXP-040` | UIA type/bounds/flags vs fixture truth; lookup latency | real apps (EXP-041) |
| **UIA property acquisition** | Windows (CI ok) | `UiaCacheTests` | production excludes AutomationId and range properties; fixture adds only AutomationId; slider cache contains only read-only range metadata; parent/selection fields retained | provider correctness (EXP-040/041) |
| **UIA slider range round trip** | interactive desktop | `UiaCacheTests.WorkerReadsWpfSliderRangeButNotButtonRange` | real WPF Slider range through worker in production/fixture modes, Button has no range, parent metadata retained | skipped without interactive desktop; WinForms EXP-040 TrackBar may not expose RangeValue |
| **Raw input attribution** | interactive + human | `haptics-lab run EXP-030` | background WM_INPUT per device; callback cost; operator says mouse behaved normally | haptics |
| **Foreground haptics** | target mouse + human | `haptics-lab run EXP-010` | PHYSICAL pulse from our focused window (blinded) | background output |
| **Background haptics** | target mouse + human | `haptics-lab run EXP-020` | PHYSICAL pulse per route with another app foreground (blinded); API-level controller presence on the raw thread | usefulness |
| **Showcase** | target machine + human | `haptics-lab` (Showcase / Inspector tabs) | each sensation on demand; every decision and skip reason over real apps, latency percentiles | evidence (nothing is persisted) |
| **Showcase UI** | Windows (CI ok) | `dotnet test tests/Haptics.Windows.Tests --filter ShowcaseTests` | setup/evidence wording, style/intensity controls, accessibility toggles, real demo providers (RangeValue, selected tabs, password), blind-mode concealment, compact/wide layout with both theme dictionaries, embedded Lab; filesystem/native startup failures report errors and roll back, unexpected exceptions propagate | real pointer priming, felt output, monitor-to-monitor rendering or a full Narrator session |
| **Automatic priming fallback** | Windows (CI ok) | `dotnet test tests/Haptics.Windows.Tests --filter "ControllerDiscoveryTests\|StartupPrimingTests\|ControllerRefreshTests\|TrayWindowTests"` | lookup error reporting, zero/duplicate-handle filtering, two-second fallback, late completion, arrival coalescing, stale-result rejection, single in-flight lookup, Off and already-primed behavior | private API availability, controller acquisition or physical feedback on a user's device |
| **Style comparison** | target device + human | Showcase → *Compare styles* (`haptics-lab run EXP-090`) | blinded preference between Quiet/Focused/Everything in 60 s blocks | anything about other users |

## CI

The main-branch proof of concept keeps automatic lookup enabled. See the
[automatic-priming checklist](AUTOMATIC_PRIMING_TEST_PLAN.md) for completed observations, pending operator
checks (cold boot, sleep/resume, reconnect, x64, mixed devices and unhealthy Windows states), and runtime
failure-to-priming recovery TODOs. Hardware-free CI does not close those items.

`.github/workflows/ci.yml` builds and tests on `windows-latest` (x64, no haptic hardware, no interactive input), then
runs `doctor`, `EXP-070`, `EXP-081`, `EXP-043`. Hosted CI never claims physical results; reports carry
`NOT_TESTED_ON_HARDWARE`. Interactive/physical experiments are operator-run (SPEC §12.6 advises against a
self-hosted runner on a personal desktop).

For the Fluent Showcase, manually check light/dark switching, moving between monitors with different DPI, Tab /
Shift+Tab through settings and demo controls, menu arrow keys, Inspector/Lab navigation, and closing during a
comparison. Keep the tray running for the coexistence check: the Showcase must show **The tray has you covered**
with its own engine off. Haptic priming and pulse perception still require real device input and an operator;
UI rendering or automation is not physical evidence.
