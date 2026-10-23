"""Bounded integration fixture: use the existing original executable, not the full arcade disk."""
from pathlib import Path
import hashlib
import json
import os
import subprocess
import sys
import uuid

here = Path(__file__).parent
wrapped, reference = map(Path, sys.argv[1:])
remote = '/tmp/imgc-wrapped-' + uuid.uuid4().hex
remote_input = remote + '.bin'
with wrapped.open('rb') as inp:
    subprocess.run(['wsl', '-d', os.environ.get('IMGC_TEST_WSL_DISTRO', 'Ubuntu'), '--exec', 'python3', '-c',
                    'import pathlib,sys; pathlib.Path(sys.argv[1]).write_bytes(sys.stdin.buffer.read())', remote_input], stdin=inp, check=True)
subprocess.run(['wsl', '-d', os.environ.get('IMGC_TEST_WSL_DISTRO', 'Ubuntu'), '--exec', 'python3', '-', remote, remote_input],
               input=(here / 'make_fixtures.py').read_bytes(), check=True)
target = here / 'generated'
for name in ['wrapped.imgc', 'expected-wrapped.json']:
    with (target / name).open('wb') as output:
        subprocess.run(['wsl', '-d', os.environ.get('IMGC_TEST_WSL_DISTRO', 'Ubuntu'), '--exec', 'cat', remote + '/' + name], stdout=output, check=True)
expected = json.loads((target / 'expected-wrapped.json').read_text())
expected['game.bin.encrypted'] = expected['game.bin']
expected['game.bin'] = hashlib.sha256(reference.read_bytes()).hexdigest().upper()
(target / 'expected-wrapped.json').write_text(json.dumps(expected, indent=2))
print('Bounded wrapped fixture ready; no arcade disk extracted.')
