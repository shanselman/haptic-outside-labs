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

1. Set `<Version>` in `Directory.Build.props` (for example `0.4.0`) and land it on `main`.
2. Tag and push: `git tag v0.4.0` then `git push origin v0.4.0`.
3. The workflow creates release `v0.4.0` with `HapticsEverywhere-0.4.0-win-arm64.zip` and `-win-x64.zip`.
4. Review the generated release notes against current behavior. In 0.4, automatic lookup can avoid the initial
   wiggle; the workflow's older template still describes manual priming. Include the validation checklist and
   keep unblinded operator checks distinct from formal physical evidence.
5. Download both zips and check executable versions/signatures and the bundled `LICENSE` and
   `THIRD-PARTY-NOTICES.txt`.

Release binaries take their version from the tag; local builds use `Directory.Build.props`. To get signed zips as a
build artifact without publishing a release, run `gh workflow run release.yml`. To check a signature, run
`Get-AuthenticodeSignature .\haptics-tray.exe`.

## Optional MSIX bundle

MSIX is an **additional, experimental distribution option**, not a replacement for the portable ZIPs.
Set the repository Actions variable `MSIX_PUBLISHER` to the **exact Subject distinguished name** on the
Azure Trusted Signing certificate used above to opt in. Do not guess it from the account/profile name.
For example, inspect `(Get-AuthenticodeSignature .\haptics-tray.exe).SignerCertificate.Subject` on a signed
release executable and confirm it against the certificate profile. Keep the package name and publisher
stable across releases; changing either creates a different package identity rather than an upgrade.
Leaving the variable unset preserves the existing ZIP-only release.

With the variable set, only pushed stable numeric tags (`vX.Y.Z` or `vX.Y.Z.W`) produce an MSIX bundle.
Prerelease tags (for example `v0.7.0-rc.1`) and manual `workflow_dispatch` runs, even when targeting a stable
tag, skip MSIX packaging, signing and verification. They still produce the same signed portable ZIPs.
No prerelease or CI suffix is stripped to create a package version, avoiding collisions with stable packages.

The workflow signs the executables first, packages both staging folders with
[`scripts/package-msix.ps1`](../scripts/package-msix.ps1), then signs and timestamps the bundle with the
same Trusted Signing profile and verifies its signature. The bundle contains both architectures; Windows
deploys the compatible one. The script reuses `assets/app.ico` for package logos, registers the tray and
Showcase in Start, and retains the minimal sample and license/notice files. No app logic changes.

### Why offer this alongside a ZIP?

| ZIP | MSIX bundle |
|---|---|
| Users choose ARM64 or x64 and find somewhere to extract it. | One download; App Installer selects the architecture and Windows manages the installation location. |
| Launch by finding an EXE or creating shortcuts manually. | Tray and Showcase get Start menu entries. |
| Updating means managing extracted copies and shortcuts. | A higher-version, signed package with the same identity can update the registered installation. |
| Removing means deleting the extracted files and any shortcuts. | A registered entry in Installed apps provides a standard uninstall for deployed package files. |
| Executables are signed individually. | The signed package also protects the manifest and packaged content against modification. |

This does **not** make the full-trust desktop app sandboxed, guarantee SmartScreen reputation, or enable
automatic updates. Automatic updates need an `.appinstaller` feed, Store distribution, or another deployment
service; none is added here. The combined download includes both architectures and is larger than either ZIP.
User data outside the managed package can remain after uninstall; do not promise complete settings cleanup.

### Local packaging

Requires Windows, PowerShell 7, and Windows SDK packaging tools (`MakeAppx.exe`). From the repository root:

```powershell
.\scripts\publish.ps1 -NoZip
.\scripts\package-msix.ps1 -Version 0.6.0 -Publisher 'CN=Your exact certificate subject'
# Result: publish\HapticsEverywhere-0.6.0.0.msixbundle (unsigned until explicitly signed)
```

For already-downloaded ZIPs, extract them into `stage-win-arm64` and `stage-win-x64` under a directory and pass
that directory with `-InputDirectory`; no rebuild is required. `-OutputDirectory` overrides the output location.
The input folders are copied, not modified. Three-part versions gain a `.0` revision; four-part numeric versions
are accepted, with each component at most 65535. Prerelease strings are rejected by the script.
Manual workflow runs do not produce MSIX bundles. Use increasing numeric versions for local upgrade testing.

The script deliberately does not create certificates, install anything, or change trust stores. Sign with a
certificate whose subject matches `-Publisher` exactly. A self-signed local test certificate must be explicitly
trusted on the test machine; do not ship it as if it were the public release signer.

Run `.\scripts\test-package-msix.ps1` for the same unsigned container/manifest smoke tests used in CI.
They use inert payload fixtures and check both architectures, version normalization, escaped publisher identity,
registered apps, preserved payloads, logos, and rejection of invalid versions or a missing license.
They also check the release workflow's stable-tag MSIX gate and unchanged ZIP version selection.

### Packaged-app validation before recommending it

Packaging/signature checks are not proof that the app works under package identity. Keep this opt-in until
these checks have been completed on **both x64 and ARM64**:

- Install on Windows 11 build 26100+; verify the publisher, architecture selection, and both Start menu entries.
- Launch the tray and Showcase, including tray-to-Showcase launch and the isolated `--uia-worker` child process.
- Check settings save/reload, real-device discovery/priming, haptics, and the same-user cooperating-app pipe
  from an unpackaged client. Do not count an API success or packaging test as physical haptic evidence.
- The current **Start with Windows** implementation writes an HKCU Run value pointing at the EXE. This PR
  does not add a package startup task or adapt that setting. Do not rely on it in the packaged build until its
  behavior across login, upgrade, and uninstall is verified; package registry virtualization and versioned
  install paths can change the result. A package-aware startup implementation is separate work.
- Install a higher version with the same identity, then uninstall. Check for stale startup entries, remaining
  settings, and coexistence with a portable copy; do not run two tray instances during a hardware comparison.
