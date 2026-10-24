# Publishes the Haptics Everywhere sample as self-contained, single-file zips (no .NET install needed).
#   .\scripts\publish.ps1              -> publish\HapticsEverywhere-<version>-win-arm64.zip and -win-x64.zip
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = '0.1.1'
$out = Join-Path $root 'publish'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
foreach ($rid in 'win-arm64', 'win-x64') {
    $stage = Join-Path $out "stage-$rid"
    foreach ($proj in 'src\Haptics.Tray\Haptics.Tray.csproj', 'samples\MinimalBackgroundHaptics\MinimalBackgroundHaptics.csproj') {
        dotnet publish (Join-Path $root $proj) -c Release -r $rid --self-contained true -o $stage --nologo -v quiet `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    @"
Haptics Everywhere $version ($rid) — sample

haptics-tray.exe
  Run it, wiggle your haptic mouse (e.g. Surface Mouse 2nd Edition) over the little box once, then use Windows.
  You'll feel a tick on things you can DO: buttons, tabs, ribbon and menu commands, checkboxes, links, sliders,
  taskbar buttons. Right-click the tray icon for intensity, exclude app, status, Off, Quit.

minimal-background-haptics.exe
  The ~150-line core trick on its own: wiggle the mouse over the window, then move it in any app and feel a
  tick every ~150 px.

Requires Windows 11 with the haptics API (build 26100+; tested on 28000) and a haptic mouse.
Passive only: never moves, clicks, or focuses anything; no keyboard; nothing leaves your PC.
Source and evidence: https://github.com/shanselman/haptic-outside-labs
"@ | Set-Content (Join-Path $stage 'README.txt')
    Get-ChildItem $stage -Filter *.pdb | Remove-Item
    $zip = Join-Path $out "HapticsEverywhere-$version-$rid.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    Write-Host "$zip  ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}
