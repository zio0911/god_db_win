# Mac → Windows 포팅 규칙

Mac 판 소스: `~/GodDB/Sources/GodDBCore` (로직), `~/GodDB/Sources/GodDB` (화면), `~/GodDB/Tests/GodDBCoreTests` (테스트).
동작 기준은 Mac 코드와 `~/GodDB/docs/FEATURES.md`. **동작을 바꾸지 말고 그대로 옮긴다.** Windows 에서 달라져야 하는 것만 바꾸고 주석에 이유를 적는다.

## 코드
- Core: `src/GodDB.Core/<영역>/파일.cs`, 네임스페이스 `GodDB.Core.<영역>` (최상위 공용 타입은 `GodDB.Core`).
  - 영역: `Sql`, `Dialects`, `Schema`, `Drivers`, `Results`, `Redis`, `Ai`, `Audit`, `Profiles`, `Modeling`, `Platform`, `L10n`.
- Swift `struct`/`enum` → C# `record`/`sealed class`/`enum`. associated value enum → `abstract record` + 하위 `record`.
- Swift 의 UTF-16 오프셋 = C# `string` 인덱스 (그대로 대응).
- 비동기: `async Task<T>` + `CancellationToken`. UI 스레드에 기대지 않는다 (Core 는 Avalonia 를 모름).
- 정규식은 가능하면 `[GeneratedRegex]`.
- OS 전용 기능은 `Platform/` 인터페이스로만 (자격 증명, 본인 확인, 기기 서명 키, 셸 열기). 구현은 `GodDB.Platform.Windows` 또는 App.

## 다국어
- 화면·메시지 문장은 Mac 과 **같은 한국어 문장**을 `Tr("…")` 로 감싼다 (`using static GodDB.Core.L10n;`). `%@` 자리는 `Tr("%@행", count)`.
- 영어는 `src/GodDB.Core/L10n/en.mac.json` (Mac 표를 `scripts/tools/import-l10n.py` 로 옮긴 것, 직접 고치지 않음) 에서 찾는다.
  - 단축키 기호는 Windows 표기로 바꿔 쓴다: `⌘T` → `Ctrl+T`, `⇧⌘L` → `Ctrl+Shift+L`, `⌥⌘L` → `Ctrl+Alt+L`, `↩` → `Enter` (번역표 키도 이미 바뀌어 있음).
  - Mac 전용 낱말이 들어간 문장(키체인, Touch ID, Finder, macOS …)은 Windows 낱말로 고친 새 한국어 문장을 쓰고, 영어를 `src/GodDB.Core/L10n/en.json` 에 추가한다.
- 테스트는 `GODDB_LANG=ko` 로 돈다 (한국어 문장 기준 비교).

## 테스트
- 단위: `tests/GodDB.Core.Tests/<Mac 테스트 파일 이름>Tests.cs` — Mac 테스트를 **하나도 빼지 않고** xUnit v3 로 옮긴다 (`[Fact]`, `Assert.*`).
- 통합(Docker DB): `tests/GodDB.Integration.Tests/` — 환경변수(`GODDB_IT_PORT`, `GODDB_IT_PASSWORD`, `GODDB_PG_PORT`, `GODDB_MSSQL_PORT`, `GODDB_REDIS_PORT`, `GODDB_REDIS_CLUSTER_PORT`, `GODDB_REDIS_SENTINEL_PORT`)가 없으면 `Assert.Skip(...)`. 포트·비밀번호는 `scripts/test-dbs.sh` 와 같다 (호스트 127.0.0.1).
- 실행: `./scripts/test.sh`, 통합 포함 `./scripts/test-dbs.sh test`. dotnet 은 `~/.dotnet/dotnet`.

## 공개 저장소
- 회사 호스트명·IP·계정·데이터를 코드·테스트·주석에 넣지 않는다. 예시는 `example.com`, `10.0.0.x`, `shop`, `tb_member` 처럼 일반적인 것만.
