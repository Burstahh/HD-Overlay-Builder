# HD Overlay Builder

**HD Overlay Builder** is a Windows tool for building and managing Crimson Desert HD texture overlay packs.

Original HD texture project by **Khain**.  
.NET port / builder by **Burstahh**.

This repository contains the public **v1.4.2** source. It is based on the fully validated v1.4 DDS release and adds post-update metadata safety, source-independent Hold/Release replay, transactional Relink recovery, duplicate-manifest canonicalization, managed base-meta rebasing, and storage throttling for slow/external drives.

See [`CHANGELOG.md`](CHANGELOG.md) for the developer-facing change history and validation details.

## What it does

HD Overlay Builder packages `.dds` texture files into Crimson Desert overlay folders while keeping the install reversible and manageable.

Main workflows:

- **Easy Apply**: first installation or intentional clean full rebuild using Safe Primary matching.
- **Update Existing Build**: add, replace, or update textures in a current managed build.
- **Manual Build / Advanced**: manual filter/options workflow.
- **Manage Installed Builds**: view or remove selected managed builds.
- **Smart Overlay Hold**: temporarily move managed overlays out of the game folder and restore the active managed base.
- **Release Hold + Reapply**: restore held overlays and replay their managed PATHC state.
- **Relink Overlays After Game Update**: relink installed overlays against the current game/mod-manager metadata.
- **Remove Current Build**: restore the active managed base and remove the current managed overlays.

Overlay folders intentionally remain named:

```text
HD01, HD02, HD03, ...
```

## Current release status

**Version:** v1.4.2  
**Validated game basis:** Crimson Desert v1.13.00

The public DDS workflow has been validated with:

- Fresh Easy Apply using 28,069 DDS files.
- Unchanged and incremental Update Existing Build paths.
- Small-folder updates, HD07 creation, and an existing-overlay hotfix.
- Manage Installed Builds removal/rebase behavior.
- Smart Overlay Hold and source-independent Release Hold + Reapply.
- Relink against freshly verified stock metadata with the original DDS source folders unavailable.
- Duplicate historical manifest canonicalization with every unique managed target resolved before commit.
- Successful in-game boot/world tests with correct textures and no rainbow/corrupted textures.
- Remove Current Build and stale-base restoration safety.
- Mixed-DPI and multi-monitor window-state handling.

## v1.4.2 highlights

### Slow / External Drive Safe Mode

Performance Mode includes **Slow / External Drive Safe Mode** for USB drives, external HDDs, network storage, or other slow locations.

Safe Mode reduces simultaneous disk pressure by favoring a single-buffer PAZ pipeline, fewer prep workers, and less aggressive read-ahead. **Auto / Recommended** remains the normal default and uses conservative storage detection so internal SSD/NVMe systems are not unnecessarily throttled.

### Source-independent Hold and Relink

After Easy Apply or Update Existing Build has created managed `HD##` overlays, the original extracted DDS source folder is not required for:

- Smart Overlay Hold
- Release Hold + Reapply
- Relink Overlays After Game Update

The source folder is still required when adding, replacing, or rebuilding texture payloads with Easy Apply or Update Existing Build.

### Safer Relink transactions

Relink validates and canonicalizes every unique managed target before committing current PATHC/PAPGT changes. Historical duplicate manifest ownership is collapsed, the newest applicable record is preferred, and unresolved targets abort the operation instead of leaving a partial managed install.

A failed Relink restores the pre-Relink metadata and preserves the active managed build, manifests, registry/state, and installed `HD##` folders.

### Managed base-meta rebasing

After a successful Relink, the current **pre-overlay underlay** becomes the new active managed base. This may be freshly verified stock metadata or valid mod-manager-adjusted metadata.

Future Smart Hold and Remove Current Build operations restore that rebased current base instead of the original metadata from an older game patch.

### Easy Apply workflow warning

When an active managed build already exists, Easy Apply now warns that it performs a clean full rebuild and directs incremental users to **Update Existing Build**.

## Recommended workflows

### First installation

1. Apply regular non-texture mods through DMM or another mod manager.
2. Select the Crimson Desert game folder.
3. Select the DDS texture source folder.
4. Use **Easy Apply**.

### Adding or replacing textures

Use **Update Existing Build**. Do not use Easy Apply unless a complete clean rebuild is intended.

### Game update with a modded setup

When possible, the safest heavily modded workflow is:

1. Smart Overlay Hold.
2. Update, sort, or remount regular mod-manager content.
3. Release Hold + Reapply.

### Game already updated

Use **Relink Overlays After Game Update** when the game updated before the overlays could be held and the rest of the installed mod state is known to be correct.

Texture-only users can generally use Relink as the straightforward post-update recovery option.

## Requirements

- Windows 10/11 x64.
- A .NET SDK capable of building `net8.0-windows` WinForms projects.
- PowerShell for ZIP and optional font preparation helpers.

## Quick build

From a normal Windows command prompt:

```bat
BUILD_WINDOWS.bat
```

Output:

```text
publish\HD Overlay Builder\HD Overlay Builder.exe
publish\HD Overlay Builder\README.md
```

The normal published end-user folder does not retain a separate `Resources` folder.

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

The repository includes `Noto_Sans.zip`. The build scripts call the font preparation helper automatically before publishing.

To prepare the fonts manually:

```bat
PREPARE_NOTO_SANS_WINDOWS.bat
```

Prepared local files:

```text
Resources\Fonts\NotoSans-Regular.ttf
Resources\Fonts\NotoSans-Bold.ttf
Resources\Fonts\NotoSans-Italic.ttf
Resources\Fonts\NotoSans-BoldItalic.ttf
Resources\Fonts\OFL_NOTO_SANS.txt
```

Generated `.ttf` files are build-time resources and are intentionally excluded from the clean repository/source ZIP layout.

Noto Sans is distributed by Google under the SIL Open Font License. Keep the OFL license text with any font files you redistribute.

## Window reset options

If the app opens offscreen after monitor/DPI changes, use either reset argument:

```bat
HD Overlay Builder.exe --reset-window
HD Overlay Builder.exe --reset-window-state
```

You can also hold **Shift** while launching to reset saved window placement.

## Archive-folder source detection

Top-level DDS source folder names can scope known archive IDs.

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

Detection is whitelist-based and does not broadly match arbitrary four-digit text.

## Managed data folder

Current managed state uses:

```text
HDOverlayBuilder
```

Legacy folders such as `HDUpscaleOverlayBuilder` remain supported for compatibility so older managed builds are not stranded.

## Safety notes

- Safe Primary matching remains the default for Easy Apply and Update Existing Build.
- Legacy duplicate fan-out remains advanced/manual opt-in only.
- Overlay folder naming remains `HD01`, `HD02`, `HD03`, etc.
- Relink only commits after all unique managed targets resolve.
- Hold/Release and Relink can replay existing managed overlays without the original DDS source folder.
- Successful Relink rebases the active managed underlay for later Hold/Remove operations.
- Backup, rollback, manifest, registry/state, PAMT, PAPGT, PATHC, and PAZ safety behavior should be preserved when developing forks.

## Repository layout

```text
Core/                         Core overlay/build/update/relink logic
Resources/                    App icon, preview, and font license placeholders
AppSetting.cs                 App setting model
Localization.cs               UI/log localization strings
MainForm.cs                   WinForms UI and window-state handling
Program.cs                    App entry point
HDOverlayBuilder.csproj       .NET project file
BUILD_WINDOWS.bat             Quick local build
BUILD_RELEASE_WINDOWS.bat     Release/package build
BUILD_AV_VARIANTS_WINDOWS.bat Alternate package variants
PREPARE_NOTO_SANS_WINDOWS.bat Embedded Noto Sans preparation
Noto_Sans.zip                 Source font archive used by the prep script
CHANGELOG.md                  Developer/forker change history and validation notes
```

## Development notes

The following behavior is considered stable and should not be changed unintentionally:

- Safe Primary matching and archive routing.
- Easy Apply and Update Existing Build semantics.
- Overlay naming and split behavior.
- PAZ/PAMT/PAPGT/PATHC handling.
- Manifest trust/fallback and partial-source overwrite protection.
- FIX05 source-independent Hold/Release replay.
- FIX06 Relink canonicalization and full-resolution-before-commit behavior.
- FIX07 managed base-meta rebasing and rollback behavior.
- Manage Installed Builds and Remove Current Build behavior.
- Window layout, DPI scaling, and window-state handling.

See [`CHANGELOG.md`](CHANGELOG.md) before modifying these areas.

## License

HD Overlay Builder source code is released under the MIT License.

Noto Sans is licensed separately under the SIL Open Font License. See `THIRD_PARTY_NOTICES.md` and `Resources/Fonts/OFL_NOTO_SANS.txt`.
