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
- No UIA action patterns (`Invoke`, `Toggle`, `Value`, `Text`, `SelectionItem`, `ExpandCollapse`, `Window`, `Transform`).
- The tray does not reference the lab or any fixture code (test).
- The only cross-process mechanism is our own UIA worker (same binary) over stdin/stdout.
- Experiments never change focus: the EXP-020 answer panel is non-activating and the lab never restores/activates
  itself mid-session. Fixture-only `SetWindowPos` re-asserts z-order of *our own* fixture window without activation.

## Consent and control

- Tray starts **Off** until the user reads and accepts the consent text, then **Observe-only** on each launch.
- **On** is impossible without local `PHYSICAL` evidence for a background route on this machine.
- Default app policy is an **opt-in allowlist**; password managers, credential UI, LogonUI, consent.exe, LockApp are
  excluded by default; password-flagged controls are always silent.
- **Stop now (Off)** disposes the pipeline: raw input is unregistered and the UIA worker process is killed.

## Second-order harms and mitigations (SPEC §10.4)

| Harm | Mitigation here |
|---|---|
| Privacy: hover patterns reveal work | No persistence in the tray; no names/text; local-only lab artifacts; hashed identities |
| Accessibility: haptic spam | ≤4 pulses/s, cooldowns, fast-flick suppression, Quiet profile, instant Off |
| Cognitive: false "success" | No Success/Error/drop-accepted in the generic layer at all |
| Security: pressure to over-privilege | No UIAccess/elevation/injection paths exist in code; ADR 0004 requires review to add any |
| Reliability: hurting target apps | UIA in a separate process, one request in flight, timeouts + quarantine |
| Ecosystem: unreliable augmentation erodes trust in haptics | Evidence gates; tray only turns on with a felt, blinded result |

## Reversibility

Reversible: flags, profiles, rules, allow/deny lists, logging. Treated as **design-review gates**: shipping any
injection/UIAccess technique, persisting UI traces from real apps, uploading anything, or emitting outcome
(Success/Error) haptics.
