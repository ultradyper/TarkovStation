#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
"""Create consistent SQLite backups, including committed WAL data, while SS14 runs."""
import argparse
import datetime
import pathlib
import sqlite3


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('data', type=pathlib.Path, help='SS14 data directory')
    parser.add_argument('destination', type=pathlib.Path)
    args = parser.parse_args()
    sources = sorted(args.data.glob('*.db'))
    if not sources:
        parser.error('No SQLite databases found in the supplied data directory')
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
    target = args.destination / stamp
    target.mkdir(parents=True, exist_ok=False)
    target.chmod(0o700)
    for source in sources:
        output = target / source.name
        with sqlite3.connect(source.resolve().as_uri() + '?mode=ro', uri=True) as original:
            with sqlite3.connect(output) as backup:
                original.backup(backup, pages=256)
                result = backup.execute('PRAGMA integrity_check').fetchone()[0]
                if result != 'ok':
                    raise RuntimeError(f'Backup integrity failed: {source.name}: {result}')
        output.chmod(0o600)
        print(f'{source.name}: {output.stat().st_size} bytes, integrity ok')
    print(f'Backup: {target.resolve()}')


if __name__ == '__main__':
    main()
