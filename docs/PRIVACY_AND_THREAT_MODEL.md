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
| UIA `ClassName`, `IsSelected` (SelectionItem), parent bounds | Yes | No | ClassName only to find taskbar buttons and slider parts; IsSelected so the tab you're already on stays silent. Read-only: `Select`/`AddToSelection`/`RemoveFromSelection` are forbidden members (EXP-081). |
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
| Accessibility: haptic spam | Land-not-pass filter, per-app density back-off, cooldowns, rate caps, Quiet style, intensity, instant Off |
| Cognitive: false "success" | No Success/Error/drop-accepted in the generic layer; only cooperating apps (which know the outcome) can send them |
| Security: pressure to over-privilege | No UIAccess/elevation/injection paths exist in code; ADR 0004 requires review to add any |
| Reliability: hurting target apps | UIA in a separate process, one request in flight, timeouts + quarantine |
| Ecosystem: unreliable augmentation erodes trust in haptics | The route the tray uses (R3a) was proven by a blinded, felt test (EXP-020) |

## Reversibility

Reversible: flags, profiles, rules, allow/deny lists, logging. Treated as **design-review gates**: shipping any
injection/UIAccess technique, persisting UI traces from real apps, uploading anything, or emitting outcome
(Success/Error) haptics.
