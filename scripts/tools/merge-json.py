#!/usr/bin/env python3
"""번역표(en.json) 병합 충돌 해결: 양쪽 문장을 모두 합친다.  python3 scripts/tools/merge-json.py <파일>"""
import json, subprocess, sys
p = sys.argv[1]
d = {}
for stage in (2, 3):
    d.update(json.loads(subprocess.run(['git', 'show', f':{stage}:{p}'], capture_output=True, text=True, check=True).stdout))
open(p, 'w', encoding='utf-8').write(json.dumps(dict(sorted(d.items())), ensure_ascii=False, indent=1) + '\n')
print(len(d), '문장')
