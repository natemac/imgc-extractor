# IMGC Extractor

A portable Windows app for reading HDD Raw Copy Tool `.imgc` images directly, assembling a merged content folder from selected Linux partitions, and verifying the result. It also accepts decompressed `.img` files.

**[Download the Windows x64 release](https://github.com/natemac/imgc-extractor/releases/latest)**

## Use

1. Download the release ZIP and extract it to a normal folder.
2. Run **IMGC Extractor.exe**.
3. Choose the source image, destination drive/folder, and a new output folder name.
4. Click **Inspect image**. Review the supported-layout result, executable status, and drive-space estimate.
5. Click **Extract and prepare**, or **Extract only (unsupported)** when preparation is unavailable.
6. Wait for completion. The output includes `Extraction-report.json` and `Extraction.log`.

Windows 10/11 x64. No installation, administrator privileges, Linux environment, Python, or separate .NET installation is needed to run the release. Use a local NTFS or exFAT destination and allow at least 1 GB of available RAM. A mapped network drive can supply the input. Keep the source connected and the PC awake during extraction.

## Space and verification

- No full intermediate IMG or partition copy is written. The compressed reader uses a bounded 256 MiB memory cache.
- Inspection calculates final file data, including both executable versions when preparation is supported.
- The app displays the selected drive's actual free space, its allocation allowance, a 1 GiB safety margin, and the total required. It checks space again before copying and during the operation.
- The in-progress folder and prepared executable are renamed in place. Extra temporary image storage beyond the final data budget is **zero**.
- Allow approximately 200 MB on the system drive for the application and bundled runtime, separately from the content budget. Copying the source image onto the destination drive requires additional space for that source copy.
- Each copied file is reread and verified with SHA-256. Conflicting static/dynamic files are recorded with both hashes.

The source is opened read-only. Existing output folders are refused. Failed or cancelled work remains in a uniquely named `.partial-*` folder with diagnostics; it is not published as completed output. Version 1 has no resume function.

## Supported layout

This is a **specific merged-content extraction profile**, not a universal disk-image browser. The image must contain a valid primary GPT and supported ext-family filesystems labelled `game_static`, `game_dynamic`, and `db_backup`. Partition numbers are discovered rather than assumed.

| Output | Source |
| --- | --- |
| `golf/` | Static content, then dynamic paths that do not already exist |
| `assetlist.txt`, `dynamic_backup/` | `game_static` |
| `game.bin`, `db_primary.tar.gz`, `lib/` | `game_dynamic` |
| `pg_backup/` | `db_backup` |

Static content wins exact-path collisions. Case-only Windows path collisions, missing required files, unsupported filesystem features, invalid filenames, and malformed image structures stop extraction. Regular-file symlinks are materialized within the virtual filesystem. Directory symlinks and special/device files are unsupported. Linux ownership, permissions, and directory timestamps are not recreated. The output is a content folder, not a bootable clone.

## Executable checking and preparation

The built-in checker recognizes supported product-14 executable containers, revisions **2 and 3**, up to **128 MiB**. During inspection it validates the decoded ELF32 i386 header, internal SHA1, and embedded DSA signature. The embedded signature checks container consistency; it does not independently authenticate a publisher.

During extraction, supported containers are checked again from the verified staging copy. The exact original is preserved as `game.bin.encrypted`, and the decoded, disk-verified executable becomes `game.bin`. Existing encrypted backups are checked for conflicting identities.

Already-ELF inputs are identified and copied without decryption. Unsupported containers are explicitly marked **extraction only**. Damaged supported containers fail preparation. The receipt distinguishes `Prepared`, `AlreadyElf`, `Unsupported`, and `PreparationFailed`.

Checks establish file integrity and supported-format preparation. They do not establish compatibility with a particular runtime or successful execution. Unused compressed-image blocks are not fully audited.

## Build and test

Requires the .NET 8 SDK and NuGet access to build:

```powershell
dotnet build src/App/App.csproj -c Release
dotnet run --project src/Check/Check.csproj -c Release -- self-test
./Build.ps1
```

`Build.ps1` creates a self-contained Windows x64 ZIP and SHA-256 sidecar under `artifacts/`, refusing to overwrite an existing package. The bundled runtime version is pinned in the script.

Integration fixtures contain only synthetic data. Generate them on Linux with Python 3 and `mkfs.ext3`:

```sh
python3 tests/make_fixtures.py tests/generated
```

On Windows with a development WSL distribution, `python tests/build_fixtures_windows.py` generates and transfers those fixtures. It defaults to `Ubuntu`; set `IMGC_TEST_WSL_DISTRO` to select another distribution. WSL is needed only for this developer fixture-generation option.

Then run on Windows:

```powershell
./tests/Run-Tests.ps1
```

Optional preparation regression checks accept your own original and known decoded files using `-WrappedExecutable` and `-KnownElf`. Generated images, executable samples, logs, and private reference data are excluded from version control and release packages.

## License and acknowledgements

Project code is MIT licensed; third-party code and redistributables retain their own terms. The IMGC format and modified LZO implementation are based on [shiz's unimgc](https://github.com/shizmob/unimgc), under the WTFPL. Read-only ext access uses [DiscUtils](https://github.com/DiscUtils/DiscUtils), under MIT. See [third-party notices](licenses/ATTRIBUTION.txt).

No disk images, application payloads, or original loader binaries are distributed.
