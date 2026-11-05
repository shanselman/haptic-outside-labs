# Privacy and threat model

A desktop-wide accessibility observer is a surveillance capability even when its stated purpose is haptics
(SPEC §10.1). This project treats that as an architectural constraint, not a disclaimer.

## What is observed, and what is not

| Data | Observed? | Persisted? | Notes |
|---|---|---|---|
| Mouse movement, buttons, wheel (Raw Input, mouse usage only) | Yes, while Observe-only/On | No (tray); trial-level in lab artifacts | Never keyboard. No `NOLEGACY`/`CAPTUREMOUSE`: other apps see input unchanged. |
| Cursor position (`GetCursorPos`) | Yes | No | |
| Foreground window PID / process name | Yes | No (tray); lab artifacts store booleans like "foreground was lab" | Process name is used for allow/deny lists only. |
| UIA control type, bounds, enabled/offscreen/password flags | Yes | No (tray); EXP-040 stores them for **our fixture only** | |
| UIA `ClassName`, `IsSelected` (SelectionItem), parent bounds | Yes | No | ClassName identifies slider parts and exact recognized Chromium toolbar classes, never labels. Taskbar geometry discovery is Everything-only in the isolated worker. IsSelected keeps the current tab silent. Read-only: `Select`/`AddToSelection`/`RemoveFromSelection` are forbidden members (EXP-081). |
| UIA toggle availability/state, dropdown capability | Yes | No (tray); authored states in replay fixtures | Focused observes Off-to-On and On-to-Off after a mouse click. Reads `TogglePatternIdentifiers.ToggleStateProperty`; never obtains or invokes a Toggle/ExpandCollapse action pattern. Unknown/indeterminate state is neither On nor Off. |
| Window class, native visibility/enabled flags, window/DWM caption bounds, root-owner PID, non-client hit test / caption rectangles (`WM_NCHITTEST`, `WM_GETTITLEBARINFOEX`) | Yes | No | Bounded, read-only queries distinguish window Close from minimize/maximize even without UIA caption buttons. DWM bounds restrict the search, never identify an individual button. Only the observed Copilot helper can resolve to its same-process Tauri owner. Native window/button identity is hashed; missing rectangles are not guessed. These queries never click, close or focus a window. |
| Monitor DPI scale | Yes | No | Target sizes are judged in logical px. |
| UIA runtime id | Hashed (SHA-256, truncated) | No | Ephemeral identity only. |
| UIA `Name`, `Value`, `Text`, document content, URLs, titles | **Never requested** | — | `ValuePattern`/`TextPattern` are on the forbidden-type list (EXP-081). |
| Slider `RangeValue` numbers | Yes, for range rules | No | Not sensitive; used for detents. |
| `AutomationId` | Only for our own fixture in EXP-040 | Lab artifact | |
| Screenshots / OCR | **Never** | — | |
| Device path | Hashed; only VID/PID and connection type kept | Lab artifacts | Paths can embed instance/serial data. |
| Machine identity | Salted SHA-256 of machine name + OS build | Lab artifacts | Lets evidence be scoped to "this machine" without naming it. |
| Network | **None** | — | No telemetry, no update checks, no remote config. |

Lab artifacts live in `artifacts/` (git-ignored) or `%LOCALAPPDATA%\HapticsEverywhere\artifacts`. Nothing leaves the
machine unless an operator copies a summary into `docs/EVIDENCE_LEDGER.md`.

## Hard constraints (enforced, not aspirational)

- Standard user, `asInvoker`, `uiAccess="false"` (manifest; source scan test).
- No `SendInput`, `mouse_event`, `SetCursorPos`, `ClipCursor`, `BlockInput`, `SetForegroundWindow`, `SetFocus`,
  `SetCapture`, `AttachThreadInput`, `SetWindowsHookEx`, `CreateRemoteThread`, `WriteProcessMemory`,
  `SystemParametersInfo`, `RegisterHotKey`, `GetAsyncKeyState`, synthetic pointer/touch injection, `SendKeys`, or
  `Cursor.Position` setter in any shipped assembly — checked by metadata scan (`ForbiddenApiScanner`, EXP-081, tests).
- No UIA action patterns (`Invoke`, `Toggle`, `Value`, `Text`, `ExpandCollapse`, `Window`, `Transform`). `SelectionItem`
  may be *read* (`IsSelected`) but its action methods and `RangeValuePattern.SetValue` are forbidden members.
- Focused and Everything use toggle state via property identifiers and the dropdown-capability flag (user-approved metadata
  expansion). Only the currently hovered toggle is refreshed, at 100 ms intervals and briefly at 25 ms intervals after
  release; its identity, pointer location and foreground must still match. No keyboard input is observed.
- Recognized browser-toolbar panel commands are silent in Focused and do not poll toggle state. Inline links
  directly under Text/Document/Edit hover in all styles. Focused also accepts small contained Hyperlinks under
  Group (at most 24 logical px tall, parent at least twice as tall); Quiet does not. This geometry heuristic can
  admit compact button-like links, but adds no reads or tree scans. Other links are Everything-only. Existing bounds and DPI
  determine 30/50 ms dwell without additional reads. Direct Text/Image parts of Buttons and Hyperlinks resolve to their owner using one parent hop,
  cached metadata, same-process checks and restrictive child flags. No content, image pixels or URLs are read.
- Direct Text/Image children of MenuItem/TabItem share the owner's hashed identity, bounds and selection state.
  This uses one parent hop and existing cached properties, never text or image content; child safety flags are preserved.
- Direct Text/Image children of a toggle-capable CheckBox share its hashed identity, bounds and toggle state under
  the same one-parent and child-safety constraints. No new properties, content reads or UIA action patterns are added.
- A directly hit slider Thumb retains its cached bounds while the single parent hop resolves its owning Slider
  identity/range. Owner and child process/safety checks apply. No thumb geometry is inferred, and providers that
  expose only a Slider receive no hover. Drag range feedback does not depend on thumb discovery.
- Quiet/Focused reject primary/secondary taskbars before UIA. Everything observes only cached Button/SplitButton
  geometry and the existing property allowlist, never text/names/icon content. At most one background scan runs
  in the isolated worker; cache freshness is 1.5 s and host bounds/PID changes invalidate it. A blocked scan reports
  an error after 2 s and cannot create replacement threads or block synchronous ordinary-app queries.
- The tray does not reference the lab or any fixture code (test).
- Cross-process mechanisms: our own UIA worker (same binary) over stdin/stdout, and the optional cooperative pipe
  `HapticsEverywhere` (`PipeOptions.CurrentUserOnly`, one waveform word per line, still arbitrated by policy, off via
  the tray's "Let apps send haptics").
- Experiments never change focus: the EXP-020 answer panel is non-activating and the lab never restores/activates
  itself mid-session. Fixture-only `SetWindowPos` re-asserts z-order of *our own* fixture window without activation.

## Consent and control

- The tray is a **sample**: launching it is the opt-in, and it starts **On** in all apps (user decision 2026-10-23).
  A product build would restore an explicit consent step and an allowlist (the policy engine still supports both).
- Password managers, credential UI, LogonUI, consent.exe, LockApp are excluded by default; password-flagged controls
  are always silent; users can exclude the current app from the menu.
- **On** unchecked or **Quit** disposes the pipeline: raw input is unregistered and the UIA worker process is killed.

## Second-order harms and mitigations (SPEC §10.4)

| Harm | Mitigation here |
|---|---|
| Privacy: hover patterns reveal work | No persistence in the tray; no names/text; local-only lab artifacts; hashed identities |
| Accessibility: haptic spam | Land-not-pass filter, per-app density thinning, cooldowns, rate caps, Quiet style, intensity, instant Off |
| Cognitive: false "success" | No Success/Error/drop-accepted in the generic layer; only cooperating apps (which know the outcome) can send them |
| Security: pressure to over-privilege | No UIAccess/elevation/injection paths exist in code; ADR 0004 requires review to add any |
| Reliability: hurting target apps | UIA in a separate process, one request in flight, timeouts + quarantine |
| Ecosystem: unreliable augmentation erodes trust in haptics | The route the tray uses (R3a) was proven by a blinded, felt test (EXP-020) |

## Reversibility

Reversible: flags, profiles, rules, allow/deny lists, logging. Treated as **design-review gates**: shipping any
injection/UIAccess technique, persisting UI traces from real apps, uploading anything, or emitting outcome
(Success/Error) haptics.
