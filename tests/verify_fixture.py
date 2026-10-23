import hashlib
import json
from pathlib import Path
import sys

folder, expected_file = map(Path, sys.argv[1:])
expected = json.loads(expected_file.read_text())
for relative, digest in expected.items():
    actual = hashlib.sha256((folder / relative).read_bytes()).hexdigest().upper()
    assert actual == digest, relative
report = json.loads((folder / 'Extraction-report.json').read_text())
assert report['status'] == 'Verified'
assert len(report['verifiedFiles']) == len(expected)
assert len(report['collisions']) == 1
assert report['collisions'][0]['identical'] is False
assert report['collisions'][0]['path'] == 'golf\\shared.txt'
assert any(f['MaterializedLink'] for f in report['verifiedFiles'])
if 'game.bin.encrypted' in expected:
    preparation = report['executablePreparation']
    assert preparation['Status'] == 'Prepared'
    assert preparation['InputSha256'] == expected['game.bin.encrypted']
    assert preparation['OutputSha256'] == expected['game.bin']
    assert report['spaceBeforeExtraction']['FinalFileBytes'] == report['plannedBytes']
    assert report['spaceBeforeExtraction']['ExtraTemporaryBytes'] == 0
print(f'PASS: all {len(expected)} extracted fixture files, static precedence, symlink materialization and verification report')
