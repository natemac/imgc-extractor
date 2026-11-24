# IMGC Extractor v1.0.3

Supports content directory names beyond the original extraction profile.

- Discover an unlabelled dynamic partition using game.bin, db_primary.tar.gz and lib, without requiring a game-specific content directory.
- Merge content directories such as bowling, common and other names, preserving static-file precedence.
- Show candidate root entries to help diagnose unsupported layouts.
- Preserve ambiguity rejection, source read-only access, copied-file hash checks and drive-space preflight.

The complete synthetic regression suite passed, including arbitrary content names, merging, missing and ambiguous partitions, collision handling and byte verification. This release does not add revision-4 executable preparation, a universal partition browser or runtime compatibility. Layouts without the required game.bin/metadata structure remain unsupported. No game files, private research or cabinet adapters are included.

Download and extract the Windows x64 ZIP into a new folder, then run IMGC Extractor.exe. A SHA-256 sidecar is provided.
