# GodDB Win 개발 계획

> 목표: macOS GodDB 1.3.5 (`zio0911/god_db`) 와 같은 기능을 가진 Windows 데스크톱 앱.
> 기능 기준: Mac 저장소의 `docs/FEATURES.md`. 이 문서의 [기능 대응표](#5-기능-대응표)가 진행 상황이다.
> 배포: **기능이 모두 갖춰진 뒤 한 번만** 릴리스한다. 그 전에는 마일스톤으로만 나눈다.

## 1. 구조

```
god_db_win/
├─ src/
│  ├─ GodDB.Core/          net10.0 (OS 무관) — 드라이버, 다이얼렉트, 로직 전부
│  │   ├─ Drivers/         MySQL · PostgreSQL · SQL Server · Redis 세션 (IDbSession)
│  │   ├─ Dialects/        인용 부호, LIMIT/TOP, 페이징, EXPLAIN, CREATE 문, 오류 번역
│  │   ├─ Sql/             문장 나누기, 위험 문장 판별, 자동완성, INSERT 힌트, 정렬·주석
│  │   ├─ Results/         페이징, 정렬, 편집(DML 생성), 복사, 내보내기, CSV 가져오기
│  │   ├─ Schema/          메타데이터, FK, 스키마 비교, ALTER 생성, 모델링, 다이어그램 배치
│  │   ├─ Redis/           RESP2, 클러스터 라우팅(CRC16), 센티널, 키 트리
│  │   ├─ Ai/              Ollama 클라이언트, 튜닝·어시스턴트·다이어그램 프롬프트 (스키마만)
│  │   ├─ Audit/           감사 로그(TSV, 해시 체인 + 기기 서명), 상태창 로그
│  │   ├─ Profiles/        접속 목록, 그룹, 내보내기/가져오기(AES-GCM), DataGrip 붙여넣기
│  │   ├─ Platform/        OS 기능 인터페이스 (비밀 저장소, 본인 확인, 기기 키, 경로, SSH)
│  │   └─ L10n/            tr("한국어") → 영어 표
│  ├─ GodDB.Platform.Windows/  net10.0-windows — Credential Manager, Windows Hello, TPM 키
│  └─ GodDB.App/           Avalonia UI (MVVM, CommunityToolkit.Mvvm)
├─ tests/
│  ├─ GodDB.Core.Tests/    xUnit 단위 테스트 (OS 무관)
│  └─ GodDB.Integration.Tests/  Docker 테스트 DB 대상 (환경변수 없으면 건너뜀)
└─ scripts/test-dbs.sh     Mac 저장소 것을 그대로 사용 (같은 포트·비밀번호)
```

- **Core 는 OS 를 모른다.** Mac 에서 `dotnet test` 로 Docker DB 통합 테스트까지 돌릴 수 있다.
- OS 전용 기능은 `Platform/` 인터페이스 뒤에 둔다. Windows 구현은 `GodDB.Platform.Windows`, Mac 에서 개발용으로 실행할 때는 메모리 구현(비밀번호는 앱을 끌 때까지만)을 쓴다.
- UI 는 Mac 화면을 그대로 옮기되 Windows 관례를 따른다: `⌘` → `Ctrl`, 메뉴는 창 안 메뉴 막대, 설정은 `Ctrl+,`.
- 다국어: Mac 의 `L10nEnglish*.swift` 표를 스크립트로 JSON 으로 옮겨 그대로 재사용한다. 누락 검사 테스트도 같은 규칙으로.

## 2. 라이브러리

| 용도 | 라이브러리 | 라이선스 | 비고 |
|---|---|---|---|
| 런타임 | .NET 10 (LTS, 2028-11 까지 지원) | MIT | self-contained 로 배포 → PC 에 .NET 설치 불필요 |
| UI | Avalonia 11.x | MIT | Mac 에서도 실행돼 화면 확인 가능 |
| SQL 편집기 | AvaloniaEdit | MIT | 하이라이트, 자동완성 창, INSERT 힌트(인라인 요소) |
| 결과 표 | Avalonia DataGrid (필요하면 TreeDataGrid) | MIT | 가상화. 20만 행 성능은 측정 후 결정 |
| MySQL | MySqlConnector | MIT | `CALL` 결과 집합, 취소(KILL QUERY) 지원 |
| PostgreSQL | Npgsql | PostgreSQL | 취소 지원 |
| SQL Server | Microsoft.Data.SqlClient | MIT | FreeTDS 불필요. **Windows 통합 인증** 가능 |
| Redis | 직접 구현 (RESP2, Mac 과 동일) | — | Mac 처럼 파이프라인·클러스터·센티널을 직접 다루기 위해. StackExchange.Redis 는 Pub/Sub·Workbench 임의 명령·MOVED 처리 방식이 Mac 동작과 달라 쓰지 않는다 |
| SSH 터널 | Windows 내장 OpenSSH `ssh.exe` (Mac 과 같은 방식) | — | `~/.ssh/config`, ssh-agent, known_hosts 그대로. 없으면 SSH.NET 으로 대체 (아래 4절) |
| Excel 내보내기 | 직접 작성 (스트리밍 XML + ZIP) | — | Mac 과 같은 방식, 큰 결과를 메모리에 올리지 않음 |
| MVVM | CommunityToolkit.Mvvm | MIT | |
| 자동 업데이트 | Velopack | MIT | 아래 3절 |
| 테스트 | xUnit | Apache-2.0 | |

`THIRD_PARTY_NOTICES.md` 와 설정 > 라이선스 화면에 위 목록을 넣는다.

## 3. Mac 전용 기능의 Windows 대응

| Mac | Windows | 비고 |
|---|---|---|
| 키체인 (서비스 `GodDB`, 계정 = 접속 ID) | **Windows 자격 증명 관리자** (`CredWrite/CredRead`, 대상 `GodDB:<접속 ID>`, 로컬 컴퓨터 범위) | 사용자 계정에 묶여 저장, `profiles.json` 에는 절대 안 씀 |
| Touch ID / Mac 로그인 암호로 저장된 비밀번호 보기 | **Windows Hello** (`UserConsentVerifier`: 지문·얼굴·PIN). Hello 가 없으면 **Windows 자격 증명 입력창**(`CredUIPromptForWindowsCredentials`)으로 로그인 암호 확인 | 매번 확인, 감사 로그에 "저장된 비밀번호 보기" |
| Secure Enclave 기기 키 (감사 로그 서명) | **TPM** 에 만든 ECDSA P-256 키 (CNG `Microsoft Platform Crypto Provider`, 꺼낼 수 없음). TPM 이 없으면 DPAPI 로 보호한 소프트웨어 키 | 서명 형식은 Mac 과 같게 (같은 검사 방식) |
| `~/Library/Application Support/GodDB/` | `%APPDATA%\GodDB\` (profiles.json, models, diagram-chats, history) | |
| `~/Library/Logs/GodDB/` (권한 600) | `%LOCALAPPDATA%\GodDB\Logs\` (ACL: 현재 사용자만) | |
| `/usr/bin/ssh` | `C:\Windows\System32\OpenSSH\ssh.exe` (Windows 10 1809+ 기본 포함) | 같은 옵션 사용. 미설치 PC 는 "선택적 기능 > OpenSSH 클라이언트" 안내 |
| FreeTDS (DB-Library) | Microsoft.Data.SqlClient | ANSI 세션 설정은 기본값이 이미 표준이라 별도 처리 불필요 (확인 테스트는 둠) |
| Sparkle + appcast.xml (서버 없음) | **Velopack** + GitHub 릴리스 (서버 없음) | 자동 / 알림(기본) / 수동 3가지 모드 그대로. 사용자 폴더에 설치돼 관리자 권한 불필요 |
| Developer ID 서명 + Apple 공증 | **Authenticode 코드 서명** | **인증서 구매가 필요 — 결정 필요** (아래) |
| EdDSA 업데이트 서명 | Velopack 패키지 해시 + Authenticode | |
| 크래시 보고서 (DiagnosticReports) | 앱이 처리 안 된 예외를 `%LOCALAPPDATA%\GodDB\Crashes\` 에 기록 → 문제 보고에 최근 3개 요약 | |
| 메뉴 막대 앱 메뉴 | 창 안 메뉴 막대 (파일 · 편집 · 데이터베이스 · 보기 · 도움말) | |
| DMG | Setup.exe (Velopack) + 포터블 zip | |

### 코드 서명 — 결정 필요
서명이 없으면 처음 실행할 때 SmartScreen "Windows 의 PC 보호" 경고가 뜬다 (추가 정보 → 실행 으로 넘어갈 수는 있음).

| 방법 | 비용(대략) | 비고 |
|---|---|---|
| A. 서명 없이 배포 | 0 | 동료 몇 명 대상이면 README 안내로 충분할 수 있음 |
| B. Azure Trusted Signing (Artifact Signing) | 월 약 $10 | GitHub Actions 에서 바로 서명. 개인은 신원 확인 조건이 있음 |
| C. OV/EV 코드 서명 인증서 구매 | 연 수십만 원 | 요즘은 하드웨어 토큰/클라우드 HSM 필수라 CI 연동이 번거로움 |

릴리스 워크플로는 **서명 단계를 선택 사항으로** 만들어 두고 (비밀 값이 있으면 서명), 결정은 릴리스 전에 받는다.

## 4. Windows 에서 더 할 수 있는 것

- **SQL Server Windows 통합 인증**: 접속 편집에 "인증: SQL 인증 / Windows 인증". Windows 인증이면 사용자·비밀번호 칸을 숨기고 `Integrated Security=true`. Mac 에서는 테스트할 수 없어 CI(Windows)에서 LocalDB 가 있으면 그걸로 확인한다.
- (선택) 비밀번호 방식 SSH: `ssh.exe` 대신 SSH.NET 을 쓰면 가능. Mac 과 동작을 맞추는 게 우선이라 **기본은 하지 않음**, 원하면 추가.

## 5. 기능 대응표

상태: ✅ 완료(구현 + 테스트) · ⛔ Windows 에서 불가(사유)
"테스트" 칸: U = 단위 테스트(화면은 헤드리스), I = Docker 테스트 DB 통합 테스트, M = Mac 에서 Avalonia 화면 확인, W = Windows CI 에서 확인.

| # | FEATURES.md 절 | Windows 구현 | 테스트 | 상태 |
|---|---|---|---|---|
| 0 | 지원 DB 와 DB 별 차이 | 다이얼렉트 3종 + Redis, 오류 번역(SQLSTATE · 오류 번호 · 소켓), 문장 나누기(DELIMITER · GO · $$ · CRLF) | U, I, W | ✅ |
| 1 | 화면 구성 | 사이드바 · 본문 · 결과 · 행 상세 · 상태창, 경계 드래그 | U(헤드리스), M 예정 | ✅ |
| 2 | 접속 관리 | 여러 접속 동시 연결, 연결 테스트, localhost IPv4→IPv6, 우클릭 메뉴, 더블클릭 | U, I | ✅ |
| 3 | 비밀번호 저장 | 자격 증명 관리자, 눈 버튼, Windows Hello → 로그인 암호 확인 | U, W(자격 증명 관리자) | ✅ (Hello 실기 미확인) |
| 4 | SSH 터널 | Windows 내장 OpenSSH `ssh.exe -N -L`, 15초 제한, ssh 오류 그대로 | U | ✅ (실기 미확인) |
| 5 | 읽기전용 모드 | 앱 검사 + 서버 세션 설정 (MySQL 1792 확인) | U, I | ✅ |
| 6 | 스키마 브라우저 | 트리, 컬럼·인덱스, 우클릭 메뉴, 검색, 새로고침, 여러 개 선택 | U, I | ✅ |
| 7 | 쿼리 편집기 | AvaloniaEdit: 하이라이트, Tab 자동완성, 결과 탭, 무한 스크롤(20만 행), 큰 값, 트랜잭션, 취소, LF 유지 · CRLF 저장 복원 | U, I | ✅ (한글 IME 실기 미확인) |
| 8 | 결과 표와 행 상세 | 페이징, 내보내기 6종(Excel 직접 작성), 복사 형식, 빠른 필터, 파일 이름 KST | U, I | ✅ |
| 9 | 실행 계획 (EXPLAIN) | 3종 + 주의사항 + 트리 보기 + AI 튜닝 연결 | U, I | ✅ |
| 10 | 테이블 수정 (ALTER) | 트리 + 속성 + 미리보기 + 검증 + 실행 확인 | U, I | ✅ |
| 11 | 인덱스 보기 | 사이드바 폴더, 인덱스 상세 | I | ✅ |
| 12 | 뷰 / 프로시저 / 함수 / 트리거 | 정의 보기, CALL/EXEC 틀 | I | ✅ |
| 13 | 스키마 비교 | 보정 SQL (DROP 없음), 자동 실행 없음 | U, I | ✅ |
| 14 | 상태창, 감사 로그, 문제 보고 | 해시 체인 + TPM(없으면 DPAPI) 서명, GitHub 이슈 미리 채우기, 크래시 기록 | U, W(DPAPI) | ✅ (TPM 실기 미확인) |
| 15 | 단축키 | ⌘→Ctrl, ⇧→Shift, ⌥→Alt | U(헤드리스) | ✅ |
| 16 | 저장 위치 | %APPDATA%\GodDB, %LOCALAPPDATA%\GodDB\Logs (본인만 접근) | U | ✅ |
| 17 | 알려진 제약 | Windows 판으로 다시 작성 (FEATURES.md) | — | ✅ |
| 19 | 설정 (언어 · 라이선스) | 즉시 언어 전환, 오픈소스 목록, THIRD_PARTY_NOTICES.md | U | ✅ |
| 20 | 쿼리 튜닝 (로컬 Ollama) | 스트리밍, 생각/본문 분리, 전/후 계획 비교, 보낸 정보 보기 | U, I(Ollama 수동) | ✅ |
| 21 | 결과 데이터 편집 | PK 기준 DML, 한 트랜잭션(열린 트랜잭션 합류), 바이너리 | U, I | ✅ |
| 22 | 이름 변경 · 삭제 · 생성문 새로고침, 헤더 정렬, hover |  | U, I | ✅ |
| 23 | ER 다이어그램 (+ AI 분석, 대화 기록) | 별도 창, 직접 그리기, 애니메이션, PNG 2배 | U | ✅ (PNG 내용 실기 미확인) |
| 24 | 접속 내보내기 · 가져오기, DataGrip 붙여넣기 | Mac 과 같은 파일 형식 (AES-GCM) | U | ✅ |
| 25 | Redis | 키 트리, 타입별 편집, Workbench, Pub/Sub, Streams, 서버 정보, Cluster, Sentinel | U, I | ✅ |
| 26 | INSERT 컬럼 힌트 | 편집기 인라인 요소 (항상 켬, Mac 과 같음) | U | ✅ |
| 27 | 모델링 | Mac 과 같은 .goddbmodel, 리버스 엔지니어링, DB와 비교, SQL 추출 | U | ✅ |
| 28 | AI 어시스턴트 | 쿼리 · 모델링 모드, 데이터 행 미전송, 물리명 적용 | U | ✅ |
| 29 | 접속 그룹 | 접기/펼치기, 끌어 놓기, 내보내기 포함 | U | ✅ |
| 30 | 시작 화면 · 자동 업데이트 · 새로운 기능 | 시작 화면, Velopack(자동/알림/수동), 변경 이력 창 | U | ✅ (설치 · 업데이트 실기 미확인) |
| 18 | 변경 이력 | Windows 판 1.0.0 부터 | — | ✅ |
| — | 1.3.x 추가 기능 | 실수 방지 확인, 취소 · 시간 제한, 히스토리 Ctrl+Y, 탭 자동 저장, 접속 환경 색, 복사 형식 · 빠른 필터, 주석 · 정렬, .sql 파일, 빠른 찾기 Ctrl+O, DELIMITER/GO, CSV 가져오기(CP949 포함) | U, I | ✅ |

> 테스트 수 (Mac, Docker 테스트 DB 포함): 420개 중 409 통과, 11개 건너뜀 (성능 측정 · Windows 전용 · Ollama). Windows CI(windows-latest) 통과.
> "실기 미확인" 은 기능은 구현했지만 실제 Windows PC 가 없어 사람이 직접 확인하지 못한 것 (7절).

## 6. 진행 방식

- 저장소: `zio0911/god_db_win` (공개). 라벨은 `god_db` 와 같게, 마일스톤은 내부용으로 나눈다:
  1. `M1 기반` — 솔루션 구조, CI, Core 다이얼렉트·SQL 텍스트·드라이버 4종, 접속/비밀번호/SSH
  2. `M2 쿼리` — 편집기, 실행, 결과 표, 페이징, 편집, 내보내기, 트랜잭션, 히스토리
  3. `M3 스키마` — 브라우저, ALTER, 비교, 다이어그램, 모델링
  4. `M4 Redis`
  5. `M5 AI · 로그 · 마무리` — Ollama 기능, 감사 로그, 문제 보고, 설정, 업데이트, 시작 화면
  6. `v1.0.0` — 릴리스 (사용자 승인 후)
- 작업 흐름: 첫 릴리스 전(초기 개발)에는 브랜치(`feat/core-foundation`)에 커밋·푸시만 하고 PR #4 를 마지막에 한 번 머지한다. 첫 릴리스 이후에는 이슈 → 브랜치 → PR(`Closes #n`) → CI 통과 → 사용자 확인 후 squash merge.
- CI (`pull_request` 에서만):
  - `windows-latest`: 빌드, 단위 테스트, Windows 전용 코드(자격 증명 관리자 등) 테스트, self-contained publish 확인.
  - (추가 제안) `ubuntu-latest` + 서비스 컨테이너: Docker DB 통합 테스트. Windows 러너에서는 Linux 컨테이너를 못 띄우기 때문.
- 릴리스 (`v*` 태그 푸시): `release` 환경 승인 대기 → 빌드 → (선택) 서명 → Velopack 패키지 → GitHub 릴리스. **사용자가 말하기 전에는 태그를 만들지 않는다.**
- 매 푸시 전 회사 정보 점검(grep).

## 7. 확인할 수 없는 것 (Windows PC 없음)

Mac 에서 Avalonia 로 화면을 돌려 보고, Windows 는 CI 로만 확인한다. 다음은 **실제 Windows PC 에서 확인하지 못한 채** 릴리스된다:

- Windows Hello / 자격 증명 입력창 실제 동작 (CI 러너엔 Hello 없음, 화면 상호작용 불가)
- TPM 키 생성 (러너에 TPM 이 없으면 소프트웨어 키 경로만 확인)
- Windows 인증 SQL Server 접속 (도메인 환경은 확인 불가, LocalDB 로 로컬 계정만)
- Velopack 설치 · 자동 업데이트의 실제 흐름, SmartScreen 경고 모양
- `ssh.exe` 터널 (Mac 에서는 같은 옵션의 `/usr/bin/ssh` 로 확인)
- 고해상도(125%/150% 배율) 화면, Windows 글꼴·한글 입력기(IME)에서의 편집기 동작, 단축키 충돌
- 실제 Windows 에서의 화면 전체 모양 (Mac 의 Avalonia 화면과 거의 같지만 글꼴·창 테두리가 다름)

릴리스 전에 동료 한 명에게 위 목록으로 확인을 부탁하는 것을 권한다.
