using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace GodDB.Core.Platform;

// MARK: - 로컬 데이터 암호화 (쿼리 히스토리 · 탭 내용 · 저장 쿼리 · AI 대화 · Redis Workbench)
//
// 파일 형식 `GDBENC1` (Mac 판과 같은 형식. 키 보관만 OS 별로 다르다):
//
//     GDBENC1\n
//     <base64(sealed box)>\n
//     <base64(sealed box)>\n      ← 줄 단위 파일(history.jsonl)만 여러 줄
//
// - 첫 줄은 ASCII `GDBENC1` 한 줄 (끝 `\n`). 이 줄이 없으면 예전 평문 파일로 보고 한 번 옮긴다.
// - 그다음 줄마다 AES-256-GCM 봉투 하나: `nonce(12) ‖ ciphertext ‖ tag(16)` (CryptoKit `combined` 와 같은 순서) 를
//   표준 base64(줄바꿈 없음)로. 추가 인증 데이터(AAD)는 쓰지 않는다.
// - 통째 파일(tabs.json 등)은 봉투가 하나 = 원래 JSON 전체.
//   줄 단위 파일(history.jsonl)은 한 줄 = 한 건의 JSON (덧붙이기가 싸다).
// - 키: 무작위 256비트.
//   Mac 은 키체인(이 기기 전용, 동기화 안 함). Windows 는 DPAPI(현재 사용자)로 잠가 %LOCALAPPDATA%\GodDB\keys 에 둔다
//   (로밍되지 않는 폴더 + 사용자 계정에 묶인 DPAPI → 다른 사용자 · 다른 PC 에서는 풀 수 없다). 키체인과 같은 역할이라
//   자격 증명 관리자보다 DPAPI 를 골랐다: 자격 증명 관리자는 "로컬 컴퓨터" 범위라도 사용자 눈에 보이는 목록에 섞이고
//   크기 · 이름 제약이 있으며, 감사 로그 기기 키도 이미 같은 DPAPI 저장소(DpapiDeviceKeyStore)를 쓴다.
// - 봉투마다 위변조(GCM 태그)는 잡지만, 줄을 통째로 빼거나 순서를 바꾸는 것은 잡지 않는다 (기밀성이 목적).

/// 키가 없거나 복호화가 안 될 때의 종류
public enum LocalDataCryptoErrorKind
{
    /// 키를 읽거나 만들 수 없음 (Code: OS 오류 번호, 모르면 0)
    KeyUnavailable,
    /// `GDBENC1` 형식이 아님 (base64 · 봉투 모양이 틀림)
    Malformed,
    /// 위변조됐거나 다른 키로 잠긴 데이터
    AuthenticationFailed,
}

public sealed class LocalDataCryptoException(LocalDataCryptoErrorKind kind, int code = 0) : Exception(Describe(kind, code))
{
    public LocalDataCryptoErrorKind Kind { get; } = kind;
    public int Code { get; } = code;

    private static string Describe(LocalDataCryptoErrorKind kind, int code) => kind switch
    {
        LocalDataCryptoErrorKind.KeyUnavailable => $"local data key unavailable (code {code})",
        LocalDataCryptoErrorKind.Malformed => "not a GDBENC1 sealed box",
        _ => "local data failed authentication (tampered or wrong key)",
    };
}

/// 암호화 키를 주는 곳. 테스트는 메모리 키를 넣어 실제 키 저장소를 건드리지 않는다.
public interface ILocalDataKeyProvider
{
    /// 32바이트 키. 못 얻으면 <see cref="LocalDataCryptoException"/> (KeyUnavailable)
    byte[] Key();
}

/// 메모리 키 (테스트 · 자동 화면 테스트용)
public sealed class StaticLocalDataKeyProvider(byte[]? key = null) : ILocalDataKeyProvider
{
    public byte[] Value { get; } = key ?? RandomNumberGenerator.GetBytes(32);
    public byte[] Key() => Value;
}

/// 언제나 실패하는 키 (키 저장소 오류 흉내)
public sealed class UnavailableLocalDataKeyProvider : ILocalDataKeyProvider
{
    public byte[] Key() => throw new LocalDataCryptoException(LocalDataCryptoErrorKind.KeyUnavailable, -25308);
}

/// 기기 키 저장소(<see cref="IDeviceKeyStore"/>)에 둔 키: 처음이면 무작위 256비트를 만들어 둔다.
/// Windows 배포본은 DPAPI 저장소(GodDB.Platform.Windows.DpapiDeviceKeyStore), Mac/Linux 개발 실행은 본인만 읽는 파일.
public sealed class DeviceKeyStoreLocalDataKeyProvider(IDeviceKeyStore store) : ILocalDataKeyProvider
{
    /// Mac 키체인 계정 이름과 같게
    public const string Account = "local-data-key-v1";

    public byte[] Key()
    {
        if (Load() is { } existing) return existing;
        var bytes = RandomNumberGenerator.GetBytes(32);
        if (store.Save(Account, bytes)) return bytes;
        // 다른 창 · 앱이 먼저 만들었으면 그것을 쓴다 (저장소는 이미 있으면 덮어쓰지 않는다)
        if (Load() is { } other) return other;
        throw new LocalDataCryptoException(LocalDataCryptoErrorKind.KeyUnavailable);
    }

    private byte[]? Load()
    {
        var data = store.Load(Account);
        if (data == null) return null;
        if (data.Length != 32) throw new LocalDataCryptoException(LocalDataCryptoErrorKind.KeyUnavailable, -26275);
        return data;
    }
}

/// 통째 파일 읽기 결과
public enum LocalReadStatus
{
    Missing,
    /// 복호화한 내용
    Decrypted,
    /// 예전 평문 파일 (옮기지 못했으면 그대로 남아 있다)
    Plaintext,
    /// 암호화돼 있지만 키를 못 얻음 — 파일은 그대로 둔다
    KeyUnavailable,
    /// 위변조 · 다른 키 — 파일은 `<이름>.undecryptable` 로 옮겼다
    Damaged,
}

public sealed record LocalReadResult(LocalReadStatus Status, byte[]? Data = null)
{
    public static readonly LocalReadResult Missing = new(LocalReadStatus.Missing);
}

/// `GDBENC1` 암호화 · 복호화와 파일 읽기 · 쓰기.
/// 다른 기능이 쓰는 공용 진입점: <see cref="ReadText"/> / <see cref="WriteText"/> (통째 파일),
/// <see cref="ReadDocumentData"/> / <see cref="WriteDocument"/> (바이트), <see cref="PrefsString"/> / <see cref="SetPrefsString"/> (설정 값).
public sealed class LocalDataCrypto
{
    private static readonly Lock SharedGate = new();
    private static LocalDataCrypto? _shared;

    /// 앱 전체가 쓰는 것. 단위 테스트 프로세스와 화면 자동 테스트에서는 실제 키 저장소 대신 메모리 · 임시 키를 쓴다.
    public static LocalDataCrypto Shared
    {
        get
        {
            lock (SharedGate) return _shared ??= CreateDefault();
        }
    }

    /// 앱을 켤 때 OS 키 저장소를 넣는다 (Windows: DPAPI). 테스트 프로세스 · 화면 자동 테스트에서는 무시한다.
    public static void UsePlatformKeyStore(IDeviceKeyStore store)
    {
        if (IsTestProcess || AppPaths.UiTest) return;
        lock (SharedGate) _shared = new LocalDataCrypto(new DeviceKeyStoreLocalDataKeyProvider(store));
    }

    private static LocalDataCrypto CreateDefault()
    {
        if (IsTestProcess) return new LocalDataCrypto(new StaticLocalDataKeyProvider());
        if (AppPaths.UiTest)
        {
            // 임시 데이터 폴더마다 정해지는 키 (다시 켜도 탭이 열리게, 키 저장소는 건드리지 않음)
            var seed = Encoding.UTF8.GetBytes("GodDB-ui-test|" + AppPaths.DataDirectory);
            return new LocalDataCrypto(new StaticLocalDataKeyProvider(SHA256.HashData(seed)));
        }
        // OS 키 저장소를 받기 전 (Mac/Linux 개발 실행): 본인만 읽는 파일 키
        return new LocalDataCrypto(new DeviceKeyStoreLocalDataKeyProvider(new FileDeviceKeyStore()));
    }

    /// xUnit 테스트 프로세스인지
    internal static readonly bool IsTestProcess = AppDomain.CurrentDomain.GetAssemblies()
        .Any(a => a.GetName().Name is { } n && (n.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                                               || n == "Microsoft.TestPlatform.CoreUtilities"));

    /// 파일 첫 줄 (끝 `\n` 포함)
    public static readonly byte[] Header = "GDBENC1\n"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;

    internal static void Warn(string message) => Trace.TraceWarning("GodDB: " + message);

    private readonly ILocalDataKeyProvider _provider;
    private readonly Lock _lock = new();
    private byte[]? _cachedKey;

    public LocalDataCrypto(ILocalDataKeyProvider provider) => _provider = provider;

    /// 키 (성공한 키만 기억한다. 실패는 다음에 다시 시도)
    public byte[] Key()
    {
        lock (_lock)
        {
            if (_cachedKey != null) return _cachedKey;
            var k = _provider.Key();
            if (k.Length != 32) throw new LocalDataCryptoException(LocalDataCryptoErrorKind.KeyUnavailable, -26275);
            _cachedKey = k;
            return k;
        }
    }

    public bool IsKeyAvailable
    {
        get
        {
            try { Key(); return true; }
            catch (LocalDataCryptoException) { return false; }
            catch (CryptographicException) { return false; }
        }
    }

    // MARK: 봉투

    /// AES-GCM 봉투 (combined = nonce ‖ ciphertext ‖ tag)
    public byte[] Seal(byte[] plain)
    {
        var key = Key();
        var output = new byte[NonceSize + plain.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(NonceSize, plain.Length), output.AsSpan(NonceSize + plain.Length, TagSize));
        return output;
    }

    public byte[] Open(byte[] combined)
    {
        var key = Key();
        if (combined.Length < NonceSize + TagSize) throw new LocalDataCryptoException(LocalDataCryptoErrorKind.Malformed);
        var cipherLength = combined.Length - NonceSize - TagSize;
        var plain = new byte[cipherLength];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(combined.AsSpan(0, NonceSize), combined.AsSpan(NonceSize, cipherLength),
                        combined.AsSpan(NonceSize + cipherLength, TagSize), plain);
        }
        catch (CryptographicException) { throw new LocalDataCryptoException(LocalDataCryptoErrorKind.AuthenticationFailed); }
        return plain;
    }

    /// 한 줄 (base64, 줄바꿈 없음)
    public string SealLine(byte[] plain) => Convert.ToBase64String(Seal(plain));

    public byte[] OpenLine(string line)
    {
        byte[] data;
        try { data = Convert.FromBase64String(line.Trim()); }
        catch (FormatException) { throw new LocalDataCryptoException(LocalDataCryptoErrorKind.Malformed); }
        return Open(data);
    }

    // MARK: 문자열 (설정 파일 등에 넣을 때)

    /// `GDBENC1:` + base64 봉투
    public const string StringPrefix = "GDBENC1:";

    public string SealString(string text) => StringPrefix + SealLine(Encoding.UTF8.GetBytes(text));

    /// 접두어가 없으면 예전 평문으로 보고 그대로 돌려준다.
    public string OpenString(string stored)
    {
        if (!stored.StartsWith(StringPrefix, StringComparison.Ordinal)) return stored;
        return Encoding.UTF8.GetString(OpenLine(stored[StringPrefix.Length..]));
    }

    // MARK: 파일

    public static bool IsEncrypted(ReadOnlySpan<byte> data) => data.StartsWith(Header);

    /// 통째 파일 내용: 머리 줄 + 봉투 한 줄
    public byte[] EncryptDocument(byte[] plain) => [.. Header, .. Encoding.ASCII.GetBytes(SealLine(plain)), (byte)'\n'];

    /// 통째 파일 복호화 (봉투가 정확히 하나여야 한다)
    public byte[] DecryptDocument(byte[] data)
    {
        if (!IsEncrypted(data)) throw new LocalDataCryptoException(LocalDataCryptoErrorKind.Malformed);
        var lines = BodyLines(data);
        if (lines.Count != 1) throw new LocalDataCryptoException(LocalDataCryptoErrorKind.Malformed);
        return OpenLine(lines[0]);
    }

    /// 머리 줄 다음의 봉투 줄들 (빈 줄 제외)
    public static List<string> BodyLines(byte[] data) =>
        Encoding.UTF8.GetString(data, Header.Length, data.Length - Header.Length)
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

    /// 통째 파일 읽기. 예전 평문이면 바로 암호화해 바꿔 쓴다 (키가 없으면 평문은 손대지 않는다).
    public LocalReadResult ReadDocument(string path)
    {
        byte[] data;
        try
        {
            if (!File.Exists(path)) return LocalReadResult.Missing;
            data = File.ReadAllBytes(path);
        }
        catch { return LocalReadResult.Missing; }
        if (!IsEncrypted(data))
        {
            if (data.Length == 0) return new(LocalReadStatus.Plaintext, data);
            try { WriteDocument(data, path); }
            catch (Exception e) { Warn($"kept plaintext {Path.GetFileName(path)}, key unavailable: {e.Message}"); }
            return new(LocalReadStatus.Plaintext, data);
        }
        try
        {
            return new(LocalReadStatus.Decrypted, DecryptDocument(data));
        }
        catch (LocalDataCryptoException e) when (e.Kind == LocalDataCryptoErrorKind.KeyUnavailable)
        {
            Warn($"cannot read {Path.GetFileName(path)}, key unavailable");
            return new(LocalReadStatus.KeyUnavailable);
        }
        catch (Exception)
        {
            MoveAside(path);
            return new(LocalReadStatus.Damaged);
        }
    }

    /// 읽은 평문 (없거나 못 읽으면 null)
    public byte[]? ReadDocumentData(string path)
    {
        var r = ReadDocument(path);
        return r.Status is LocalReadStatus.Decrypted or LocalReadStatus.Plaintext ? r.Data : null;
    }

    /// 통째 파일 쓰기 (원자적, 본인만). 키가 없으면 throw 하고 아무것도 쓰지 않는다 (평문을 쓰지 않음).
    public void WriteDocument(byte[] plain, string path) => WriteAtomically(EncryptDocument(plain), path);

    /// 글 파일 읽기 (UTF-8). 없거나 키가 없거나 못 열면 null. 예전 평문 파일은 이때 암호화된다.
    public string? ReadText(string path) => ReadDocumentData(path) is { } d ? Encoding.UTF8.GetString(d) : null;

    /// 글 파일을 암호화해서 쓴다. 키가 없거나 쓰지 못하면 false (평문으로 쓰지 않음).
    public bool WriteText(string path, string text)
    {
        try
        {
            WriteDocument(Encoding.UTF8.GetBytes(text), path);
            return true;
        }
        catch (Exception e)
        {
            Warn($"{Path.GetFileName(path)} not saved ({e.Message})");
            return false;
        }
    }

    /// 임시 파일에 쓰고 본인만 접근하게 한 뒤 바꿔 넣는다 (Mac: 원자적 쓰기 + 권한 600)
    public static void WriteAtomically(byte[] data, string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) AppPaths.Ensure(dir);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllBytes(tmp, data);
            UserOnlyFile.Restrict(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    // MARK: 설정(Prefs, Mac UserDefaults)에 두는 글

    /// 암호화해 둔 설정 글 읽기. 예전 평문이면 그대로 돌려주고 암호화해 바꿔 둔다. 못 열면 null.
    public string? PrefsString(string key) => PrefsString(key, Prefs.GetString, Prefs.Set);

    /// 암호화해서 설정에 저장. 키가 없으면 쓰지 않는다 (평문으로 쓰지 않음).
    public void SetPrefsString(string key, string text) => SetPrefsString(key, text, Prefs.Set);

    /// 테스트용: 읽기 · 쓰기를 넘긴다
    public string? PrefsString(string key, Func<string, string?> get, Action<string, string> set)
    {
        if (get(key) is not { } stored) return null;
        if (!stored.StartsWith(StringPrefix, StringComparison.Ordinal))
        {
            try { set(key, SealString(stored)); } catch (Exception) { }
            return stored;
        }
        try { return OpenString(stored); } catch (Exception) { return null; }
    }

    public void SetPrefsString(string key, string text, Action<string, string> set)
    {
        string sealedText;
        try { sealedText = SealString(text); } catch (Exception) { return; }
        set(key, sealedText);
    }

    /// 열 수 없는 파일을 지우지 않고 옆으로 치운다 (`<이름>.undecryptable`, 이전 것은 덮어씀)
    public static void MoveAside(string path)
    {
        var dest = path + ".undecryptable";
        try { File.Move(path, dest, overwrite: true); } catch { }
        Warn($"{Path.GetFileName(path)} could not be decrypted, moved to {Path.GetFileName(dest)}");
    }
}
