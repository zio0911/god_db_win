using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GodDB.Core.Profiles;

namespace GodDB.Core.Tests;

public class ConnectionTransferTests
{
    private readonly ConnectionProfile a = new()
    {
        Kind = DatabaseKind.Mysql, Name = "dev", Host = "db.local", Port = 3306, Username = "app", Database = "main",
        Ssh = new SshConfig { Host = "bastion", KeyPath = "~/.ssh/id" },
    };
    private readonly ConnectionProfile b = new() { Kind = DatabaseKind.Postgres, Name = "pg", Host = "pg.local", Port = 5432, Username = "pg" };

    [Fact]
    public void RoundTripWithoutPasswords()
    {
        var data = ConnectionTransfer.Export([a, b], appVersion: "0.7.5");
        var text = Encoding.UTF8.GetString(data);
        Assert.Contains("goddb-connections", text);
        Assert.DoesNotContain("passwords", text);
        var bundle = ConnectionTransfer.Parse(data);
        Assert.Equal([a, b], bundle.Profiles);
        Assert.False(bundle.HasPasswords);
        Assert.Equal("GodDB 0.7.5", bundle.App);
    }

    [Fact]
    public void PasswordsAreEncrypted()
    {
        var data = ConnectionTransfer.Export([a, b], new Dictionary<Guid, string> { [a.Id] = "s3cret-pw", [Guid.NewGuid()] = "other" },
                                             passphrase: "team-pass");
        Assert.DoesNotContain("s3cret-pw", Encoding.UTF8.GetString(data));
        var bundle = ConnectionTransfer.Parse(data);
        Assert.True(bundle.HasPasswords);
        Assert.Equal(new Dictionary<Guid, string> { [a.Id] = "s3cret-pw" }, ConnectionTransfer.Passwords(bundle, "team-pass"));
        var e = Assert.Throws<ConnectionTransferException>(() => ConnectionTransfer.Passwords(bundle, "wrong"));
        Assert.Equal(ConnectionTransferErrorKind.WrongPassphrase, e.Kind);
    }

    [Fact]
    public void PasswordsNeedPassphrase()
    {
        var e = Assert.Throws<ConnectionTransferException>(() =>
            ConnectionTransfer.Export([a], new Dictionary<Guid, string> { [a.Id] = "x" }, passphrase: ""));
        Assert.Equal(ConnectionTransferErrorKind.EmptyPassphrase, e.Kind);
    }

    [Fact]
    public void RejectsOtherFiles()
    {
        var e1 = Assert.Throws<ConnectionTransferException>(() => ConnectionTransfer.Parse(Encoding.UTF8.GetBytes("[1,2]")));
        Assert.Equal(ConnectionTransferErrorKind.NotGodDBFile, e1.Kind);
        var e2 = Assert.Throws<ConnectionTransferException>(() => ConnectionTransfer.Parse(Encoding.UTF8.GetBytes(
            """{"format":"other","version":1,"exportedAt":"2026-01-01T00:00:00Z","app":"x","profiles":[]}""")));
        Assert.Equal(ConnectionTransferErrorKind.NotGodDBFile, e2.Kind);
    }

    [Fact]
    public void MergeReplacesSameIDAndRenamesDuplicates()
    {
        var changed = a with { Host = "new.host" };
        var sameName = new ConnectionProfile { Name = "pg", Host = "other" };
        var merged = ConnectionTransfer.Merge([changed, sameName], [a, b]);
        Assert.Equal(3, merged.Count);
        Assert.Equal("new.host", merged[0].Host);
        Assert.Equal("pg (2)", merged[2].Name);
        Assert.Equal(new ImportAction.Replace("dev"), ConnectionTransfer.Action(changed, [a]));
        Assert.Equal(ImportAction.Add.Instance, ConnectionTransfer.Action(sameName, [a, b]));
        // Windows 추가: 넘긴 접속은 그대로 (복사본을 합친다)
        Assert.Equal("pg", sameName.Name);
    }

    // ---- Windows 판 추가 테스트 ----

    [Fact]
    public void ExportedFileRoundTripsThroughDisk()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var c = new ConnectionProfile { Kind = DatabaseKind.Redis, Name = "캐시", Host = "cache.local", Port = 6380, Group = "운영",
                                        Environment = ConnectionEnvironment.Production, RedisMode = RedisMode.Sentinel, SentinelMaster = "mymaster" };
        var path = dir.File(ConnectionTransfer.ExportFileName(new DateTime(2026, 11, 18)));
        Assert.EndsWith("goddb-connections-20261118.json", path);
        ConnectionTransfer.WriteFile(path, ConnectionTransfer.Export([a, b, c], new Dictionary<Guid, string> { [c.Id] = "비밀/pw" },
                                                                      passphrase: "long-passphrase", appVersion: "1.0.0"));
        Assert.True(Platform.UserOnlyFile.IsRestricted(path));
        var bundle = ConnectionTransfer.Parse(File.ReadAllBytes(path));
        Assert.Equal([a, b, c], bundle.Profiles);
        Assert.Equal("비밀/pw", ConnectionTransfer.Passwords(bundle, "long-passphrase")[c.Id]);
    }

    [Fact]
    public void ExportFormatMatchesMac()
    {
        var now = new DateTimeOffset(2026, 11, 13, 14, 46, 25, 398, TimeSpan.FromHours(9));
        var data = ConnectionTransfer.Export([a], new Dictionary<Guid, string> { [a.Id] = "pw" }, passphrase: "team-pass", now: now);
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        // 키 이름순 (Swift .sortedKeys)
        Assert.Equal(["app", "exportedAt", "format", "passwords", "profiles", "version"], root.EnumerateObject().Select(p => p.Name).ToArray());
        var profileKeys = root.GetProperty("profiles")[0].EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(profileKeys.Order(StringComparer.Ordinal), profileKeys);
        // Swift .iso8601: UTC, 소수 초 없음
        Assert.Equal("2026-11-13T05:46:25Z", root.GetProperty("exportedAt").GetString());
        Assert.Equal("goddb-connections", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("mysql", root.GetProperty("profiles")[0].GetProperty("kind").GetString());
        var pw = root.GetProperty("passwords");
        Assert.Equal(["data", "kdf", "rounds", "salt"], pw.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("pbkdf2-sha256", pw.GetProperty("kdf").GetString());
        Assert.Equal(200_000, pw.GetProperty("rounds").GetInt32());
        Assert.Equal(16, Convert.FromBase64String(pw.GetProperty("salt").GetString()!).Length);
        // 안의 JSON: {"<대문자 UUID>":"pw"} → nonce 12 + 암호문 + tag 16
        var plainLength = Encoding.UTF8.GetByteCount($"{{\"{a.Id.ToString().ToUpperInvariant()}\":\"pw\"}}");
        Assert.Equal(12 + plainLength + 16, Convert.FromBase64String(pw.GetProperty("data").GetString()!).Length);
    }

    /// PBKDF2-HMAC-SHA256 이 표준 값과 같은지 (CommonCrypto CCKeyDerivationPBKDF 와 같은 알고리즘)
    [Fact]
    public void KeyDerivationMatchesStandardVectors()
    {
        var salt = Encoding.UTF8.GetBytes("salt");
        Assert.Equal("120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b",
                     Convert.ToHexStringLower(ConnectionTransfer.Key("password", salt, 1)));
        Assert.Equal("ae4d0c95af6b46d32d0adff928f06dd02a303f8ef3c251dfd6e2d85a95474c43",
                     Convert.ToHexStringLower(ConnectionTransfer.Key("password", salt, 2)));
    }

    /// Mac 판 Swift 코드가 만드는 그대로 (JSONEncoder .prettyPrinted .sortedKeys, UUID 대문자, "/" → "\/",
    /// PBKDF2-SHA256 20만 회 → AES.GCM.seal(...).combined = nonce‖암호문‖tag) 만든 파일을 읽는다.
    [Fact]
    public void ReadsPasswordProtectedFileBuiltLikeMac()
    {
        const string id = "B7DCA191-568C-407D-A9CA-7BCD3A995EA2";
        const string passphrase = "팀 공유 암호!";
        var salt = Enumerable.Range(0, 16).Select(i => (byte)(i * 7 + 1)).ToArray();
        var nonce = Enumerable.Range(0, 12).Select(i => (byte)(0xA0 + i)).ToArray();
        // Swift JSONEncoder().encode([String: String]) — 압축, 슬래시 이스케이프
        var plain = Encoding.UTF8.GetBytes($$"""{"{{id}}":"p\/w-한글"}""");
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, 200_000, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        var combined = Convert.ToBase64String([.. nonce, .. cipher, .. tag]);

        var file = $$"""
        {
          "app" : "GodDB 0.9.0",
          "exportedAt" : "2026-11-13T05:46:25Z",
          "format" : "goddb-connections",
          "passwords" : {
            "data" : "{{combined}}",
            "kdf" : "pbkdf2-sha256",
            "rounds" : 200000,
            "salt" : "{{Convert.ToBase64String(salt)}}"
          },
          "profiles" : [
            {
              "database" : "shop",
              "environment" : "development",
              "group" : "개발",
              "host" : "db.example.com",
              "id" : "{{id}}",
              "kind" : "mysql",
              "name" : "dev",
              "port" : 3306,
              "readOnly" : false,
              "redisMode" : "standalone",
              "savePassword" : true,
              "sentinelMaster" : "",
              "ssh" : {
                "host" : "bastion.example.com",
                "keyPath" : "~\/.ssh\/id_ed25519",
                "port" : 22,
                "username" : "ec2-user"
              },
              "useTLS" : true,
              "username" : "app"
            }
          ],
          "version" : 1
        }
        """;
        var bundle = ConnectionTransfer.Parse(Encoding.UTF8.GetBytes(file));
        Assert.Equal("GodDB 0.9.0", bundle.App);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 5, 46, 25, TimeSpan.Zero), bundle.ExportedAt);
        var p = Assert.Single(bundle.Profiles);
        Assert.Equal(Guid.Parse(id), p.Id);
        Assert.Equal("~/.ssh/id_ed25519", p.Ssh?.KeyPath);
        Assert.Equal("개발", p.Group);
        Assert.Equal(ConnectionEnvironment.Development, p.Environment);
        Assert.Equal("p/w-한글", ConnectionTransfer.Passwords(bundle, passphrase)[Guid.Parse(id)]);
        var e = Assert.Throws<ConnectionTransferException>(() => ConnectionTransfer.Passwords(bundle, "다른 암호"));
        Assert.Equal(ConnectionTransferErrorKind.WrongPassphrase, e.Kind);
    }

    [Fact]
    public void RejectsNewerVersionAndBrokenPayload()
    {
        var e = Assert.Throws<ConnectionTransferException>(() => ConnectionTransfer.Parse(Encoding.UTF8.GetBytes(
            """{"format":"goddb-connections","version":2,"exportedAt":"2026-01-01T00:00:00Z","app":"x","profiles":[]}""")));
        Assert.Equal(ConnectionTransferErrorKind.UnsupportedVersion, e.Kind);
        Assert.Equal(2, e.FileVersion);
        var bundle = ConnectionTransfer.Parse(Encoding.UTF8.GetBytes(
            """{"format":"goddb-connections","version":1,"exportedAt":"2026-01-01T00:00:00Z","app":"x","profiles":[],"passwords":{"kdf":"pbkdf2-sha256","rounds":1,"salt":"AAAA","data":"AAAA"}}"""));
        var e2 = Assert.Throws<ConnectionTransferException>(() => ConnectionTransfer.Passwords(bundle, "x"));
        Assert.Equal(ConnectionTransferErrorKind.NotGodDBFile, e2.Kind);
    }

    [Fact]
    public void ImportSheetDefaultsAndPassphraseRules()
    {
        var incoming = new[] { a, new ConnectionProfile { Name = "new" } };
        Assert.Equal([incoming[1].Id], ConnectionTransfer.DefaultSelection(incoming, [a]));
        Assert.Null(ConnectionTransfer.PassphraseProblem(false, "", ""));
        Assert.Equal("암호는 8자 이상으로 정해 주세요.", ConnectionTransfer.PassphraseProblem(true, "short", "short"));
        Assert.Equal("암호 확인이 달라요.", ConnectionTransfer.PassphraseProblem(true, "long-enough", "long-enougH"));
        Assert.Null(ConnectionTransfer.PassphraseProblem(true, "long-enough", "long-enough"));
        var log = ConnectionTransfer.ImportedLog([a, b], "x.json", 1);
        Assert.Equal("접속 정보 2개 가져옴 · x.json · 비밀번호 1개", log.Message);
        Assert.Equal("접속 정보 2개 가져옴 · 비밀번호 1개", log.Audit?.Result);
        Assert.Equal("x.json · dev, pg", log.Audit?.Statement);
    }
}

public class ConnectionGroupsTests
{
    [Fact]
    public void GroupsAreSortedAndMovable()
    {
        var list = new List<ConnectionProfile>
        {
            new() { Name = "a", Group = "dev 10" }, new() { Name = "b", Group = "Dev 2" }, new() { Name = "c" }, new() { Name = "d", Group = "dev 10" },
        };
        Assert.Equal(["Dev 2", "dev 10"], ConnectionGroups.Groups(list));
        Assert.Equal("c", Assert.Single(ConnectionGroups.Ungrouped(list)).Name);
        Assert.Equal("c 접속을 운영 그룹으로 옮겼어요", ConnectionGroups.Move(list, list[2].Id, "  운영 "));
        Assert.Null(ConnectionGroups.Move(list, list[2].Id, "운영"));
        Assert.Equal("c 접속을 그룹에서 뺐어요", ConnectionGroups.Move(list, list[2].Id, ""));
        Assert.Equal("dev 10 그룹 이름을 개발 로 바꿨어요", ConnectionGroups.Rename(list, "dev 10", "개발"));
        Assert.Equal(2, ConnectionGroups.Members(list, "개발").Count);
        Assert.Equal("개발 그룹을 풀었어요", ConnectionGroups.Rename(list, "개발", " "));
        Assert.Equal(3, ConnectionGroups.Ungrouped(list).Count);
        Assert.Equal(new HashSet<string> { "b", "a" }, ConnectionGroups.DecodeCollapsed(ConnectionGroups.EncodeCollapsed(["b", "a"])));
        Assert.Equal("a\nb", ConnectionGroups.EncodeCollapsed(["b", "a"]));
    }
}
