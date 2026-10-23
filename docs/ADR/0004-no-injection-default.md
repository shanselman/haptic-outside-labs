# ADR 0004 — No injection, UIAccess, or private protocols by default

**Status:** accepted

## Context
If supported routes fail, it is tempting to reach for DLL injection into foreground apps, `uiAccess=true`, browser
DevTools attachment, OCR, or private HID reports to the mouse (SPEC §4.1 R5, §7.1).

## Decision
None of these exist in the codebase. Adding any requires a separate, explicitly proposed research branch with:
a security/privacy review, explicit opt-in per app, a reproducibility report, and a demonstrated advantage over the
supported routes. They are never automatic fallbacks.

## Consequences
If EXP-020 fails on all supported routes, the honest outcome is a documented NO-GO/PIVOT for a third-party tray and a
recommendation for a first-party Windows/Surface integration, not a quiet escalation of privilege.
