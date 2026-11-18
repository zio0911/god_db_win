# GodDB for Windows

MySQL · PostgreSQL · SQL Server · Redis 를 다루는 Windows 데스크톱 DB 클라이언트.
macOS 판 [GodDB](https://github.com/zio0911/god_db) 와 같은 기능을 C# / .NET 10 / Avalonia 로 다시 만든다.

> 개발 중. 기능이 모두 갖춰진 뒤 첫 릴리스를 낸다. 진행 상황은 [docs/PLAN.md](docs/PLAN.md) 의 기능 대응표.

## 개발

```bash
./scripts/test.sh                 # 빌드 + 단위 테스트
./scripts/test-dbs.sh up          # Docker 테스트 DB (MySQL · PostgreSQL · SQL Server · Redis)
./scripts/test-dbs.sh test        # 통합 테스트 포함
dotnet run --project src/GodDB.App -f net10.0   # Mac / Linux 에서 화면 확인 (Windows 전용 기능은 대체 구현)
```

## 라이선스
MIT — [LICENSE](LICENSE)
