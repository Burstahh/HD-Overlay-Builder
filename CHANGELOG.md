# Changelog

This file is intended for users, forkers, and developers who need to understand the behavioral and safety changes between public source revisions.

## v1.4.2

v1.4.2 preserves the validated v1.4 DDS matching/build backend while adding storage throttling and several post-game-update safety fixes.

### Slow / External Drive Safe Mode

- Added a three-lane Performance Mode workflow:
  - Auto / Recommended
  - Balanced
  - Slow / External Drive Safe Mode
- Safe Mode favors stable sequential I/O over aggressive parallel preparation.
- Safe Mode uses a single-buffer PAZ pipeline and lower PAZ preparation concurrency.
- Auto detection checks the selected game/output location and DDS source location before applying.
- Detection was made conservative after fixed internal NVMe storage could false-trigger immediately after a large write-heavy build.
- High-confidence removable/network/slow-output signals can automatically select Safe Mode.
- Manual performance selections remain available as overrides.
- Relink also surfaces slow/external-storage guidance because metadata replay can be affected by unreliable external-drive I/O.

No matching, routing, overlay naming, manifest ownership, or texture-selection behavior was intentionally changed by the performance-mode work.

### FIX05: Source-independent Hold/Release and safer Relink

The original DDS source folder is no longer a required replay dependency after managed overlays have been built.

Implementation details:

- Easy Apply and Update Existing Build cache required PATHC replay/header metadata in managed overlay records.
- Managed records preserve the owning `HD##` overlay folder.
- Smart Overlay Hold captures a pre-hold PATHC replay snapshot before restoring the managed base.
- Release Hold + Reapply can recover replay metadata from:
  - manifest header cache,
  - saved PATHC snapshots,
  - installed/held PAZ payload headers where necessary.
- Relink uses the same managed replay sources before considering any legacy source-folder fallback.
- New managed builds should perform Hold/Release and Relink with zero original DDS source reads.
- Existing older builds retain guarded fallback/recovery behavior where enough installed metadata is available.

Relink failure handling was tightened:

- Validate installed overlay folders and metadata before committing.
- Stage PATHC/meta work before replacing live files.
- Preserve active manifests, registry/state, and installed `HD##` folders on failure.
- Restore the pre-Relink metadata backup if the transaction cannot complete.
- Log the exact failed target and failure class instead of unregistering the managed installation.

### FIX06: Relink duplicate-manifest canonicalization

Historical manifests can legitimately overlap after Update Existing Build hotfixes or overlay reconciliation. Relink must not treat those historical duplicates as unresolved packed entries.

Canonicalization behavior:

- Normalize and deduplicate managed matches by target path.
- Normalize and deduplicate overlay records by target path plus owning `HD##` folder.
- Prefer the newest applicable manifest record when historical manifests overlap.
- Report unique managed targets rather than raw historical match totals.
- Log the number of duplicate historical records collapsed.
- Do not classify duplicate historical ownership as "packed without editable metadata."
- Require every unique managed target to resolve before committing PATHC/PAPGT changes.
- Abort and restore the pre-Relink metadata if any managed target remains unresolved.

Validated FIX06 corpus:

```text
28,365 historical match records -> 28,364 unique managed targets
33,362 historical overlay entries -> 28,364 canonical entries
4,999 duplicate historical records collapsed
28,344 updated
20 unchanged
0 packed without editable metadata
0 skipped
28,364 / 28,364 unique managed targets resolved
source-folder fallback: 0
```

Both original DDS source folders remained renamed/unavailable during validation. The game booted successfully and textures were correct in-game with no rainbow/corrupted textures.

The exact counts above describe the validation installation and are not hardcoded expectations for every user build.

### FIX07: Rebase the active managed base after successful Relink

Before FIX07, a successful Relink could use current verified metadata but leave Smart Hold and Remove Current Build pointing at the original pre-update backup. Restoring that obsolete backup after a later game patch could prevent the game from booting even while all `HD##` overlays were held.

FIX07 behavior:

1. Validate and canonicalize all managed overlay targets using the passing FIX06 replay logic.
2. Preserve the current pre-Relink metadata as the candidate new base/underlay.
3. Apply Relink changes transactionally.
4. Only after full Relink success, atomically register the candidate underlay as the active managed base.
5. Keep the previous valid base pointer/history available for rollback where applicable.
6. On Relink failure, restore pre-Relink metadata and keep the previous valid active-base pointer.

Important distinction:

- The rebased managed base is the current **non-HDOB underlay before overlay replay**.
- It may be freshly verified stock metadata or valid DMM/other-manager metadata.
- It is not the final overlay-applied metadata.

Smart Overlay Hold and Remove Current Build now log the exact active base backup/revision they restore. Successful Relink logs the previous and new active base revisions/paths.

Existing managed installations attempt a safe migration/recovery path when a latest successful Relink basis is available. Operations should stop rather than silently restoring a known-obsolete base when safe recovery is impossible.

### Easy Apply workflow confirmation

- Added a confirmation dialog when Easy Apply detects an existing managed build.
- The dialog states that Easy Apply is intended for first installation or a complete clean rebuild.
- Users adding, replacing, or updating textures are directed to **Update Existing Build**.
- Cancel returns without changing managed state.
- Continuing logs that the user explicitly confirmed Easy Apply over an existing build.
- Detection is based on actual managed state, not only the selected DDS source path.

### Crimson Desert v1.13.00 validation

Validated post-update workflows include:

- Smart Overlay Hold followed by Release Hold + Reapply.
- Source-independent replay with both original DDS source folders unavailable.
- Relink against freshly verified stock metadata.
- FIX06 duplicate-manifest canonicalization.
- FIX07 active-base rebasing.
- Correct game boot and correct in-game textures after replay/relink.

### Compatibility and invariants for forks

v1.4.2 intentionally preserves:

- Safe Primary as the Easy Apply / Update Existing Build default.
- Legacy duplicate fan-out as advanced/manual opt-in only.
- `HD01`, `HD02`, `HD03`, etc. overlay naming.
- Existing archive routing and whitelist folder-token detection.
- Existing PAZ/PAMT/PAPGT/PATHC formats and behavior except the narrow replay/base-revision additions required for safety.
- Partial-source overwrite protection.
- Manage Installed Builds semantics.
- Remove Current Build semantics, with the active-base safety correction described above.
- Existing UI layout, DPI scaling, and window-state behavior.

## v1.4.1

### Stale metadata restore guard

- Added protection against restoring an old game-patch metadata backup over a newer Crimson Desert installation.
- New managed backups capture `meta/0.paver` and write backup identity information.
- Smart Overlay Hold, Remove Selected Build, and Remove Current Build validate the backup basis before restoration.
- Unsafe stale restoration is blocked or routed through the current managed-base recovery path.
- The immediate recovery for metadata already overwritten by an older build remains Steam Verify Integrity.

### Packaging cleanup

- Public version fields and UI badge were updated to v1.4.1.
- Normal publish output was changed to:

```text
publish\HD Overlay Builder\
```

- `README.md` is copied beside the EXE.
- The normal end-user publish folder does not retain a separate `Resources` directory.

## v1.4

v1.4 is the validated public DDS baseline and rollback point from which the later safety releases were developed.

Validated v1.4 behavior included:

- Fresh Easy Apply with 28,069 DDS files and Safe Primary matching.
- Unchanged Update Existing Build skip behavior.
- Small-folder update, HD07 creation, and existing HD01 hotfix behavior.
- Repeated unchanged updates.
- Manage Installed Builds selected-build removal and active-manifest rebase.
- Big-folder reconciliation and small-folder reapply.
- Smart Overlay Hold and Release Hold + Reapply.
- Relink.
- In-game boot/world testing.
- Final Remove Current Build cleanup.
- Recursive registry/state backup correction.
- Mixed-DPI/multi-monitor Window-State Hotfix 04.
- GitHub source/README/license preparation.

The v1.4 DDS backend remains the compatibility baseline. Later releases should be treated as narrow safety/performance extensions rather than a redesign of matching or overlay creation.
