# IMGC Extractor v1.0.2

Fixes inspection of supported images whose dynamic filesystem has no label.

- When `game_dynamic` is absent, require a unique unlabelled ext partition containing root files `game.bin` and `db_primary.tar.gz` and root directories `golf` and `lib`.
- Support images with both dynamic and backup labels absent; retain the existing backup discovery rule.
- Reject missing, ambiguous or combined-role candidates instead of assuming a partition number.
- Record both dynamic and backup discovery in the interface, extraction log and report.
- Preserve existing labelled-image behavior, executable checks, original-byte preservation and drive-space preflight.

Validated with full synthetic extraction, missing/ambiguous layout regressions, explicit-label precedence, and successful inventory/executable verification of a reference image on another Windows PC. Full extraction of that reference image remains unverified.

Download the ZIP, extract it into a new folder, and run **IMGC Extractor.exe**. The release is self-contained; no separate runtime or Linux installation is needed. A SHA-256 sidecar is available as a separate asset. No disk images or application payloads are included.
