#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
"""Check own Fluent message uniqueness and ru/en key and variable parity."""
import pathlib
import re
import sys

repo = pathlib.Path(__file__).resolve().parents[3]
errors = []
locales = {}
for language in ('ru-RU', 'en-US'):
    messages = {}
    for path in sorted((repo / 'Resources' / 'Locale' / language / '_TarkovStation').rglob('*.ftl')):
        current = None
        for number, line in enumerate(path.read_text().splitlines(), 1):
            match = re.match(r'^([\w-]+)\s*=', line)
            if match:
                current = match[1]
                if current in messages:
                    errors.append(f'{language}: duplicate {current} at {path.name}:{number}')
                messages[current] = set()
            elif line and not line[0].isspace() and not line.startswith('#'):
                current = None
            if current:
                messages[current].update(re.findall(r'\$([\w]+)', line))
    locales[language] = messages
ru, en = locales['ru-RU'], locales['en-US']
for key in sorted(ru.keys() ^ en.keys()):
    errors.append(f'Message missing in one language: {key}')
for key in sorted(ru.keys() & en.keys()):
    if ru[key] != en[key]:
        errors.append(f'Variable mismatch: {key}: ru={sorted(ru[key])}, en={sorted(en[key])}')
if errors:
    print('\n'.join(errors))
    sys.exit(1)
print(f'{len(ru)} unique messages per language; keys and variables match.')
