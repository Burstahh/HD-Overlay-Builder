# HD Overlay Builder

**HD Overlay Builder** is a Windows tool for building and managing Crimson Desert HD texture overlay packs.

Original HD texture project by **Khain**.  
.NET port / builder by **Burstahh**.

This repository contains the public v1.4 source code with the final UI polish, safe default matching behavior, registry/state backup hardening, Manage Installed Builds manifest rebase fix, archive-folder token detection, and the mixed-DPI multi-monitor window-state hotfix.

## What it does

HD Overlay Builder helps package `.dds` texture files into Crimson Desert overlay folders while keeping the install reversible and manageable.

Main workflows:

- **Easy Apply**: quick/safe build using Safe Primary matching.
- **Update Existing Build**: applies only missing or changed targets when possible.
- **Manual Build / Advanced**: manual filter/options workflow.
- **Manage Installed Builds**: view/remove selected managed installs.
- **Smart Overlay Hold**: temporarily move managed overlays out of the game folder.
- **Release Hold + Reapply**: restore held overlays and replay PATHC state.
- **Relink Overlays After Game Update**: relink existing overlays against updated game metadata.
- **Remove Current Build**: restore base metadata and remove managed overlays.

Overlay folders intentionally remain named:

```text
HD01, HD02, HD03, ...
```

## Current release status

**Version:** v1.4

Final validation passed against the public v1.4 build, including:

- Fresh Easy Apply with 28,069 DDS files.
- Same-folder Update Existing skip path.
- Small-folder update creating HD07 and hotfixing one existing HD01 target.
- Manage Installed Builds / Remove Selected Build manifest rebase.
- Smart Overlay Hold / Release Hold + Reapply.
- Relink.
- In-game boot/world test with HD01-HD07 active.
- Final Remove Current Build cleanup.
- Mixed-DPI/multi-monitor window-state hotfix testing.

## Requirements

- Windows 10/11 x64.
- .NET SDK capable of building `net8.0-windows` WinForms projects.
  - The build scripts were tested with modern .NET SDK installs.
- PowerShell, used by the helper build scripts for ZIP/font preparation.

## Quick build

From a normal Windows command prompt:

```bat
BUILD_WINDOWS.bat
```

Output:

```text
publish\win-x64\HD Overlay Builder.exe
```

The build is self-contained and single-file by default.

## Release build/package

To build the public EXE ZIP and source ZIP locally:

```bat
BUILD_RELEASE_WINDOWS.bat
```

Output folder:

```text
dist\
```

## AV/Nexus variant builds

To produce alternate packaging variants for antivirus/Nexus testing:

```bat
BUILD_AV_VARIANTS_WINDOWS.bat
```

Output folder:

```text
av_variants\
```

## Optional embedded Noto Sans fonts

The app can run without embedded Noto Sans font files. If the embedded font files are absent, it falls back to installed Noto Sans or Segoe UI.

For a local embedded-font build, place `Noto_Sans.zip` beside `PREPARE_NOTO_SANS_WINDOWS.bat`, then run:

```bat
PREPARE_NOTO_SANS_WINDOWS.bat
BUILD_WINDOWS.bat
```

Expected prepared files:

```text
Resources\Fonts\NotoSans-Regular.ttf
Resources\Fonts\NotoSans-Bold.ttf
Resources\Fonts\NotoSans-Italic.ttf
Resources\Fonts\NotoSans-BoldItalic.ttf
Resources\Fonts\OFL_NOTO_SANS.txt
```

Noto Sans is distributed by Google under the SIL Open Font License. Keep the OFL license text with any font files you redistribute.

## Window reset options

If the app ever opens offscreen because of monitor/DPI changes, use one of these reset methods:

```bat
HD Overlay Builder.exe --reset-window
HD Overlay Builder.exe --reset-window-state
```

You can also hold **Shift** while launching the app to reset saved window placement.

v1.4 includes additional startup validation and mixed-DPI multi-monitor maximize/restore handling so the window should stay on the monitor where it is currently located.

## Archive-folder source detection

The builder supports known archive IDs in top-level DDS source folder names.

Supported archive tokens:

```text
0000, 0001, 0002, 0007, 0009, 0012, 0015
```

Examples:

```text
0000          -> archive scope 0000
UHD0000       -> archive scope 0000
CDHDTR0001    -> archive scope 0001
SomePack0015  -> archive scope 0015
```

This detection is whitelist-based and avoids broad random 4-digit matching.

## Managed data folder

New v1.4 managed state uses:

```text
HDOverlayBuilder
```

Legacy folders such as `HDUpscaleOverlayBuilder` are still detected/handled for compatibility. The app should not strand older managed builds.

## Safety notes

- Safe Primary matching remains the default for Easy Apply and Update Existing Build.
- Legacy duplicate fan-out is advanced/manual opt-in only.
- Build/update/relink/remove behavior is designed to be reversible through the managed backup/restore flow.
- Remove Current Build restores base metadata and removes managed `HD##` overlays for the current build.

## Repository layout

```text
Core/                         Core overlay/build/update/relink logic
Resources/                    App icon/preview/font placeholder resources
AppSetting.cs                 App setting model
Localization.cs               UI/log localization strings
MainForm.cs                   WinForms UI and window-state handling
Program.cs                    App entry point
HDOverlayBuilder.csproj       .NET project file
BUILD_WINDOWS.bat             Quick local build
BUILD_RELEASE_WINDOWS.bat     Release/package build
BUILD_AV_VARIANTS_WINDOWS.bat Alternate package variants
PREPARE_NOTO_SANS_WINDOWS.bat Optional embedded Noto Sans prep
```

## Development notes

Please keep these areas stable unless intentionally working on a specific fix:

- Safe Primary matching behavior.
- Easy Apply / Update Existing Build behavior.
- Overlay folder naming (`HD01`, `HD02`, `HD03`, etc.).
- PAZ/PAMT/PAPGT/PATHC handling.
- Manifest/schema behavior.
- Relink behavior.
- Remove Current Build behavior.
- Smart Overlay Hold / Release Hold + Reapply behavior.
- Backup/restore safety.

## License

HD Overlay Builder source code is released under the MIT License.

Noto Sans is licensed separately under the SIL Open Font License. See `THIRD_PARTY_NOTICES.md` and `Resources/Fonts/OFL_NOTO_SANS.txt`.
