using System.Globalization;
using System.Net.Sockets;
using System.Text;
using GodDB.Core.Audit;
using GodDB.Core.Platform;

namespace GodDB.Core.Tests;

public class LogFileTests
{
    [Fact]
    public void WritesOneTabSeparatedLinePerAuditedAction()
    {
        using var dir = new TempDir();
        var signer = new SoftwareDeviceSigner();
        var file = new LogFile(dir.Path, retention: () => 0, signer: () => signer);
        var date = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000).LocalDateTime;
        file.Append(new LogEntry(LogLevel.Success, LogSource.Query, "실행 1행", Detail: "UPDATE t\nSET a = 1",
                                 Audit: new AuditRecord(AuditAction.Update, "dev/shop", "1행 · 3ms", "UPDATE t\nSET\ta = 1")) { Date = date });
        // 감사 대상이 아니면 파일에 남지 않는다 (AI 질문 내용, 화면 이동 등)
        file.Append(new LogEntry(LogLevel.Info, LogSource.Schema, "다이어그램 AI", Detail: "오늘 날씨 알려줘") { Date = date });
        file.Append(new LogEntry(LogLevel.Error, LogSource.Connection, "연결 실패",
                                 Audit: new AuditRecord(AuditAction.Connect, "dev", "실패: 비밀번호 오류")) { Date = date });
        file.Flush();
        var path = file.PathFor(date);
        Assert.StartsWith("goddb-audit-", Path.GetFileName(path));
        var data = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, data[..3]);
        var text = File.ReadAllText(path, Encoding.UTF8);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);   // 머리글 + 체인 + 2행
        Assert.Equal("시각\t사용자@PC\t구분\t대상\t결과\t내용\t무결성\t서명", lines[0]);
        Assert.Equal("체인", lines[1].Split('\t')[2]);
        var cols = lines[2].Split('\t');
        Assert.Equal(8, cols.Length);
        Assert.True(cols[2] == "수정" && cols[3] == "dev/shop" && cols[4] == "1행 · 3ms" && cols[5] == "UPDATE t SET a = 1");
        Assert.Equal(64, cols[6].Length);
        Assert.Equal("접속", lines[3].Split('\t')[2]);
        Assert.DoesNotContain("날씨", text);
        // Mac: 권한 600. Windows: 본인 계정만 허용하는 ACL
        Assert.True(UserOnlyFile.IsRestricted(path));
    }

    [Fact]
    public void RemovesFilesOlderThanKeepDays()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var old = dir.File("goddb-2000-01-01.log");
        File.WriteAllText(old, "x");
        var file = new LogFile(dir.Path, keepDays: 30, signer: () => null);
        file.Append(new LogEntry(LogLevel.Info, LogSource.App, "시작", Audit: new AuditRecord(AuditAction.AppStart)));
        file.Flush();
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(file.PathFor(DateTime.Now)));
    }

    [Fact]
    public void KeepsOldFilesByDefault()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var old = dir.File("goddb-2000-01-01.log");
        File.WriteAllText(old, "x");
        var file = new LogFile(dir.Path, retention: () => 0, signer: () => null);
        file.Append(new LogEntry(LogLevel.Info, LogSource.App, "시작", Audit: new AuditRecord(AuditAction.AppStart)));
        file.Flush();
        Assert.True(File.Exists(old));
    }
}

public class ClientInfoTests
{
    /// 이름 읽기는 네트워크 조회 없이 바로 끝나야 한다 (앱 시작이 멈추던 문제)
    [Fact]
    public void NamesAreQuickAndNonEmpty()
    {
        var start = DateTime.UtcNow;
        Assert.NotEmpty(ClientInfo.ComputerName);
        Assert.NotEmpty(ClientInfo.HostName);
        Assert.DoesNotContain(" ", ClientInfo.Identity);
        Assert.True((DateTime.UtcNow - start).TotalSeconds < 1);
    }

    [Fact]
    public void IdentityAndSummary()
    {
        Assert.StartsWith(Environment.UserName + "@", ClientInfo.Identity);
        Assert.DoesNotContain(" ", ClientInfo.Identity);
        var s = ClientInfo.Summary();
        // Mac 은 "os: macOS …". Windows 는 "os: Windows …" (다른 OS 에서 테스트하면 그 OS 설명)
        Assert.True(s.Contains($"user: {Environment.UserName}") && s.Contains("computer:") && s.Contains("ip:") && s.Contains("os: "));
        if (OperatingSystem.IsWindows()) Assert.Contains("os: Windows", s);
        Assert.Equal("kim@pc", LogFile.Format(new LogEntry(LogLevel.Info, LogSource.App, "m", Audit: new AuditRecord(AuditAction.AppStart)), identity: "kim@pc")
            .Split('\t')[1]);
    }

    [Fact]
    public void SourceIPToLocalhostAndAddresses()
    {
        Assert.Equal("127.0.0.1", ClientInfo.SourceIp("127.0.0.1", 3306));
        Assert.All(ClientInfo.Addresses(), a => Assert.True(!a.Ip.StartsWith("127.") && !a.Ip.StartsWith("fe80", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("client 127.0.0.1 → 127.0.0.1:1", ClientInfo.ConnectionNote("127.0.0.1", 1, null));
        Assert.StartsWith("client 127.0.0.1 → ssh 127.0.0.1:22 → db:3306",
                          ClientInfo.ConnectionNote("db", 3306, new SshConfig { Host = "127.0.0.1", Port = 22 }));
    }

    /// Windows 추가: VPN 어댑터 판별 (Mac 이름 규칙 + Windows 어댑터 종류·설명)
    [Fact]
    public void VpnDetection()
    {
        Assert.True(ClientInfo.LooksLikeVpn("utun3"));
        Assert.True(ClientInfo.LooksLikeVpn("로컬 영역 연결 2", "TAP-Windows Adapter V9"));
        Assert.True(ClientInfo.LooksLikeVpn("회사", "", System.Net.NetworkInformation.NetworkInterfaceType.Ppp));
        Assert.False(ClientInfo.LooksLikeVpn("Wi-Fi", "Intel(R) Wi-Fi 6 AX201", System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211));
    }
}

public class AuditClassifyTests
{
    [Fact]
    public void SqlActions()
    {
        Assert.Equal(AuditAction.Read, AuditRecord.Classify("-- 메모\nSELECT 1"));
        Assert.Equal(AuditAction.Read, AuditRecord.Classify("show tables"));
        Assert.Equal(AuditAction.Insert, AuditRecord.Classify("INSERT INTO t VALUES (1)"));
        Assert.Equal(AuditAction.Update, AuditRecord.Classify("update t set a=1"));
        Assert.Equal(AuditAction.Delete, AuditRecord.Classify("DELETE FROM t WHERE id=1"));
        Assert.Equal(AuditAction.Delete, AuditRecord.Classify("TRUNCATE TABLE t"));
        Assert.Equal(AuditAction.Ddl, AuditRecord.Classify("ALTER TABLE t ADD c int"));
        Assert.Equal(AuditAction.Ddl, AuditRecord.Classify("DROP TABLE t"));
        Assert.Equal(AuditAction.Grant, AuditRecord.Classify("GRANT SELECT ON t TO u"));
        Assert.Equal(AuditAction.Execute, AuditRecord.Classify("CALL p()"));
        Assert.Equal(AuditAction.Read, AuditRecord.Classify("WITH x AS (SELECT 1) SELECT * FROM x"));
        Assert.Equal(AuditAction.Delete, AuditRecord.Classify("WITH x AS (SELECT id FROM a) DELETE FROM b USING x"));
    }

    [Fact]
    public void RedisActions()
    {
        Assert.Equal(AuditAction.Read, AuditRecord.Classify(["get", "k"]));
        Assert.Equal(AuditAction.Read, AuditRecord.Classify(["HGETALL", "k"]));
        Assert.Equal(AuditAction.Update, AuditRecord.Classify(["SET", "k", "v"]));
        Assert.Equal(AuditAction.Update, AuditRecord.Classify(["EXPIRE", "k", "10"]));
        Assert.Equal(AuditAction.Delete, AuditRecord.Classify(["DEL", "k"]));
        Assert.Equal(AuditAction.Delete, AuditRecord.Classify(["FLUSHDB"]));
        Assert.Equal(AuditAction.Read, AuditRecord.Classify(["CONFIG", "GET", "*"]));
        Assert.Equal(AuditAction.Settings, AuditRecord.Classify(["CONFIG", "SET", "x", "y"]));
        Assert.Equal(AuditAction.Execute, AuditRecord.Classify(["PUBLISH", "c", "m"]));
    }

    [Fact]
    public void CellIsOneLine()
    {
        Assert.Equal("SELECT * FROM t WHERE a = 1", AuditRecord.Cell("SELECT *\n  FROM\tt\r\n WHERE a = 1  "));
        Assert.StartsWith("aaaaaaaaaa …(30자", AuditRecord.Cell(new string('a', 30), limit: 10));
    }
}

[Collection("LogRetention")]
public class LogRetentionTests
{
    [Fact]
    public void RetentionSettingDeletesOldFilesAndHasMinimum()
    {
        var saved = LogFile.RetentionDays;
        try
        {
            LogFile.RetentionDays = 30;
            Assert.Equal(90, LogFile.RetentionDays);          // 최소 90일
            LogFile.RetentionDays = 0;
            Assert.Equal(0, LogFile.RetentionDays);           // 영구

            using var dir = new TempDir();
            Directory.CreateDirectory(dir.Path);
            string Day(int daysAgo) => DateTime.Now.AddDays(-daysAgo).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var old = dir.File($"goddb-audit-{Day(200)}.tsv");
            var recent = dir.File($"goddb-audit-{Day(100)}.tsv");
            foreach (var u in new[] { old, recent }) File.WriteAllText(u, "x");
            var file = new LogFile(dir.Path, retention: () => 180);
            file.CleanNow(); file.Flush();
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(recent));
        }
        finally { LogFile.RetentionDays = saved; }
    }
}

public class AuditIntegrityTests
{
    private static void Write(string dir, IDeviceSigner key, DateTime[] days, int perDay = 3)
    {
        var file = new LogFile(dir, retention: () => 0, signer: () => key);
        foreach (var d in days)
            for (int i = 0; i < perDay; i++)
                file.Append(new LogEntry(LogLevel.Success, LogSource.Query, "",
                                         Audit: new AuditRecord(AuditAction.Read, "dev/shop", $"{i}행", $"SELECT {i}")) { Date = d.AddSeconds(i) });
        file.Flush();
    }

    private static readonly DateTime Day1 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000).LocalDateTime;
    private static DateTime Day2 => Day1.AddDays(1);
    private static DateTime Day3 => Day1.AddDays(2);

    private static string FirstFile(string dir) => Directory.GetFiles(dir).First();

    [Fact]
    public void IntactLogsVerify()
    {
        using var dir = new TempDir();
        var key = new SoftwareDeviceSigner();
        Write(dir.Path, key, [Day1, Day2, Day3]);
        var r = AuditChain.Verify(dir.Path);
        Assert.Equal(3, r.Count);
        Assert.All(r, x => Assert.Equal(AuditFileStatus.Ok.Instance, x.Status));
        // 다시 열어서 이어 쓰기 (앱 재시작)
        Write(dir.Path, key, [Day3], perDay: 1);
        Assert.All(AuditChain.Verify(dir.Path), x => Assert.Equal(AuditFileStatus.Ok.Instance, x.Status));
    }

    [Fact]
    public void EditedLineIsDetected()
    {
        using var dir = new TempDir();
        Write(dir.Path, new SoftwareDeviceSigner(), [Day1]);
        var f = FirstFile(dir.Path);
        var text = File.ReadAllText(f, Encoding.UTF8).Replace("SELECT 1", "SELECT 9");   // 3번째 기록 줄(파일 4번째 줄) 수정
        File.WriteAllText(f, text, Encoding.UTF8);
        var r = AuditChain.Verify(dir.Path);
        Assert.Equal(new AuditFileStatus.Broken(4, "이 줄이 바뀌었거나, 앞 줄이 지워졌거나 끼워 넣어졌어요"), r.First().Status);
    }

    [Fact]
    public void DeletedLineIsDetected()
    {
        using var dir = new TempDir();
        Write(dir.Path, new SoftwareDeviceSigner(), [Day1]);
        var f = FirstFile(dir.Path);
        var rows = File.ReadAllText(f, Encoding.UTF8).Split('\n').ToList();
        rows.RemoveAt(3);                                                                      // 기록 한 줄 삭제
        File.WriteAllText(f, string.Join("\n", rows), Encoding.UTF8);
        var status = Assert.IsType<AuditFileStatus.Broken>(AuditChain.Verify(dir.Path).First().Status);
        Assert.Equal(4, status.Line);
    }

    [Fact]
    public void KeyChangeIsReported()
    {
        using var dir = new TempDir();
        Write(dir.Path, new SoftwareDeviceSigner(), [Day1]);
        Write(dir.Path, new SoftwareDeviceSigner(), [Day2]);   // 다른 키로 쓴 다음 날 파일
        var r = AuditChain.Verify(dir.Path);
        var status = Assert.IsType<AuditFileStatus.Unlinked>(r.Last().Status);
        Assert.Contains("기기 키", status.Reason);
    }

    [Fact]
    public void DeletedOrReplacedFileIsDetected()
    {
        using var dir = new TempDir();
        Write(dir.Path, new SoftwareDeviceSigner(), [Day1, Day2, Day3]);
        var files = Directory.GetFiles(dir.Path).OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
        File.Delete(files[1]);                                                                 // 가운데 날짜 파일 삭제
        var r = AuditChain.Verify(dir.Path);
        Assert.Equal(2, r.Count);
        Assert.IsType<AuditFileStatus.Unlinked>(r[1].Status);
    }

    [Fact]
    public void OtherKeyCannotForge()
    {
        using var dir = new TempDir();
        Write(dir.Path, new SoftwareDeviceSigner(), [Day1]);
        // 내용을 고치고 무결성 값(SHA-256)까지 다시 계산해도, 기기 키 서명이 없으면 통과하지 못한다
        var f = FirstFile(dir.Path);
        var rows = File.ReadAllText(f, Encoding.UTF8).Split('\n');
        var cols = rows[3].Split('\t');
        cols[5] = "SELECT 999";
        var prevHash = rows[2].Split('\t')[6];
        cols[6] = AuditChain.Hash(prevHash, string.Join('\t', cols[..6]));
        rows[3] = string.Join('\t', cols);
        File.WriteAllText(f, string.Join("\n", rows), Encoding.UTF8);
        var status = Assert.IsType<AuditFileStatus.Broken>(AuditChain.Verify(dir.Path).First().Status);
        Assert.True(status.Line == 4 && status.Reason.Contains("서명"));
    }

    [Fact]
    public void TwoAppInstancesKeepOneChain()
    {
        // 앱이 동시에 두 개 켜져 같은 파일에 번갈아 써도 체인이 갈라지지 않는다
        using var dir = new TempDir();
        var key = new SoftwareDeviceSigner();
        var a = new LogFile(dir.Path, retention: () => 0, signer: () => key);
        var b = new LogFile(dir.Path, retention: () => 0, signer: () => key);
        for (int i = 0; i < 6; i++)
        {
            var file = i % 2 == 0 ? a : b;
            file.Append(new LogEntry(LogLevel.Success, LogSource.Query, "",
                                     Audit: new AuditRecord(AuditAction.Read, "dev", $"{i}", $"SELECT {i}")) { Date = Day1.AddSeconds(i) });
            file.Flush();
        }
        var r = AuditChain.Verify(dir.Path);
        Assert.Equal(AuditFileStatus.Ok.Instance, r.First().Status);
        Assert.Equal(7, r.First().Lines);   // 체인 + 6줄
    }

    [Fact]
    public void ForkFromConcurrentWriterIsSuspectNotTampered()
    {
        // 예전 버전처럼 각자 기억한 마지막 값에 이어 쓴 줄: 앞의 다른 줄 값에 이어져 있고 서명도 맞다 → "의심"
        using var dir = new TempDir();
        var key = new SoftwareDeviceSigner();
        Write(dir.Path, key, [Day1], perDay: 3);
        var f = FirstFile(dir.Path);
        var rows = File.ReadAllText(f, Encoding.UTF8).Split('\n').ToList();
        if (rows[^1] == "") rows.RemoveAt(rows.Count - 1);
        var forkFrom = rows[3].Split('\t')[6];                                                   // 파일 4번째 줄 값
        var line = string.Join('\t', "2027-01-15 08:00:09.000", "u@pc", "접속", "dev", "성공", "x");
        var sealedLine = AuditChain.Seal(forkFrom, line, key);
        rows.Add(line + "\t" + sealedLine.Suffix);                                               // 파일 6번째 줄 (5번째가 아닌 4번째에 이어짐)
        File.WriteAllText(f, string.Join("\n", rows) + "\n", new UTF8Encoding(false));
        var status = Assert.IsType<AuditFileStatus.Unlinked>(AuditChain.Verify(dir.Path).First().Status);
        Assert.True(status.Reason.Contains("6번째 줄") && status.Reason.Contains("동시에"), status.Reason);
    }

    /// Windows 추가: 실제로 프로세스 두 개가 같은 로그 폴더에 동시에 써도 한 줄기 체인으로 남는다 (이름 있는 Mutex)
    [Fact]
    public void TwoProcessesWriteConcurrentlyInOneChain()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        using var key = new SoftwareDeviceSigner();
        const int perProcess = 40;
        var children = Enumerable.Range(0, 2).Select(n => AuditChildProcess.Start(dir.Path, key.ExportPkcs8(), $"p{n}", perProcess)).ToList();
        foreach (var c in children)
        {
            Assert.True(c.WaitForExit(120_000), "자식 프로세스가 끝나지 않음");
            c.Process.WaitForExit();
            Assert.True(c.ExitCode == 0, $"자식 프로세스 실패 ({c.ExitCode}): {c.Log}");
        }
        var r = AuditChain.Verify(dir.Path);
        var report = Assert.Single(r);
        Assert.Equal(AuditFileStatus.Ok.Instance, report.Status);
        Assert.Equal(1 + 2 * perProcess, report.Lines);   // 체인 + 두 프로세스의 모든 줄
        var text = File.ReadAllText(Directory.GetFiles(dir.Path, "*.tsv").Single(), Encoding.UTF8);
        Assert.Equal(perProcess, text.Split('\n').Count(l => l.Contains("\tp0-")));
        Assert.Equal(perProcess, text.Split('\n').Count(l => l.Contains("\tp1-")));
    }

    /// 자식 프로세스 쪽: 환경 변수가 있을 때만 쓴다 (평소 테스트 실행에서는 아무것도 하지 않고 통과)
    [Fact]
    public void AuditChildWriter()
    {
        if (Environment.GetEnvironmentVariable(AuditChildProcess.DirVar) is not { Length: > 0 } dir) return;
        var key = SoftwareDeviceSigner.FromPkcs8(Convert.FromBase64String(Environment.GetEnvironmentVariable(AuditChildProcess.KeyVar)!))!;
        var tag = Environment.GetEnvironmentVariable(AuditChildProcess.TagVar)!;
        var count = int.Parse(Environment.GetEnvironmentVariable(AuditChildProcess.CountVar)!, CultureInfo.InvariantCulture);
        // 두 프로세스가 동시에 시작하도록 잠깐 맞춘다
        var startAt = DateTime.FromBinary(long.Parse(Environment.GetEnvironmentVariable(AuditChildProcess.StartVar)!, CultureInfo.InvariantCulture));
        while (DateTime.UtcNow < startAt) Thread.Sleep(5);
        var file = new LogFile(dir, retention: () => 0, signer: () => key);
        for (int i = 0; i < count; i++)
        {
            file.Append(new LogEntry(LogLevel.Success, LogSource.Query, "",
                                     Audit: new AuditRecord(AuditAction.Read, "dev", $"{tag}-{i}", $"SELECT {i}")) { Date = Day1.AddMilliseconds(i) });
            file.Flush();   // 한 줄씩 써서 두 프로세스가 섞이게
        }
    }

    [Fact]
    public void LegacyFilesAreReported()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(dir.File("goddb-audit-2026-01-01.tsv"), "시각\t사용자@PC\t구분\t대상\t결과\t내용\nx\ty\t조회\td\tr\ts\n", Encoding.UTF8);
        Assert.Equal(AuditFileStatus.Legacy.Instance, AuditChain.Verify(dir.Path).First().Status);
    }

    // ---- Windows 판 추가 테스트 ----

    /// 서명 형식이 CryptoKit 과 같다: 공개 키 X‖Y 64바이트, 서명 r‖s 64바이트 (base64)
    [Fact]
    public void SignatureAndKeyUseRawEncoding()
    {
        using var key = new SoftwareDeviceSigner();
        Assert.Equal(64, key.PublicKeyRaw.Length);
        var (hash, suffix) = AuditChain.Seal(AuditChain.Zero, "a\tb", key);
        Assert.Equal(AuditChain.Hash(AuditChain.Zero, "a\tb"), hash);
        var sig = Convert.FromBase64String(suffix.Split('\t')[1]);
        Assert.Equal(64, sig.Length);
        Assert.True(SoftwareDeviceSigner.Verify(key.PublicKeyRaw, Encoding.UTF8.GetBytes(hash), sig));
        Assert.StartsWith($"t\tu\t체인\t-\tkey:sw:{Convert.ToBase64String(key.PublicKeyRaw)}\t", AuditChain.LinkLine("t", "u", null, AuditChain.Zero, key));
        // 알려진 값: SHA-256("000…0\nGodDB-audit|goddb-audit-2026-01-01.tsv")
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(AuditChain.Zero + "\nGodDB-audit|goddb-audit-2026-01-01.tsv"))), AuditChain.Seed("goddb-audit-2026-01-01.tsv"));
    }

    /// 기기 키는 키 보관소에 한 번 만들고 다시 읽는다
    [Fact]
    public void DeviceKeyIsPersistedInKeyStore()
    {
        using var dir = new TempDir();
        var store = new FileDeviceKeyStore(dir.Path);
        Assert.Null(store.Load("device-key-sw"));
        using var key = new SoftwareDeviceSigner();
        Assert.True(store.Save("device-key-sw", key.ExportPkcs8()));
        Assert.False(store.Save("device-key-sw", key.ExportPkcs8()));   // 덮어쓰지 않음
        Assert.True(UserOnlyFile.IsRestricted(Path.Combine(dir.Path, "device-key-sw.key")));
        using var loaded = SoftwareDeviceSigner.FromPkcs8(store.Load("device-key-sw")!);
        Assert.Equal(key.PublicKeyRaw, loaded!.PublicKeyRaw);
    }
}

public class AppLogTests
{
    [Fact]
    public void KeepsLast1000AndTurnsErrorsIntoAuditFailures()
    {
        var log = new AppLog();
        for (int i = 0; i < 1005; i++) log.Log(LogLevel.Info, LogSource.App, $"m{i}");
        Assert.Equal(AppLog.MaxEntries, log.Entries.Count);
        Assert.Equal("m5", log.Entries[0].Message);
        LogEntry? added = null;
        log.Added += e => added = e;
        var shown = log.LogError(new SocketException((int)SocketError.ConnectionRefused), LogSource.Connection, "dev 연결 실패",
                                 audit: new AuditRecord(AuditAction.Connect, "dev"));
        Assert.Equal(LogLevel.Error, added?.Level);
        Assert.Equal("실패: 서버가 연결을 거부했어요. (포트가 닫혀 있음)", added?.Audit?.Result);
        Assert.StartsWith("dev 연결 실패: 서버가 연결을 거부했어요.", added?.Message);
        Assert.Contains("\n\n해결 방법: ", shown);
        Assert.Equal(1, log.ErrorCount);
        Assert.Single(log.RecentErrorLines());
        Assert.StartsWith("[접속] dev 연결 실패", log.RecentErrorLines()[0]);
        Assert.Contains("해결 방법: ", added!.FullText);
    }

    [Fact]
    public void DescribesCommonErrors()
    {
        Assert.Equal("연결 시간이 초과됐어요.", ErrorText.Describe(new TimeoutException()).Message);
        Assert.Equal("호스트 이름을 찾을 수 없어요.", ErrorText.Describe(new Exception("nodename nor servname provided")).Message);
        Assert.Equal("TLS 연결에 실패했어요.", ErrorText.Describe(new Exception("SSL handshake failed")).Message);
        Assert.Equal("그냥 오류", ErrorText.Describe(new Exception("그냥 오류")).Message);
        Assert.NotNull(ErrorText.SshHint("Permission denied (publickey)"));
        Assert.NotNull(ErrorText.RedisHint("WRONGPASS"));
        Assert.Contains("DBA", ErrorText.ServerHint(1142, ""));
    }
}
