using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GodDB.Core.Ai;
using GodDB.Core.Audit;
using GodDB.Core.Platform;

namespace GodDB.Core.Tests;

/// 로컬 데이터 암호화 (GDBENC1). 실제 키 저장소 · 앱 데이터 폴더는 건드리지 않는다 (메모리 키 + 임시 폴더). (Mac LocalDataCryptoTests)
public class LocalDataCryptoTests : IDisposable
{
    private readonly LocalDataCrypto crypto = new(new StaticLocalDataKeyProvider());
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private string TempDir()
    {
        var d = Path.Combine(_tmp.Path, "goddb-enc-" + Guid.NewGuid());
        Directory.CreateDirectory(d);
        return d;
    }

    private static HistoryEntry Entry(string sql, double daysAgo = 0, DateTime? now = null) =>
        new(Guid.NewGuid(), "p", "shop", sql, 1, true, date: (now ?? DateTime.Now).AddDays(-daysAgo));

    private static bool Contains(byte[] data, string text) => data.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    private const string SecretSql = "SELECT * FROM member WHERE phone = '010-1234-5678'";

    private static byte[] Json(HistoryEntry e) => JsonSerializer.SerializeToUtf8Bytes(e, QueryHistory.Json);

    // MARK: 봉투

    [Fact]
    public void SharedUsesMemoryKeyInTests()
    {
        // 단위 테스트에서 Shared 가 실제 키 저장소를 쓰지 않는다
        Assert.True(LocalDataCrypto.IsTestProcess);
    }

    [Fact]
    public void RoundTrip()
    {
        var plain = Encoding.UTF8.GetBytes(SecretSql);
        var sealedBox = crypto.Seal(plain);
        Assert.Equal(plain.Length + 12 + 16, sealedBox.Length);
        Assert.Equal(plain, crypto.Open(sealedBox));
        // 같은 내용도 nonce 가 달라 매번 다르다
        Assert.NotEqual(sealedBox, crypto.Seal(plain));

        var doc = crypto.EncryptDocument(plain);
        Assert.True(doc.AsSpan().StartsWith("GDBENC1\n"u8));
        Assert.Equal((byte)'\n', doc[^1]);
        Assert.Equal(plain, crypto.DecryptDocument(doc));

        var s = crypto.SealString("RPUSH q 1");
        Assert.StartsWith("GDBENC1:", s);
        Assert.Equal("RPUSH q 1", crypto.OpenString(s));
        Assert.Equal("평문 그대로", crypto.OpenString("평문 그대로"));
    }

    private static void ThrowsKind(LocalDataCryptoErrorKind kind, Action action) =>
        Assert.Equal(kind, Assert.Throws<LocalDataCryptoException>(action).Kind);

    [Fact]
    public void TamperingIsDetected()
    {
        var sealedBox = crypto.Seal(Encoding.UTF8.GetBytes(SecretSql));
        sealedBox[sealedBox.Length / 2] ^= 0x01;
        ThrowsKind(LocalDataCryptoErrorKind.AuthenticationFailed, () => crypto.Open(sealedBox));
        // 다른 키
        var other = new LocalDataCrypto(new StaticLocalDataKeyProvider());
        var good = crypto.Seal("x"u8.ToArray());
        ThrowsKind(LocalDataCryptoErrorKind.AuthenticationFailed, () => other.Open(good));
        // 모양이 틀림
        ThrowsKind(LocalDataCryptoErrorKind.Malformed, () => crypto.OpenLine("not base64 !!"));
        ThrowsKind(LocalDataCryptoErrorKind.Malformed, () => crypto.Open([1, 2, 3]));
        // 키 없음
        var none = new LocalDataCrypto(new UnavailableLocalDataKeyProvider());
        Assert.Throws<LocalDataCryptoException>(() => none.Seal("x"u8.ToArray()));
    }

    [Fact]
    public void TamperedDocumentIsMovedAsideNotDeleted()
    {
        var file = Path.Combine(TempDir(), "tabs.json");
        crypto.WriteDocument("[]"u8.ToArray(), file);
        var raw = File.ReadAllBytes(file);
        raw[^5] = raw[^5] == 0x41 ? (byte)0x42 : (byte)0x41;
        File.WriteAllBytes(file, raw);
        Assert.Equal(LocalReadStatus.Damaged, crypto.ReadDocument(file).Status);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(file + ".undecryptable"));
    }

    // MARK: 평문 → 암호화

    [Fact]
    public void MigratesPlaintextDocument()
    {
        var file = Path.Combine(TempDir(), "tabs.json");
        var tabs = new List<SavedTab> { new(Guid.NewGuid(), "shop", 1, null, SecretSql, true) };
        File.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(tabs, QueryHistory.Json));

        var store = new TabStore(file, crypto);
        Assert.Equal(tabs, store.Load());
        var raw = File.ReadAllBytes(file);
        Assert.True(LocalDataCrypto.IsEncrypted(raw));
        Assert.False(Contains(raw, "010-1234-5678"));
        Assert.Equal(tabs, store.Load());
    }

    [Fact]
    public void PlaintextKeptWhenKeyUnavailable()
    {
        var dir = TempDir();
        var tabsFile = Path.Combine(dir, "tabs.json");
        var tabs = new List<SavedTab> { new(Guid.NewGuid(), "shop", 1, null, "SELECT 1", true) };
        var plain = JsonSerializer.SerializeToUtf8Bytes(tabs, QueryHistory.Json);
        File.WriteAllBytes(tabsFile, plain);
        var none = new LocalDataCrypto(new UnavailableLocalDataKeyProvider());
        var store = new TabStore(tabsFile, none);
        Assert.Equal(tabs, store.Load());
        // 평문은 그대로 (잃지 않음), 새로 저장해도 평문으로 쓰지 않는다
        Assert.Equal(plain, File.ReadAllBytes(tabsFile));
        store.Save([]);
        Assert.Equal(plain, File.ReadAllBytes(tabsFile));

        // 히스토리: 메모리에서만, 디스크는 그대로
        var histFile = Path.Combine(dir, "history.jsonl");
        var legacy = (byte[])[.. Json(Entry("SELECT old")), (byte)'\n'];
        File.WriteAllBytes(histFile, legacy);
        var h = new QueryHistory(histFile, crypto: none);
        Assert.Equal(["SELECT old"], h.All().Select(e => e.Sql));
        Assert.True(h.IsMemoryOnly);
        h.Append(Entry("SELECT new"));
        Assert.Equal(["SELECT new", "SELECT old"], h.All().Select(e => e.Sql));
        Assert.Equal(legacy, File.ReadAllBytes(histFile));
    }

    [Fact]
    public void MigratesPlaintextHistory()
    {
        var file = Path.Combine(TempDir(), "history.jsonl");
        var lines = new[] { Entry("SELECT 1", daysAgo: 2), Entry(SecretSql, daysAgo: 1) }
            .Select(e => Encoding.UTF8.GetString(Json(e)));
        File.WriteAllText(file, string.Join("\n", lines) + "\n");

        var h = new QueryHistory(file, crypto: crypto);
        Assert.Equal([SecretSql, "SELECT 1"], h.All().Select(e => e.Sql));
        var raw = File.ReadAllBytes(file);
        Assert.True(LocalDataCrypto.IsEncrypted(raw));
        Assert.False(Contains(raw, "010-1234-5678"));
        Assert.False(Contains(raw, "SELECT"));
        Assert.Equal([SecretSql, "SELECT 1"], new QueryHistory(file, crypto: crypto).All().Select(e => e.Sql));
    }

    /// 처음엔 키가 없다가 나중에 생기는 키 (키 저장소가 잠겨 있다가 풀림)
    private sealed class Flaky : ILocalDataKeyProvider
    {
        public readonly byte[] K = RandomNumberGenerator.GetBytes(32);
        public bool Available;
        public byte[] Key() => Available ? K : throw new LocalDataCryptoException(LocalDataCryptoErrorKind.KeyUnavailable, -25308);
    }

    [Fact]
    public void HistoryRecoversWhenKeyComesBack()
    {
        var flaky = new Flaky();
        var c = new LocalDataCrypto(flaky);
        var file = Path.Combine(TempDir(), "history.jsonl");
        // 암호화된 기록이 이미 있다
        var seed = new QueryHistory(file, crypto: new LocalDataCrypto(new StaticLocalDataKeyProvider(flaky.K)));
        seed.Append(Entry("SELECT disk"));
        var before = File.ReadAllBytes(file);

        var h = new QueryHistory(file, crypto: c);
        Assert.Empty(h.All());
        h.Append(Entry("SELECT memory"));
        Assert.True(h.IsMemoryOnly);
        Assert.Equal(before, File.ReadAllBytes(file));  // 덮어쓰지 않음

        flaky.Available = true;
        Assert.Equal(["SELECT memory", "SELECT disk"], h.All().Select(e => e.Sql));
        Assert.False(h.IsMemoryOnly);
        Assert.Equal(["SELECT memory", "SELECT disk"], new QueryHistory(file, crypto: c).All().Select(e => e.Sql));
    }

    // MARK: 줄 단위 히스토리

    [Fact]
    public void PerLineAppendAndReload()
    {
        var file = Path.Combine(TempDir(), "history.jsonl");
        var h = new QueryHistory(file, crypto: crypto);
        h.Append(Entry("SELECT 1"));
        var first = File.ReadAllBytes(file);
        h.Append(Entry("SELECT 2"));
        h.Append(Entry("SELECT 3"));
        var raw = File.ReadAllBytes(file);
        // 덧붙이기만 했다 (앞부분 그대로), 머리 줄 + 한 줄에 한 건
        Assert.True(raw.AsSpan().StartsWith(first));
        var lines = Encoding.UTF8.GetString(raw).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("GDBENC1", lines[0]);
        Assert.Equal(4, lines.Length);
        foreach (var line in lines.Skip(1))
        {
            Convert.FromBase64String(line);
            var e = JsonSerializer.Deserialize<HistoryEntry>(crypto.OpenLine(line), QueryHistory.Json)!;
            Assert.StartsWith("SELECT", e.Sql);
        }
        Assert.Equal(["SELECT 3", "SELECT 2", "SELECT 1"], new QueryHistory(file, crypto: crypto).All().Select(e => e.Sql));

        // 한 줄이 깨져도 나머지는 읽는다
        var broken = lines.ToArray();
        broken[2] = new string(broken[2].Reverse().ToArray());
        File.WriteAllText(file, string.Join("\n", broken) + "\n");
        Assert.Equal(["SELECT 3", "SELECT 1"], new QueryHistory(file, crypto: crypto).All().Select(e => e.Sql));
    }

    // MARK: 보관 기간

    [Theory]
    [InlineData(7, 1)]
    [InlineData(30, 2)]
    [InlineData(90, 3)]
    [InlineData(0, 5)]
    public void Retention(int keepDays, int expected)
    {
        var now = DateTime.Now;
        double[] ages = [400, 100, 40, 10, 1];
        var file = Path.Combine(TempDir(), "history.jsonl");
        var lines = ages.Select(a => crypto.SealLine(Json(Entry($"SELECT {(int)a}", daysAgo: a, now: now))));
        File.WriteAllText(file, string.Join("\n", new[] { "GDBENC1" }.Concat(lines)) + "\n");

        var h = new QueryHistory(file, crypto: crypto, keepDays: () => keepDays, now: () => now);
        Assert.Equal(expected, h.All().Count);
        // 파일도 정리됐다
        Assert.Equal(expected, new QueryHistory(file, crypto: crypto, keepDays: () => 0, now: () => now).All().Count);
        Assert.Equal(expected, QueryHistory.Retained(h.All(), keepDays, now).Count);
    }

    [Fact]
    public void RetentionRunsAtMostOncePerDayOnAppend()
    {
        var clock = DateTime.Now;
        var file = Path.Combine(TempDir(), "history.jsonl");
        var h = new QueryHistory(file, crypto: crypto, keepDays: () => 7, now: () => clock);
        h.Append(Entry("SELECT a", now: clock));
        // 8일 지남 → 다음 기록 때 정리
        clock = clock.AddDays(8);
        h.Append(Entry("SELECT b", now: clock));
        Assert.Equal(["SELECT b"], h.All().Select(e => e.Sql));
        // 하루 안에는 다시 정리하지 않는다
        clock = clock.AddHours(1);
        h.Append(Entry("SELECT c", daysAgo: 30, now: clock));
        Assert.Equal(2, h.All().Count);
        h.ApplyRetentionNow();
        Assert.Equal(["SELECT b"], h.All().Select(e => e.Sql));
    }

    // MARK: 탭 · AI 대화

    [Fact]
    public void TabStoreRoundTripIsEncrypted()
    {
        var file = Path.Combine(TempDir(), "tabs.json");
        var store = new TabStore(file, crypto);
        var tabs = new List<SavedTab>
        {
            new(Guid.NewGuid(), "shop", 1, "회원", SecretSql, true),
            new(Guid.NewGuid(), "crm", 2, null, "", false),
        };
        store.Save(tabs);
        Assert.Equal(tabs, store.Load());
        Assert.Equal(tabs, new TabStore(file, crypto).Load());
        var raw = File.ReadAllBytes(file);
        Assert.True(LocalDataCrypto.IsEncrypted(raw));
        Assert.Equal(2, Encoding.UTF8.GetString(raw).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        // 다른 키로는 못 연다 (빈 목록, 파일은 옆으로 치움)
        Assert.Empty(new TabStore(file, new LocalDataCrypto(new StaticLocalDataKeyProvider())).Load());
    }

    /// Mac 은 저장 쿼리(SavedQueryStore)까지 네 파일. Windows 는 저장 쿼리가 아직 없어 이 셋 + 공용 WriteText 로 쓴 파일을 본다.
    [Fact]
    public void NoPlaintextSQLOnDisk()
    {
        var dir = TempDir();
        const string marker = "SELECT secret_marker_7f3a FROM member WHERE email = 'kim@example.com'";
        var h = new QueryHistory(Path.Combine(dir, "history.jsonl"), crypto: crypto);
        h.Append(Entry(marker));
        new TabStore(Path.Combine(dir, "tabs.json"), crypto)
            .Save([new SavedTab(Guid.NewGuid(), "shop", 1, null, marker, true)]);
        Assert.True(crypto.WriteText(Path.Combine(dir, "saved-queries.json"),
            JsonSerializer.Serialize(new { queries = new[] { new { name = "q", sql = marker, lastValues = new { email = "kim@example.com" } } } })));
        var chats = new DiagramChatStore(Path.Combine(dir, "diagram-chats"), crypto: crypto);
        chats.Save(new DiagramConversation(messages: [new("user", marker)]), "s");

        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        Assert.Equal(4, files.Length);
        foreach (var f in files)
        {
            var raw = File.ReadAllBytes(f);
            Assert.True(LocalDataCrypto.IsEncrypted(raw), Path.GetFileName(f));
            foreach (var needle in new[] { "secret_marker_7f3a", "kim@example.com", "SELECT", "member" })
                Assert.False(Contains(raw, needle), $"{Path.GetFileName(f)} contains {needle}");
        }
        // 다시 읽힌다
        Assert.Equal(marker, h.All().First().Sql);
        Assert.Equal(marker, chats.Load("s").First().Messages.First().Text);
        Assert.Contains("secret_marker_7f3a", crypto.ReadText(Path.Combine(dir, "saved-queries.json")));
    }

    [Fact]
    public void ChatStoreMigratesAndKeepsFileWhenKeyUnavailable()
    {
        var dir = TempDir();
        var plainStore = new DiagramChatStore(dir, crypto: crypto);
        var conv = new DiagramConversation(messages: [new("user", "SELECT 1")]);
        // 예전 평문 파일
        var file = plainStore.FilePath("s");
        File.WriteAllBytes(file, DiagramChatStore.Encode([conv]));
        plainStore.PruneAll();
        Assert.True(LocalDataCrypto.IsEncrypted(File.ReadAllBytes(file)));
        Assert.Equal([conv.Id], plainStore.Load("s").Select(c => c.Id));

        // 키가 없으면 지우기 · 저장이 파일을 건드리지 않는다
        var before = File.ReadAllBytes(file);
        var locked = new DiagramChatStore(dir, crypto: new LocalDataCrypto(new UnavailableLocalDataKeyProvider()));
        Assert.Empty(locked.Load("s"));
        locked.Delete(conv.Id, "s");
        locked.Save(new DiagramConversation(messages: [new("user", "x")]), "s");
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    /// Mac defaultsStringMigratesAndEncrypts (UserDefaults → Windows 설정 파일 Prefs)
    [Fact]
    public void PrefsStringMigratesAndEncrypts()
    {
        var store = new Dictionary<string, string>();
        string? Get(string k) => store.TryGetValue(k, out var v) ? v : null;
        void Set(string k, string v) => store[k] = v;
        store["w"] = "SET k secret";
        Assert.Equal("SET k secret", crypto.PrefsString("w", Get, Set));
        Assert.StartsWith("GDBENC1:", store["w"]);
        crypto.SetPrefsString("w", "GET k", Set);
        Assert.DoesNotContain("GET", store["w"]);
        Assert.Equal("GET k", crypto.PrefsString("w", Get, Set));
    }

    // MARK: Windows 추가

    /// 키 저장소 키: 처음이면 만들어 두고, 다음에는 같은 키. 다른 곳이 먼저 만들었으면 그것을 쓴다.
    [Fact]
    public void DeviceKeyStoreProviderCreatesOnceAndReuses()
    {
        var store = new FileDeviceKeyStore(Path.Combine(TempDir(), "keys"));
        var a = new DeviceKeyStoreLocalDataKeyProvider(store).Key();
        Assert.Equal(32, a.Length);
        Assert.Equal(a, new DeviceKeyStoreLocalDataKeyProvider(store).Key());
        Assert.Equal(a, store.Load(DeviceKeyStoreLocalDataKeyProvider.Account));
        // 같은 키로 잠근 파일은 다른 인스턴스도 연다
        var file = Path.Combine(TempDir(), "tabs.json");
        new LocalDataCrypto(new DeviceKeyStoreLocalDataKeyProvider(store)).WriteText(file, "hello");
        Assert.Equal("hello", new LocalDataCrypto(new DeviceKeyStoreLocalDataKeyProvider(store)).ReadText(file));
    }

    /// 키 파일이 망가졌으면(32바이트가 아님) 새로 만들어 덮어쓰지 않고 키 없음으로 본다 (예전 데이터를 잃지 않게)
    [Fact]
    public void BrokenKeyIsNotReplaced()
    {
        var store = new FileDeviceKeyStore(Path.Combine(TempDir(), "keys"));
        Assert.True(store.Save(DeviceKeyStoreLocalDataKeyProvider.Account, [1, 2, 3]));
        var c = new LocalDataCrypto(new DeviceKeyStoreLocalDataKeyProvider(store));
        Assert.False(c.IsKeyAvailable);
        Assert.False(c.WriteText(Path.Combine(TempDir(), "x.json"), "x"));
        Assert.Equal([1, 2, 3], store.Load(DeviceKeyStoreLocalDataKeyProvider.Account));
    }

    /// 1.4.3 파일 권한: 앱 데이터 폴더는 본인만 (Windows: 최상위 폴더 ACL 을 현재 사용자만 + 상속, 그 밖: 700),
    /// 암호화 파일도 본인만. 앱 폴더 밖은 건드리지 않는다.
    [Fact]
    public void DataFolderAndFilesAreUserOnly()
    {
        var sub = Path.Combine(AppPaths.DataDirectory, "perm-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            AppPaths.Ensure(sub);
            Assert.True(UserOnlyFile.IsDirectoryRestricted(OperatingSystem.IsWindows() ? AppPaths.DataDirectory : sub));
            var file = Path.Combine(sub, "tabs.json");
            crypto.WriteText(file, "x");
            Assert.True(UserOnlyFile.IsRestricted(file));
            Assert.True(AppPaths.IsInside(sub, AppPaths.DataDirectory));
            Assert.False(AppPaths.IsInside(_tmp.Path, AppPaths.DataDirectory));
            Assert.False(AppPaths.IsInside(AppPaths.DataDirectory + "-backup", AppPaths.DataDirectory));
        }
        finally { try { Directory.Delete(sub, true); } catch { } }
    }

    /// Mac CryptoKit 이 만든 봉투(nonce ‖ 암호문 ‖ tag)와 같은 배치: 직접 AesGcm 으로 만든 줄을 연다
    [Fact]
    public void OpensBoxBuiltLikeCryptoKit()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var c = new LocalDataCrypto(new StaticLocalDataKeyProvider(key));
        var nonce = Enumerable.Range(0, 12).Select(i => (byte)(0xA0 + i)).ToArray();
        var plain = Encoding.UTF8.GetBytes("""[{"text":"SELECT 1"}]""");
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        var doc = Encoding.ASCII.GetBytes("GDBENC1\n" + Convert.ToBase64String([.. nonce, .. cipher, .. tag]) + "\n");
        Assert.Equal(plain, c.DecryptDocument(doc));
    }
}
