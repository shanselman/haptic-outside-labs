# Test matrix: what each layer can and cannot prove

| Layer | Where it runs | Command | Proves | Cannot prove |
|---|---|---|---|---|
| **Unit / evidence rules** | anywhere (CI) | `dotnet test tests/Haptics.Core.Tests` | API-true never becomes PHYSICAL; status combination; Fisher test; ledger gating per machine | anything about Windows |
| **Policy** | anywhere (CI) | same | Off/consent/exclusions/password/disabled/stale/rate caps/cooldowns; no generic Success/Error | real latency |
| **Vocabulary** | anywhere (CI) | `dotnet test tests/Haptics.Core.Tests` (`VocabularyTests`) | land-vs-pass (speed + dwell), adaptive landing speed, Click for large targets in logical px, Step detents for tabs/radios, silence for selected tabs/tiny targets/inline links, group memory, strip-edge Collide, per-app density back-off, Everything style, cooperative arbitration | whether it *feels* right (EXP-090) |
| **Golden trace replay (13 traces)** | anywhere (CI) | `haptics-lab run EXP-070` or tests | exact logical decisions for sweep, 1 px crawl, flick, jitter, stationary redraw, menus, slider detents, wheel-at-limit, late UIA, disconnect, native suppression, password, excluded app | OS delivery, UIA timing, feel |
| **Static safety scan** | anywhere (CI) | `haptics-lab run EXP-081`, `SafetyTests` | no SendInput/SetCursorPos/hooks/focus/injection/settings/UIA-action APIs in compiled assemblies; mouse-only raw input; tray doesn't reference lab | reflection/dynamic calls |
| **UIA worker containment** | Windows (CI ok) | `haptics-lab run EXP-043`, `RuntimeTests` | hang/slow/crash are cut off, restarted, never delivered late; fault commands need opt-in | real provider behaviour |
| **Capability inventory** | target machine | `haptics-lab doctor` | API/contract/waveforms present, guards don't throw | that anything buzzes |
| **Thread affinity** | target machine | `haptics-lab run EXP-023` | own-process TryGetForThread semantics | cross-process anything (never attempted) |
| **UIA fixture** | interactive desktop | `haptics-lab run EXP-040` | UIA type/bounds/flags vs fixture truth; lookup latency | real apps (EXP-041) |
| **Raw input attribution** | interactive + human | `haptics-lab run EXP-030` | background WM_INPUT per device; callback cost; operator says mouse behaved normally | haptics |
| **Foreground haptics** | target mouse + human | `haptics-lab run EXP-010` | PHYSICAL pulse from our focused window (blinded) | background output |
| **Background haptics** | target mouse + human | `haptics-lab run EXP-020` | PHYSICAL pulse per route with another app foreground (blinded); API-level controller presence on the raw thread | usefulness |
| **Showcase** | target machine + human | `haptics-lab` (Showcase / Inspector tabs) | each sensation on demand; every decision and skip reason over real apps, latency percentiles | evidence (nothing is persisted) |
| **Style comparison** | target device + human | Showcase → *Compare styles* (`haptics-lab run EXP-090`) | blinded preference between Quiet/Focused/Everything in 60 s blocks | anything about other users |

## CI

`.github/workflows/ci.yml` builds and tests on `windows-latest` (x64, no haptic hardware, no interactive input), then
runs `doctor`, `EXP-070`, `EXP-081`, `EXP-043`. Hosted CI never claims physical results; reports carry
`NOT_TESTED_ON_HARDWARE`. Interactive/physical experiments are operator-run (SPEC §12.6 advises against a
self-hosted runner on a personal desktop).
