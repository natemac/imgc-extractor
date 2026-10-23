"""Development only: run under Linux with mkfs.ext3; the released app needs no Linux.
Creates tiny synthetic GPT/ext3 and IMGC images, never mounts a filesystem.
"""
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import uuid
import zlib

out = Path(sys.argv[1]).resolve()
out.mkdir(parents=True, exist_ok=True)

def literal(data):
    n = len(data)
    if n < 4:
        raise ValueError("fixture blocks must be >=4 bytes")
    if n <= 18:
        code = bytes([n - 3])
    else:
        remaining = n - 18
        count, end = divmod(remaining - 1, 255)
        code = b'\0' * (count + 1) + bytes([end + 1])
    return code + data + b'\x11\0\0'

def imgc(raw, destination):
    header = bytearray(4096)
    app = b'HDD Raw Copy Tool'
    header[0] = len(app)
    header[1:1+len(app)] = app
    struct.pack_into('<QQ', header, 0x500, raw.stat().st_size // 512, 512)
    with raw.open('rb') as src, destination.open('wb') as dst:
        dst.write(header)
        while data := src.read(64 * 1024):
            if not any(data):
                dst.write(b'omg!' + struct.pack('<IQ', 16, len(data)))
            else:
                n = len(data)
                prefix = struct.pack('<HH', (n & 0x7fff) | 0x8000, n >> 15) if n > 0x7fff else struct.pack('<H', n)
                packed = prefix + literal(data)
                dst.write(b'lol!' + struct.pack('<I', 8 + len(packed)) + packed)

def generate(kind):
    with tempfile.TemporaryDirectory(prefix='imgc-fixture-') as temp:
        base = Path(temp)
        contents = {
            'game_static': {'golf/shared.txt': b'static wins\n', 'golf/static.dat': bytes(range(256)) * 400,
                            'assetlist.txt': b'assets\n', 'dynamic_backup/dynamic.tar.gz': b'backup fixture\n'},
            'game_dynamic': {'golf/shared.txt': b'dynamic loses\n', 'golf/dynamic.dat': b'addition\n',
                             'game.bin': b'not executable\n', 'db_primary.tar.gz': b'database fixture\n', 'lib/libtest.so.1': b'library fixture\n'},
            'db_backup': {'pg_backup/backup.dat': b'postgres fixture\n'},
        }
        if kind == 'case-collision': contents['game_static']['golf/STATIC.dat'] = b'bad collision'
        if kind == 'missing-file': del contents['game_dynamic']['game.bin']
        if kind == 'wrapped': contents['game_dynamic']['game.bin'] = Path(sys.argv[2]).read_bytes()
        # Deliberately use p1=static, p2=backup, p3=dynamic, unlike the real p3/p5/p7.
        labels = ['game_static', 'db_backup', 'game_dynamic']
        if kind == 'missing-label': labels[1] = 'other_backup'
        if kind in ('unlabelled-backup', 'ambiguous-backup', 'missing-backup'): labels[1] = ''
        if kind == 'ambiguous-backup': labels.append('')
        if kind == 'missing-backup': contents['db_backup'] = {'unrelated/data.txt': b'no backup here'}
        partition_size = (64 if kind == 'wrapped' else 16) * 1024 * 1024
        raw = out / f'{kind}.img'
        disk_size = partition_size * len(labels) + 4 * 1024 * 1024
        entries = bytearray(128 * 128)
        with raw.open('wb') as disk:
            disk.truncate(disk_size)
            mbr = bytearray(512)
            mbr[446+4] = 0xee
            struct.pack_into('<II', mbr, 446+8, 1, disk_size // 512 - 1)
            mbr[510:] = b'\x55\xaa'
            disk.seek(0); disk.write(mbr)
            for i, label in enumerate(labels):
                root = base / f'partition-{i}'; root.mkdir()
                role = 'db_backup' if label in ('other_backup', '') else label
                for relative, data in contents[role].items():
                    f = root / relative; f.parent.mkdir(parents=True, exist_ok=True); f.write_bytes(data)
                if label == 'game_dynamic': (root / 'lib/libtest.so').symlink_to('libtest.so.1')
                fs = base / f'partition-{i}.ext3'
                with fs.open('wb') as f: f.truncate(partition_size)
                subprocess.run(['mkfs.ext3', '-q', '-F', '-L', label, '-d', str(root), str(fs)], check=True)
                start = 1024 * 1024 + i * partition_size
                disk.seek(start); disk.write(fs.read_bytes())
                entry = memoryview(entries)[i*128:(i+1)*128]
                entry[:16] = uuid.UUID('0fc63daf-8483-4772-8e79-3d69d8477de4').bytes_le
                entry[16:32] = uuid.uuid4().bytes_le
                struct.pack_into('<QQ', entry, 32, start // 512, (start + partition_size) // 512 - 1)
            header = bytearray(512)
            header[:8] = b'EFI PART'
            struct.pack_into('<III', header, 8, 0x10000, 92, 0)
            struct.pack_into('<QQQQ', header, 24, 1, disk_size // 512 - 1, 34, disk_size // 512 - 34)
            header[56:72] = uuid.uuid4().bytes_le
            struct.pack_into('<QIII', header, 72, 2, 128, 128, zlib.crc32(entries))
            struct.pack_into('<I', header, 16, zlib.crc32(header[:92]))
            disk.seek(512); disk.write(header); disk.write(entries)
        imgc(raw, out / f'{kind}.imgc')
        if kind in ('valid', 'wrapped'):
            expected = dict(contents['game_static'])
            for p, data in contents['game_dynamic'].items(): expected.setdefault(p, data)
            expected.update(contents['db_backup'])
            expected['lib/libtest.so'] = expected['lib/libtest.so.1']
            (out / ('expected-wrapped.json' if kind == 'wrapped' else 'expected.json')).write_text(json.dumps({p: hashlib.sha256(data).hexdigest().upper() for p, data in expected.items()}, indent=2))

for variant in (['wrapped'] if len(sys.argv) > 2 else ['valid', 'case-collision', 'missing-file', 'missing-label', 'unlabelled-backup', 'ambiguous-backup', 'missing-backup']):
    generate(variant)
    print('Created', variant, flush=True)
