using System.Security.Cryptography;
using System.Text;
using GodDB.Core.Platform;

namespace GodDB.Core.Audit;

/// 감사 로그 위변조 확인 — 파일만 있으면 어디서든 검사할 수 있는 방식 (Mac 판과 같은 형식).
/// - 줄마다 `무결성` = SHA-256(이전 줄 무결성 + "\n" + 이 줄 내용) 의 소문자 16진수
///   (해시 체인: 한 줄이라도 고치거나 지우거나 끼워 넣으면 그 줄부터 깨짐)
/// - 줄마다 `서명` = 이 PC 의 기기 키로 무결성 값(UTF-8)에 전자서명 (ECDSA P-256, r‖s 64바이트 base64)
/// - 날짜 파일 첫 줄("체인")에 검증용 공개 키(`key:종류:X‖Y base64`)와 이전 날짜 파일의 마지막 무결성 값을 남긴다.
/// 사용자 화면에는 아무것도 안 보인다.
/// 한계: 같은 PC 에서 앱과 같은 키로 다시 서명하는 특수한 조작은 파일만으로는 못 막는다 (외부 저장소가 필요).
public static class AuditChain
{
    public static readonly string Zero = new('0', 64);
    /// 파일 머리글에 붙는 칸 이름
    public static readonly IReadOnlyList<string> Columns = ["무결성", "서명"];

    // MARK: 기기 서명 키

    /// 플랫폼 기기 키 (Windows 판: TPM 키). 없거나 null 을 돌려주면 소프트웨어 키를 쓴다.
    /// Mac 판의 Secure Enclave 우선과 같은 자리.
    public static Func<IDeviceSigner?>? PlatformSigner { get; set; }

    /// 소프트웨어 키 보관소 (기본: 본인만 읽을 수 있는 파일). Windows 판은 DPAPI 저장소로 바꿀 수 있다.
    public static IDeviceKeyStore KeyStore { get; set; } = new FileDeviceKeyStore();

    /// 날짜 파일이 바뀔 때 이전 파일 마지막 무결성 값을 남길 시스템 로그 (Mac: os_log).
    /// Windows 판은 이벤트 로그 등으로 연결할 수 있다. 없으면 남기지 않는다.
    public static Action<string>? SystemLog { get; set; }

    private static readonly Lock CacheLock = new();
    private static IDeviceSigner? _cached;

    private const string SoftwareAccount = "device-key-sw";

    /// 이 PC 의 기기 키. 처음이면 만든다 (플랫폼 키 우선). 키를 저장할 수 없으면 null.
    public static IDeviceSigner? DeviceSigner()
    {
        lock (CacheLock)
        {
            if (_cached != null) return _cached;
            try
            {
                if (PlatformSigner?.Invoke() is { } platform) return _cached = platform;
            }
            catch { }
            if (KeyStore.Load(SoftwareAccount) is { } data && SoftwareDeviceSigner.FromPkcs8(data) is { } loaded)
                return _cached = loaded;
            var key = new SoftwareDeviceSigner();
            if (!KeyStore.Save(SoftwareAccount, key.ExportPkcs8())) { key.Dispose(); return null; }
            return _cached = key;
        }
    }

    /// 캐시한 기기 키를 버린다 (키 보관소를 바꾼 뒤·테스트용)
    public static void ResetDeviceSigner()
    {
        lock (CacheLock) _cached = null;
    }

    /// 서명 → base64 (실패하면 null)
    public static string? Sign(IDeviceSigner signer, byte[] data)
    {
        try { return Convert.ToBase64String(signer.Sign(data)); }
        catch { return null; }
    }

    public static string PublicKeyText(IDeviceSigner signer) => Convert.ToBase64String(signer.PublicKeyRaw);

    // MARK: 해시

    public static string Hash(string prev, string line) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prev + "\n" + line)));

    /// 파일 체인의 시작 값 (파일 이름에 묶어서 다른 날짜로 바꿔치기해도 드러나게)
    public static string Seed(string fileName) => Hash(Zero, "GodDB-audit|" + fileName);

    /// 파일 첫 줄: 검증용 공개 키 + 이전 파일과의 연결 (6칸 — 무결성·서명은 쓰는 쪽에서 붙인다)
    public static string LinkLine(string time, string identity, string? previousFile, string previousHash, IDeviceSigner signer) =>
        string.Join('\t', time, identity, "체인", previousFile ?? "-", $"key:{signer.Kind}:{PublicKeyText(signer)}", previousHash);

    /// 한 줄에 붙일 "무결성\t서명"
    public static (string Hash, string Suffix) Seal(string prev, string line, IDeviceSigner signer)
    {
        var h = Hash(prev, line);
        return (h, h + "\t" + (Sign(signer, Encoding.UTF8.GetBytes(h)) ?? "-"));
    }

    /// 시스템 로그에 봉인 기록
    public static void SealToSystemLog(string file, string lastHash)
    {
        try { SystemLog?.Invoke($"GodDB audit seal file={file} last={lastHash}"); }
        catch { }
    }

    // MARK: 검사 (파일만으로)

    /// 한 파일 검사. expectedPrevious 를 주면 첫 "체인" 줄이 그 값과 같은지도 본다.
    public static AuditFileReport Verify(string fileName, string text, string? expectedPrevious)
    {
        var rows = text.Split('\n').ToList();
        if (rows.Count > 0 && rows[^1] == "") rows.RemoveAt(rows.Count - 1);
        if (rows.Count == 0) return new AuditFileReport(fileName, 0, AuditFileStatus.Ok.Instance);
        var header = rows[0];
        if (!header.EndsWith("\t" + string.Join('\t', Columns), StringComparison.Ordinal))
            return new AuditFileReport(fileName, Math.Max(0, rows.Count - 1), AuditFileStatus.Legacy.Instance);
        var prev = Seed(fileName);
        byte[]? key = null;
        string? keyText = null;
        AuditFileStatus status = AuditFileStatus.Ok.Instance;
        int width = AuditRecord.Header.Count + Columns.Count;
        AuditFileReport Fail(int line, string reason) =>
            new(fileName, rows.Count - 1, new AuditFileStatus.Broken(line, reason), PublicKey: keyText);

        for (int i = 1; i < rows.Count; i++)
        {
            var cols = rows[i].Split('\t').ToList();
            if (cols.Count != width) return Fail(i + 1, $"칸 수가 달라요 ({cols.Count}칸)");
            var sig = cols[^1];
            var h = cols[^2];
            cols.RemoveRange(cols.Count - 2, 2);
            var line = string.Join('\t', cols);
            if (i == 1)
            {
                // 첫 줄: 체인 시작 + 공개 키
                if (cols[2] != "체인" || !cols[4].StartsWith("key:", StringComparison.Ordinal)) return Fail(2, "체인 시작 줄이 없어요");
                var parts = cols[4].Split(':', 3);
                byte[]? raw = parts.Length == 3 ? FromBase64(parts[2]) : null;
                if (raw == null || !IsValidPublicKey(raw)) return Fail(2, "공개 키가 손상됐어요");
                key = raw;
                keyText = parts[2];
            }
            if (h != Hash(prev, line))
                return Fail(i + 1, i == 1 ? "첫 줄이 바뀌었어요" : "이 줄이 바뀌었거나, 앞 줄이 지워졌거나 끼워 넣어졌어요");
            var sigData = FromBase64(sig);
            if (key == null || sigData == null || !SoftwareDeviceSigner.Verify(key, Encoding.UTF8.GetBytes(h), sigData))
                return Fail(i + 1, "서명이 맞지 않아요 (이 PC 의 기기 키로 쓴 줄이 아니에요)");
            if (i == 1 && expectedPrevious != null && cols[5] != expectedPrevious)
                status = new AuditFileStatus.Unlinked($"이전 파일({cols[3]})의 마지막 무결성 값과 달라요 — 이전 파일이 지워졌거나 바뀌었어요");
            prev = h;
        }
        return new AuditFileReport(fileName, rows.Count - 1, status, prev, keyText);
    }

    /// 폴더의 감사 로그를 날짜 순으로 검사 (파일 사이 연결, 기기 키가 바뀐 곳 포함)
    public static List<AuditFileReport> Verify(string directory)
    {
        var files = Directory.Exists(directory)
            ? Directory.GetFiles(directory)
                .Where(f => Path.GetFileName(f).StartsWith("goddb-audit-", StringComparison.Ordinal) && Path.GetExtension(f) == ".tsv")
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToList()
            : [];
        var output = new List<AuditFileReport>();
        AuditFileReport? previous = null;
        foreach (var f in files)
        {
            string text;
            try { text = File.ReadAllText(f, Encoding.UTF8); } catch { text = ""; }
            var r = Verify(Path.GetFileName(f), text, previous?.LastHash);
            if (r.Status is AuditFileStatus.Ok && previous?.PublicKey is { } p && r.PublicKey is { } k && p != k)
                r = r with { Status = new AuditFileStatus.Unlinked("기기 키가 바뀌었어요 (다른 PC·재설치 또는 다시 만든 파일)") };
            output.Add(r);
            previous = r;
        }
        return output;
    }

    /// 사람이 읽을 결과 한 줄
    public static string Describe(AuditFileReport r) => r.Status switch
    {
        AuditFileStatus.Ok => $"정상    {r.File} · {r.Lines}줄",
        AuditFileStatus.Broken b => $"변조    {r.File} · {b.Line}번째 줄부터 · {b.Reason}",
        AuditFileStatus.Unlinked u => $"의심    {r.File} · {u.Reason}",
        _ => $"검사불가 {r.File} · {r.Lines}줄 · 예전 형식(무결성 칸 없음)",
    };

    private static byte[]? FromBase64(string s)
    {
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }

    private static bool IsValidPublicKey(byte[] raw)
    {
        using var k = SoftwareDeviceSigner.ImportPublicKey(raw);
        return k != null;
    }
}

/// 감사 로그 파일 검사 결과
public abstract record AuditFileStatus
{
    public sealed record Ok : AuditFileStatus { public static readonly Ok Instance = new(); }
    /// 몇 번째 줄(파일 기준, 머리글 = 1)부터 깨졌는지
    public sealed record Broken(int Line, string Reason) : AuditFileStatus;
    /// 이전 파일과 연결이 안 됨 (이전 파일이 지워졌거나 바뀜)
    public sealed record Unlinked(string Reason) : AuditFileStatus;
    /// 무결성 칸이 없는 예전 형식
    public sealed record Legacy : AuditFileStatus { public static readonly Legacy Instance = new(); }
}

/// <param name="PublicKey">이 파일을 서명한 기기 공개 키 (base64)</param>
public sealed record AuditFileReport(string File, int Lines, AuditFileStatus Status, string? LastHash = null, string? PublicKey = null)
{
    public string Id => File;
}
