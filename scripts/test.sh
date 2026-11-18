#!/usr/bin/env bash
# 단위 테스트 (+ 환경변수가 있으면 통합 테스트)
# 통합 테스트: scripts/test-dbs.sh up 으로 컨테이너를 띄운 뒤 scripts/test-dbs.sh test
set -euo pipefail
cd "$(dirname "$0")/.."
# 테스트는 한국어 메시지를 기준으로 비교한다
export GODDB_LANG=ko
command -v dotnet >/dev/null || export PATH="$HOME/.dotnet:$PATH"
dotnet test --solution GodDB.slnx "$@"
