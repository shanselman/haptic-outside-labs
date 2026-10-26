# Publishes the Haptics Everywhere sample as self-contained, single-file zips (no .NET install needed).
#   .\scripts\publish.ps1                          -> publish\HapticsEverywhere-<version>-win-arm64.zip and -win-x64.zip
#   .\scripts\publish.ps1 -Version 0.3.0 -NoZip    -> publish\stage-<rid> only (the release workflow signs, then zips)
param(
    [string]$Version = ([xml](Get-Content (Join-Path (Split-Path -Parent $PSScriptRoot) 'Directory.Build.props'))).Project.PropertyGroup.Version,
    [switch]$NoZip
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = $Version
$out = Join-Path $root 'publish'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
foreach ($rid in 'win-arm64', 'win-x64') {
    $stage = Join-Path $out "stage-$rid"
    foreach ($proj in 'src\Haptics.Tray\Haptics.Tray.csproj', 'src\Haptics.Lab\Haptics.Lab.csproj', 'samples\MinimalBackgroundHaptics\MinimalBackgroundHaptics.csproj') {
        dotnet publish (Join-Path $root $proj) -c Release -r $rid --self-contained true -o $stage --nologo -v quiet `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
            -p:Version=$version
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    @"
Haptics Everywhere $version ($rid) — sample

haptics-tray.exe   (the app)
  Run it, wiggle your haptic mouse and/or touchpad over the little box once, then use Windows.
  Land on something you can DO (button, menu command, checkbox, link, taskbar button) -> light tick.
  Big ribbon buttons -> firmer click. New toolbar / ribbon group / tab or option -> detent.
  Sliding off the end of a toolbar or tab strip -> edge bump. Flying past things stays quiet.
  Right-click the tray icon for Style (Quiet / Focused / Everything), Intensity, Exclude app, Status, Off, Quit.

haptics-lab.exe    (the Showcase)
  Feel each sensation, try them on a demo surface, see why each tick happened (or didn't), and blind-compare
  the styles. Quit the tray (or untick "Engine on") so only one of them is driving your mouse.

minimal-background-haptics.exe
  The ~150-line core trick on its own: wiggle the mouse over the window, then move it in any app and feel a
  tick every ~150 px.

Requires Windows 11 with the haptics API (build 26100+; tested on 28000) and a haptic mouse or touchpad.
Passive only: never moves, clicks, or focuses anything; no keyboard; nothing leaves your PC.
Source and evidence: https://github.com/shanselman/haptic-outside-labs
"@ | Set-Content (Join-Path $stage 'README.txt')
    Get-ChildItem $stage -Filter *.pdb | Remove-Item
    if ($NoZip) {
        continue
    }

    $zip = Join-Path $out "HapticsEverywhere-$version-$rid.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    Write-Host "$zip  ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}
