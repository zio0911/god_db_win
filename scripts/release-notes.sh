#!/usr/bin/env bash
# docs/FEATURES.md 의 "### X.Y.Z" 제목 아래 변경 이력을 잘라 GitHub 릴리스 노트(변경 사항 + 설치 안내)로 출력한다.
#   ./scripts/release-notes.sh 1.0.0            → 표준 출력
# 해당 버전 제목이 없으면 변경 사항 없이 설치 안내만 출력한다.
set -euo pipefail
cd "$(dirname "$0")/.."
V="${1:-}"; V="${V#v}"
[[ -n "$V" ]] || { echo "사용법: $0 <버전>" >&2; exit 1; }

SECTION=$(awk -v h="### $V" '
  $0 == h { on = 1; next }
  on && (/^### / || /^## /) { exit }
  on { print }
' docs/FEATURES.md 2>/dev/null | sed -e '/./,$!d' | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}' || true)

if [[ -n "$SECTION" ]]; then
  printf '%s\n' "## 변경 사항" "$SECTION" ""
fi
cat <<'MD'
## 설치
`GodDB-win-Setup.exe` 를 받아 실행하세요. 관리자 권한 없이 사용자 폴더에 설치되고, 시작 메뉴에 GodDB 가 생겨요.
설치하지 않고 쓰려면 `GodDB-win-Portable.zip` 을 풀어 `GodDB.exe` 를 실행하세요.
처음 실행할 때 "Windows 의 PC 보호" 창이 뜨면 **추가 정보 → 실행** 을 누르세요.
이미 설치돼 있으면 앱이 자동으로 업데이트를 알려줘요.
MD
