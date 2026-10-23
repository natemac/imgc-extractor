# IMGC Extractor v1.0.0

Initial public release of the portable Windows x64 extractor with built-in executable checking and preparation.

- Direct IMGC and raw IMG reading without an intermediate disk image.
- Label-based GPT/ext partition discovery and deterministic content merging.
- Final/temporary space estimates, selected-drive free-space checks and safety margin.
- SHA-256 verification, collision reports, cancellation and preserved partial output.
- Supported executable-container preparation with original-byte preservation.
- Self-contained Windows executable; no separate runtime installation required.

Download the ZIP, extract it, and run **IMGC Extractor.exe**. A SHA-256 sidecar is included as a separate release asset.

This release supports the specific merged-content layout documented in the README. Unsupported executable formats remain extraction-only. Full large-disk extraction is not yet qualified; synthetic end-to-end extraction and bounded reference checks passed. No images or application payloads are included.
