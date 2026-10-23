# Testing with a Surface Mouse (operator runbook)

This is the checklist for the first **physical** session. Everything automated has already run; what is
left can only be answered by a human hand on a real mouse. Budget ~20 minutes.

> **What you are proving:** whether a normal, non-elevated, non-injected app can make the Surface Mouse
> buzz **while some other app is in the foreground** (SPEC §0). The lab will not accept "the API returned
> true" as an answer. Only your blinded "felt / not felt" answers produce `PHYSICAL` evidence.

## 0. Before you start (2 min)

1. Pair the **Surface Mouse (2nd Edition)** and make sure it is charged.
2. **Settings → Bluetooth & devices → Mouse → Haptic feedback**: on, intensity at a level you can clearly feel.
   Note the value; you will want it in the ledger.
3. Feel a **native** haptic first so you know what to expect, e.g. drag a window to a screen edge
   (Windows Snap) or align a shape in PowerPoint. If you cannot feel native haptics, stop — the hardware or
   setting is the problem, and any experiment result would be `INDETERMINATE`.
4. Keep your other hand **off the touchpad**. This Surface Laptop's touchpad is *also* a haptic device;
   the lab checks which device delivered input and discards trials from the wrong one.
5. Build and open the lab:

   ```powershell
   cd D:\github\haptic-outside-labs
   .\scripts\run-lab.ps1          # builds Release and opens the dashboard
   ```

The banner should say `NOT_TESTED_ON_HARDWARE`. **Gates** shows *Next: EXP-010*.

## 1. EXP-010 — foreground baseline (5 min)

*Can our own focused window make the mouse buzz using the documented pattern?*

1. Dashboard → **Run next experiment** (or `haptics-lab run EXP-010`).
2. Defaults are fine: waveform arms **Click** and **Step**, 6 pulses each, 6 silent controls, intensity 1.0,
   target **mouse**. Tick *"I have felt this mouse's native haptics"* only if step 0.3 succeeded.
3. **Start blinded trials.** Keep moving the mouse inside the dark pad. When it flashes orange **NOW**,
   decide whether you felt a pulse and answer **Felt it (F)** / **Not felt (N)** / **Unsure (U)**.
   - About a third of the trials send nothing. That is the point. Answer honestly; "Unsure" is fine.
   - The bottom status line shows `device=Mouse controller=present` when the lab sees the mouse. If it says
     `Touchpad`, you touched the touchpad — move the mouse.
4. At the end the pad turns green (PASS), red (FAIL) or blue (INDETERMINATE).

**PASS** means ≥80% of pulses felt, ≤20% of controls "felt", one-sided Fisher p ≤ 0.01. If it is not PASS,
check intensity, battery, and that you used the mouse; then rerun. Do not proceed to EXP-020 without a PASS —
EXP-020 can only report a real FAIL when EXP-010 has passed on this machine.

## 2. EXP-020 — the critical gate (8 min)

*Can it buzz while another app is foreground, driven from background Raw Input?*

1. Open **Notepad** (any unrelated app works; Notepad is simplest).
2. Dashboard → **Run next experiment** (EXP-020). Defaults: all three routes, waveform Click, intensity 1.0,
   raw window `message`, target `mouse`, 6 pulses/route, 6 controls.
   - **R2** — `GetForCurrentThread()` on the thread that receives background `WM_INPUT` (the main hypothesis).
   - **R3a** — a `SimpleHapticsController` captured from your mouse during step 3, used directly later.
   - **R3b** — that controller installed with `SetOverrideHapticsController` on the raw-input thread.
3. **1. Start (prime)** and move the **mouse** over the dark pad for a second until it says
   *Controller captured (Mouse …)*. This also tells the lab which raw device is "the mouse".
4. **2. Begin background trials.** The lab window minimizes and a small dark panel appears bottom-right.
   That panel is *non-activating*: clicking it never steals focus.
5. **Click into Notepad** so it is the foreground app, then keep moving the mouse over Notepad.
   After a random delay the panel turns orange **NOW**. Decide, then click **Felt it / Not felt / Unsure** on the
   panel, and move back over Notepad for the next trial. Trials only fire while Notepad (not the lab) is in front
   and the pointer is not over the panel.
6. After the last trial the panel shows the result. Click **Stop** to close it, then restore the minimized
   *EXP-020* window for the per-route table.

**Reading the result:** each route gets its own verdict. Any route at `PASS` unlocks that route for the tray
on this machine. The result also records how often the raw-input thread *saw a controller at all* during
background input (`rawThreadBackgroundSamplesWithController`) — that is API-level evidence for H1 even if you
felt nothing.

## 3. Optional, if time allows

- **EXP-030** (2 min) — `haptics-lab run EXP-030`: click into another app and use the mouse normally for 20 s.
  Confirms background raw input is attributed to the right device and the callback stays under 2 ms.
- **Live observer** (dashboard tab) — *Observe-only*, profile *Balanced*. Hover Word's ribbon, Explorer,
  Edge, the lab's **fixture** (Experiments → Open fixture). Watch `EnterControl → DryRun` rows. For slider detents
  use `fixtures\browser\index.html` in Edge (the WinForms fixture slider exposes no RangeValue, so it is
  deliberately silent). If a background route passed, pick it in **Route** and switch Mode to **On** to feel it.
- **Waveform playground** — feel each named waveform (foreground, not evidence).

## 4. Tray (only if EXP-020 passed)

```powershell
.\scripts\run-tray.ps1
```

Right-click the tray icon → read the consent text → *Observe only* → *On (R…)*. If the passing route was
R3a/R3b, use *Prime haptic mouse…* first. Under *Apps*, either allow the current app or toggle
*Allow all apps (except exclusions)*. **Stop now (Off)** tears everything down.

## 5. Record what happened

```powershell
haptics-lab report                                   # artifacts\report.html
haptics-lab evidence summarize artifacts\runs\<run-id>   # paste into docs\EVIDENCE_LEDGER.md
```

Add a sentence on the **promotion decision** for each hypothesis in `docs/HYPOTHESES.yaml` (e.g. H1 →
`CONFIRMED_ON[machine fingerprint]` or `FALSIFIED_ON[…]`). Raw traces stay in `artifacts/` (git-ignored) unless
you choose to share a sanitized copy.

## Variant: the Surface Laptop haptic touchpad (no mouse needed)

The same routing question applies to any haptic input device, and the Surface Laptop 8 touchpad is on Microsoft's
supported-device list. Run the identical steps with **Target = touchpad**:

```powershell
haptics-lab run EXP-010 --target touchpad
haptics-lab run EXP-020 --target touchpad
```

- Use only the touchpad, keep the external mouse still, and **glide** your finger. Don't press: a touchpad press makes
  its own native haptic click.
- In EXP-010, answer with the keyboard (**F / N / U**) rather than clicking.
- Precision touchpads may deliver raw input **without a device handle**. EXP-020 locks onto whichever device you used
  while priming (including a handle-less one). It records 	argetRawDevice and adds a limitation note, since handle-less
  input can't be told apart from synthetic input by handle alone (nothing in the lab generates synthetic input).
- A touchpad PASS answers H1/H2 for the **touchpad**, scoped to this machine. The Surface Mouse still needs its own run.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `device=Touchpad` in EXP-010 | You touched the touchpad last. Move the mouse. |
| No `NOW` in EXP-020 | Lab still foreground, pointer over the panel, or input coming from a different device than the primed one. |
| `R3a/R3b skipped` | Prime phase never captured a `Mouse` controller. Move the mouse (not touchpad) over the pad. |
| Everything `api-rejected` | No haptic controller for that thread/route — itself a result; note it. |
| Felt pulses on controls too | Mechanical click or expectation bias. Use Unsure; increase trials. |
