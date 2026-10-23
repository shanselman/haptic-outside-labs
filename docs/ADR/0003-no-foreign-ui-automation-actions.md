# ADR 0003 — No foreign UI automation actions; passive observation only

**Status:** accepted

## Context
The product augments apps it does not own. Any action on them — invoking a control, setting focus, synthesizing
input, moving the pointer — changes behaviour the user didn't ask for and crosses a trust line (SPEC §1.2, §4.7).

## Decision
- Production code may read: mouse-only Raw Input, cursor position, foreground PID, and a minimal set of UIA properties
  (type, bounds, flags, hashed runtime id, slider range).
- Production code may not call or reference: `SendInput` and friends, cursor setters, focus/foreground setters,
  hooks, UIA action patterns, `ValuePattern`/`TextPattern`, `SendKeys`.
- This is enforced by `ForbiddenApiScanner` over compiled assemblies (EXP-081 and `SafetyTests`), with a negative
  control proving the scanner detects a forbidden import.
- If synthetic input is ever needed (EXP-031), it goes in a separate fixture-only project that the tray cannot reference.

## Consequences
Automated testing of physical routing is impossible by construction (also observed: posted input yields no haptic
controller). Physical experiments therefore rely on a human operator with blinded trials.
