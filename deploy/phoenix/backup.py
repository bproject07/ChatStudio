#!/usr/bin/env python3
"""Private PostgreSQL + credential backup; retain only this script's dated backups."""
import argparse
import datetime
import fcntl
import os
import pathlib
import re
import shutil
import subprocess
import tarfile

parser = argparse.ArgumentParser()
parser.add_argument('--directory', required=True)
parser.add_argument('--keep-days', type=int, default=7)
args = parser.parse_args()
if args.keep_days < 1:
    parser.error('keep-days must be positive')
root = pathlib.Path(args.directory).resolve()
if not (root / 'compose.yaml').is_file() or not (root / 'secrets/phoenix.env').is_file():
    raise SystemExit('Expected an initialized Phoenix deployment directory.')
os.umask(0o077)
backups = root / 'backups'
backups.mkdir(mode=0o700, exist_ok=True)
with (backups / '.backup.lock').open('w') as lock:
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')
    pending = backups / ('.pending-' + stamp)
    pending.mkdir(mode=0o700)
    with (pending / 'phoenix.dump').open('wb') as output:
        subprocess.run(['docker', 'compose', '-f', str(root / 'compose.yaml'), 'exec', '-T', 'db',
                        'pg_dump', '-U', 'phoenix', '-d', 'phoenix', '-Fc'], stdout=output, check=True)
    with tarfile.open(pending / 'configuration.tar.gz', 'w:gz') as archive:
        for name in ['compose.yaml', 'secrets']:
            archive.add(root / name, arcname=name)
    pending.rename(backups / stamp)
    cutoff = datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(days=args.keep_days)
    for item in backups.iterdir():
        if item.is_symlink() or not item.is_dir() or not re.fullmatch(r'\d{8}T\d{6}Z', item.name):
            continue
        created = datetime.datetime.strptime(item.name, '%Y%m%dT%H%M%SZ').replace(tzinfo=datetime.timezone.utc)
        if created < cutoff and item.resolve().parent == backups.resolve():
            shutil.rmtree(item)
    print('Phoenix database and private configuration backed up:', stamp)
