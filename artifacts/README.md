# artifacts/

Local experiment output (`runs/<run-id>/result.json`, `trace.jsonl`, `summary.md`, and `report.md/html`) lands here
when the lab runs from inside this repository. Everything except this README is git-ignored on purpose: raw desktop
traces stay on the machine that produced them (see `docs/PRIVACY_AND_THREAT_MODEL.md`).

To publish a result, review it and paste the sanitized summary into `docs/EVIDENCE_LEDGER.md`:

```powershell
haptics-lab evidence summarize artifacts\runs\<run-id>
```
