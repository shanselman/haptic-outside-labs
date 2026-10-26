# Releasing

[`.github/workflows/release.yml`](../.github/workflows/release.yml) builds, signs and publishes releases. It is adapted
from WindowsEdgeLight's workflow: it runs the tests, publishes self-contained single-file builds for win-arm64 and
win-x64 (`scripts/publish.ps1`), signs every `.exe` with Azure Trusted Signing (account `hanselman`, certificate profile
`WindowsEdgeLight`), zips them, and attaches the zips to a GitHub release.

## One-time setup: signing secrets

The workflow signs as the same Azure app registration as WindowsEdgeLight. GitHub can't copy secret values between
repositories, so set them here. Each command prompts for the value without echoing it:

```powershell
gh secret set AZURE_TENANT_ID -R shanselman/haptic-outside-labs
gh secret set AZURE_CLIENT_ID -R shanselman/haptic-outside-labs
gh secret set AZURE_CLIENT_SECRET -R shanselman/haptic-outside-labs
```

If the client secret isn't saved anywhere, add a new one to the same app registration and pipe it straight into GitHub
so it's never shown. `--append` keeps WindowsEdgeLight's existing secret working:

```powershell
az ad app credential reset --id <AZURE_CLIENT_ID> --append --display-name haptic-outside-labs --query password -o tsv |
    gh secret set AZURE_CLIENT_SECRET -R shanselman/haptic-outside-labs
```

## Cut a release

1. Set `<Version>` in `Directory.Build.props` (for example `0.3.0`) and merge it to `main`.
2. Tag and push: `git tag v0.3.0` then `git push origin v0.3.0`.
3. The workflow creates release `v0.3.0` with `HapticsEverywhere-0.3.0-win-arm64.zip` and `-win-x64.zip`.

Release binaries take their version from the tag; local builds use `Directory.Build.props`. To get signed zips as a
build artifact without publishing a release, run `gh workflow run release.yml`. To check a signature, run
`Get-AuthenticodeSignature .\haptics-tray.exe`.
