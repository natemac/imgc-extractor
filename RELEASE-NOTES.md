# IMGC Extractor v1.0.1

Fixes inspection of supported images whose backup filesystem has no label.

- When `db_backup` is absent, locate the unique unlabelled ext partition containing a root `pg_backup` directory.
- Reject missing or ambiguous backup candidates instead of assuming a partition number.
- Show detected partitions and record backup discovery in the extraction log and report.
- Retain existing labelled-image behavior, executable checks, original preservation and drive-space preflight.

Validated with full synthetic extraction, missing/ambiguous-backup regressions, and successful inventory/executable verification of a reference image on another Windows PC. Full extraction of that reference image remains unverified.

Download the ZIP, extract it into a new folder, and run **IMGC Extractor.exe**. The release is self-contained; no separate runtime or Linux installation is needed. A SHA-256 sidecar is available as a separate asset. No disk images or application payloads are included.
