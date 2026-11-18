#!/usr/bin/env python3
"""Mac 판 GodDB 의 영어 번역표(L10nEnglish*.swift)를 Windows 판 JSON 으로 옮긴다.
   python3 scripts/tools/import-l10n.py <GodDB 저장소>/Sources/GodDBCore src/GodDB.Core/L10n/en.mac.json
- 키(한국어)와 값(영어) 모두 Mac 단축키 기호를 Windows 표기로 바꾼다 (⇧⌘L → Ctrl+Shift+L).
- Windows 판에서 새로 생긴 문장은 en.json 에 직접 추가한다 (en.json 이 en.mac.json 보다 우선).
"""
import json, re, sys, pathlib

src, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
pair = re.compile(r'^\s*"((?:[^"\\]|\\.)*)"\s*:\s*"((?:[^"\\]|\\.)*)"\s*,?\s*$', re.M)

def unescape(s):
    return re.sub(r'\\(.)', lambda m: {'n': '\n', 't': '\t', '"': '"', '\\': '\\', "'": "'", '0': '\0'}.get(m.group(1), m.group(0)), s)

KEYS = {'↩': 'Enter', '⌫': 'Backspace', '⎋': 'Esc', '⇥': 'Tab', '⌦': 'Delete'}
shortcut = re.compile(r'([⌃⌥⇧⌘]+)(↩|⌫|⎋|⇥|⌦|Tab|Space|Enter|[A-Za-z0-9,./\[\]=\-+;])?')

def win(s):
    def rep(m):
        mods, key = m.group(1), m.group(2) or ''
        parts = []
        if '⌘' in mods or '⌃' in mods: parts.append('Ctrl')
        if '⇧' in mods: parts.append('Shift')
        if '⌥' in mods: parts.append('Alt')
        k = KEYS.get(key, key.upper() if len(key) == 1 else key)
        return '+'.join(parts + ([k] if k else []))
    s = shortcut.sub(rep, s)
    # 단독 기호 (예: "↩ 확정")
    for k, v in KEYS.items():
        s = s.replace(k, v)
    return s

table = {}
for f in sorted(src.glob('L10nEnglish*.swift')):
    for k, v in pair.findall(f.read_text(encoding='utf-8')):
        table[win(unescape(k))] = win(unescape(v))
out.write_text(json.dumps(dict(sorted(table.items())), ensure_ascii=False, indent=1) + '\n', encoding='utf-8')
print(f'{len(table)} 문장 → {out}')
