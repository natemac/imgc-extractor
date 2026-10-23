"""Developer fixture bridge for Windows with WSL; never required by the portable app."""
from pathlib import Path
import os
import subprocess
import uuid

here = Path(__file__).parent
remote = '/tmp/imgc-tests-' + uuid.uuid4().hex
subprocess.run(['wsl', '-d', os.environ.get('IMGC_TEST_WSL_DISTRO', 'Ubuntu'), '--exec', 'python3', '-', remote], input=(here / 'make_fixtures.py').read_bytes(), check=True)
target = here / 'generated'
target.mkdir(exist_ok=True)
for name in ['valid.img', 'valid.imgc', 'case-collision.imgc', 'missing-file.imgc', 'missing-label.imgc', 'unlabelled-backup.imgc', 'ambiguous-backup.imgc', 'missing-backup.imgc', 'expected.json']:
    with (target / name).open('wb') as stream:
        subprocess.run(['wsl', '-d', os.environ.get('IMGC_TEST_WSL_DISTRO', 'Ubuntu'), '--exec', 'cat', remote + '/' + name], stdout=stream, check=True)
print('Fixtures exported to', target)
