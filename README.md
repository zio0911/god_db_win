# GodDB for Windows

MySQL · PostgreSQL · SQL Server · Redis 를 한 앱에서 다루는 Windows 데스크톱 DB 클라이언트.
macOS 판 [GodDB](https://github.com/zio0911/god_db) 와 같은 기능을 C# / .NET 10 / Avalonia 로 다시 만들었고, SQL Server **Windows 통합 인증**을 더했다.

- 여러 접속 동시 연결, SSH 터널, 읽기전용 모드, 접속 환경(개발 · 스테이징 · 운영) 표시
- 쿼리 편집기 (Tab 자동완성, 결과 탭, 페이징, 결과 편집, 트랜잭션, 실행 계획), 내보내기 6종 (CSV · TSV · Excel · JSON · SQL INSERT · Markdown)
- 테이블 수정(ALTER), 스키마 비교, ER 다이어그램, 모델링, Redis (키 트리 · Workbench · Pub/Sub · Streams · Cluster · Sentinel)
- 로컬 Ollama(이 PC 만)로 쿼리 튜닝 · AI 어시스턴트 (구조 · 코멘트 · 통계 · SQL 만 보내고 데이터 행은 보내지 않음. 예외로 **AI 데이터 찾기**는 후보 컬럼 값 몇 개를 로컬 Ollama 에만 보냄), 감사 로그
- 테이블 설명 (AI 참고): 소스를 본 AI 에이전트에게 받은 테이블 · 컬럼 · 코드 값 설명 JSON 을 DB 마다 넣어 두면 AI 어시스턴트 · 데이터 찾기가 참고 ([양식](docs/table-descriptions.md))

자세한 기능은 [docs/FEATURES.md](docs/FEATURES.md).

## 설치

[Releases](https://github.com/zio0911/god_db_win/releases) 에서 받는다.

- **설치판**: `GodDB-win-Setup.exe` 를 실행한다. 관리자 권한 없이 사용자 폴더에 설치되고 시작 메뉴에 GodDB 가 생긴다. 새 버전은 앱이 알려 주거나 자동으로 설치한다 (설정 > 업데이트).
- **포터블**: `GodDB-win-Portable.zip` 을 풀어 `GodDB.exe` 를 실행한다.

> 지금 배포 파일은 코드 서명이 되어 있지 않다. 처음 실행할 때 "Windows 의 PC 보호" (SmartScreen) 창이 뜨면 **추가 정보 → 실행** 을 누른다.

## 요구 사항

- Windows 10 1809 이상 / Windows 11, x64 (.NET 은 앱에 들어 있어 따로 설치하지 않아도 된다)
- 대상 DB: MySQL 5.7 / 8.x, PostgreSQL 12+, SQL Server 2016+, Redis 6+
- SSH 터널: Windows 내장 OpenSSH 클라이언트 (`ssh.exe`)
- AI 기능 (선택): [Ollama](https://ollama.com) 를 이 PC 에 설치 (추천 모델: 메모리 48GB 이상 `qwen3.8:27b`, 24GB `qwen3:14b`)

## 개발

```bash
./scripts/test.sh                 # 빌드 + 단위 테스트
./scripts/test-dbs.sh up          # Docker 테스트 DB (MySQL · PostgreSQL · SQL Server · Redis)
./scripts/test-dbs.sh test        # 통합 테스트 포함
dotnet run --project src/GodDB.App -f net10.0   # Mac / Linux 에서 화면 확인 (Windows 전용 기능은 대체 구현)
./scripts/release-notes.sh 1.3.7.1  # docs/FEATURES.md 변경 이력으로 릴리스 노트 만들기
```

개발 계획과 포팅 규칙은 [docs/PLAN.md](docs/PLAN.md), [docs/PORTING.md](docs/PORTING.md).

## 라이선스
MIT — [LICENSE](LICENSE). 사용한 오픈소스는 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
