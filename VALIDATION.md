# Validation scope

The original implementation was exercised with synthetic GPT/ext3 images and private, read-only reference samples. The public source and release exclude all private samples, their paths and identifiers, and local logs.

The regression suite covers:

- Modified LZO instructions, malformed inputs, Windows path validation and GPT CRC.
- Full decompressed-stream hash comparison and 1,000 random reads against a synthetic raw image.
- Complete extraction from synthetic IMGC and IMG files, with independently generated expected hashes.
- Label-based partition discovery using deliberately reordered partitions.
- Unlabelled backup discovery by directory contents, complete extraction and provenance reporting; missing and ambiguous backup rejection.
- Static/dynamic collision precedence and file-symlink materialization.
- Truncated images, bad GPT checksums, missing labels/files and case-only collisions.
- Cancellation, preservation of existing output folders, and selected-drive space accounting.
- Optional executable-preparation checks against user-supplied original/decoded pairs: identity, backup preservation, collision rejection, corruption, unsupported revisions and cancellation.

The Windows interface and self-contained packaging were also exercised. A user reported successful full extraction of a labelled reference image with v1.0.0. For v1.0.1, direct inspection on a second Windows PC confirmed the unlabelled backup layout, completed the file inventory and verified supported executable decoding. Automated full extraction of that large image has not been completed as part of this release validation. No extracted executable was run.

Passing these checks is not a claim of universal image support or runtime compatibility. The supported layout and preparation limits are described in the README.
