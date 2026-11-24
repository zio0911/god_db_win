using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using static GodDB.Core.L10n;

namespace GodDB.Core.Drivers;

// F9-3. TLS 서버 인증서 확인 (MySQL · PostgreSQL · Redis · SQL Server — SQL Server 는 MssqlTls.cs)
// 직접 접속: 시스템 신뢰 저장소로 체인 + 호스트 이름 확인 → 실패하면 고정 지문(TlsPinSHA256) 비교.
// SSH 터널 경유: 이름 확인을 하지 않고, 고정 지문이 있으면 지문만 비교.
// Mac 은 Security.framework(SecTrust) + NIOSSL 콜백, Windows 는 X509Chain(Windows 인증서 저장소) + SslStream 의
// RemoteCertificateValidationCallback 으로 같은 판정을 한다 (MySqlConnector · Npgsql · Redis 모두 이 콜백을 받는다).

/// 접속할 때 인증서를 어떻게 확인할지
/// - Verify: 서버 인증서 확인 (접속 편집의 "서버 인증서 확인")
/// - Hostname: 체인 · 이름을 확인할 호스트 이름. null 이면 SSH 터널 경유라 지문만 확인한다.
/// - Pin: 고정한 인증서 지문 (SHA-256)
public sealed record TlsPolicy(bool Verify, string? Hostname, string? Pin)
{
    /// 접속 정보로 정한다. 터널이면 Hostname 없음.
    public static TlsPolicy Of(ConnectionProfile profile, string? host = null)
    {
        var pin = string.IsNullOrEmpty(profile.TlsPinSHA256) ? null : profile.TlsPinSHA256;
        return new TlsPolicy(profile.TlsVerify, profile.Ssh == null ? host ?? profile.Host : null, pin);
    }
}

/// 인증서 확인을 통과하지 못한 이유
public enum TlsFailureReason { UntrustedIssuer, NameMismatch, Expired }

/// 판정 종류
public enum TlsVerdictKind
{
    /// 시스템 신뢰 저장소로 체인 · 이름 확인 통과
    Trusted,
    /// 체인 확인은 안 됐지만(또는 터널이라 하지 않았지만) 고정 지문과 같음
    Pinned,
    /// 터널 경유 · 고정 지문 없음 → 확인하지 않음
    Unchecked,
    /// 체인 확인 실패 · 고정 지문 없음 → 사용자에게 묻는다
    Untrusted,
    /// 고정 지문과 다름 → 연결하지 않는다
    PinMismatch,
}

/// 확인 결과 (Mac TLSVerdict — 연관 값이 있는 enum 이라 종류 + 이유 목록으로 옮겼다)
public sealed record TlsVerdict(TlsVerdictKind Kind, IReadOnlyList<TlsFailureReason> Reasons)
{
    public static readonly TlsVerdict Trusted = new(TlsVerdictKind.Trusted, []);
    public static readonly TlsVerdict Pinned = new(TlsVerdictKind.Pinned, []);
    public static readonly TlsVerdict Unchecked = new(TlsVerdictKind.Unchecked, []);
    public static readonly TlsVerdict PinMismatch = new(TlsVerdictKind.PinMismatch, []);
    public static TlsVerdict Untrusted(params TlsFailureReason[] reasons) => new(TlsVerdictKind.Untrusted, reasons);

    public bool AllowsConnection => Kind is TlsVerdictKind.Trusted or TlsVerdictKind.Pinned or TlsVerdictKind.Unchecked;

    public bool Equals(TlsVerdict? other) =>
        other is not null && Kind == other.Kind && Reasons.SequenceEqual(other.Reasons);

    public override int GetHashCode() => HashCode.Combine(Kind, Reasons.Count);

    public override string ToString() =>
        Reasons.Count == 0 ? Kind.ToString() : $"{Kind}({string.Join(", ", Reasons)})";
}

/// 연결 후 상태창에 남기는 TLS 상태
public enum TlsStatus
{
    Verified,
    Pinned,
    TunnelUnchecked,
    /// "서버 인증서 확인" 을 끔
    Unchecked,
    Off,
}

public static class TlsTextExtensions
{
    public static string Text(this TlsFailureReason r) => r switch
    {
        TlsFailureReason.UntrustedIssuer => Tr("신뢰할 수 없는 발급자"),
        TlsFailureReason.NameMismatch => Tr("이름이 다름"),
        _ => Tr("만료됨"),
    };

    public static string Text(this TlsStatus s) => s switch
    {
        TlsStatus.Verified => Tr("TLS 사용 · 인증서 확인됨"),
        TlsStatus.Pinned => Tr("TLS 사용 · 고정 인증서"),
        TlsStatus.TunnelUnchecked => Tr("TLS 사용 · 확인 안 함(SSH 터널)"),
        TlsStatus.Unchecked => Tr("TLS 사용 · 확인 안 함"),
        _ => Tr("TLS 안 씀"),
    };
}

/// 인증서 정보 (확인 창에 보여 줌)
/// - Sha256: 맨 앞(서버) 인증서 DER 의 SHA-256, "3F:A2:…" 형식
public sealed record TlsCertInfo(string Subject, string Issuer, DateTimeOffset? NotBefore, DateTimeOffset? NotAfter, string Sha256)
{
    /// "2026-01-01 ~ 2027-01-01" (Mac 처럼 이 PC 의 시간대 기준 날짜)
    public string ValidityText
    {
        get
        {
            static string D(DateTimeOffset? d) => d is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "?";
            return $"{D(NotBefore)} ~ {D(NotAfter)}";
        }
    }
}

public enum TlsCertificateErrorKind { Untrusted, PinMismatch, ServerNoTls }

/// 인증서 때문에 연결하지 않았을 때. 화면이 이 정보를 보여 주고, 신뢰하면 지문을 저장한 뒤 다시 연결한다.
public sealed class TlsCertificateException : Exception
{
    public TlsCertificateErrorKind Kind { get; }
    public TlsCertInfo? Info { get; }
    public IReadOnlyList<TlsFailureReason> Reasons { get; }
    /// PinMismatch: 저장돼 있던 지문
    public string? Expected { get; }

    private TlsCertificateException(TlsCertificateErrorKind kind, TlsCertInfo? info, IReadOnlyList<TlsFailureReason> reasons, string? expected)
        : base(Describe(kind, info, reasons))
    {
        Kind = kind;
        Info = info;
        Reasons = reasons;
        Expected = expected;
    }

    public static TlsCertificateException Untrusted(TlsCertInfo info, IReadOnlyList<TlsFailureReason> reasons) =>
        new(TlsCertificateErrorKind.Untrusted, info, reasons, null);
    public static TlsCertificateException PinMismatch(TlsCertInfo info, string expected) =>
        new(TlsCertificateErrorKind.PinMismatch, info, [], expected);
    /// TLS 를 켰는데 서버가 TLS 를 하지 않음
    public static TlsCertificateException ServerNoTls() => new(TlsCertificateErrorKind.ServerNoTls, null, [], null);

    private static string Describe(TlsCertificateErrorKind kind, TlsCertInfo? info, IReadOnlyList<TlsFailureReason> reasons) => kind switch
    {
        TlsCertificateErrorKind.Untrusted => Tr("서버 인증서를 확인할 수 없어요 (%@). SHA-256: %@",
            string.Join(", ", reasons.Select(r => r.Text())), info?.Sha256),
        TlsCertificateErrorKind.PinMismatch => Tr("서버 인증서가 바뀌었어요. 서버를 교체한 게 아니라면 중간에서 가로채는 공격일 수 있어요. 관리자에게 확인한 뒤 [저장된 인증서 지우기] 를 누르세요."),
        _ => Tr("서버가 TLS 를 지원하지 않아요. TLS 를 끄거나 SSH 터널을 쓰세요."),
    };

    /// 같은 오류인지 (테스트용 — Mac 의 Equatable)
    public bool SameAs(TlsCertificateException other) =>
        Kind == other.Kind && Equals(Info, other.Info) && Reasons.SequenceEqual(other.Reasons) && Expected == other.Expected;
}

public static class TlsVerify
{
    // MARK: - 지문

    /// 바이트 → "3F:A2:…:9C"
    public static string Fingerprint(IEnumerable<byte> bytes) =>
        string.Join(":", bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    /// DER 의 SHA-256 지문
    public static string Sha256Fingerprint(byte[] der) => Fingerprint(SHA256.HashData(der));

    /// 비교용: 콜론 · 공백 · "SHA256:" 접두사를 빼고 대문자로
    public static string Normalize(string fingerprint)
    {
        var s = fingerprint.Trim();
        if (s.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)) s = s[7..];
        return new string(s.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
    }

    public static bool PinMatches(string pin, string fingerprint)
    {
        var a = Normalize(pin);
        return a.Length > 0 && a == Normalize(fingerprint);
    }

    /// PEM(첫 인증서) → DER
    public static byte[]? DerFromPem(string pem)
    {
        const string begin = "-----BEGIN CERTIFICATE-----", end = "-----END CERTIFICATE-----";
        var s = pem.IndexOf(begin, StringComparison.Ordinal);
        if (s < 0) return null;
        var e = pem.IndexOf(end, s + begin.Length, StringComparison.Ordinal);
        if (e < 0) return null;
        var body = new string(pem[(s + begin.Length)..e].Where(c => !char.IsWhiteSpace(c)).ToArray());
        try { return Convert.FromBase64String(body); } catch (FormatException) { return null; }
    }

    // MARK: - 판정

    /// 판정 표.
    /// - checkChain: 직접 접속이면 true (체인 · 이름 확인 결과를 본다), 터널이면 false
    /// - chainOK: 시스템 신뢰 저장소 확인 통과 여부
    public static TlsVerdict Decide(bool checkChain, bool chainOK, IReadOnlyList<TlsFailureReason> reasons, string leafSHA256, string? pin)
    {
        if (pin != null && Normalize(pin).Length > 0)
        {
            // 고정한 인증서가 있으면 지문이 달라질 때 항상 거부 (체인이 통과해도)
            if (!PinMatches(pin, leafSHA256)) return TlsVerdict.PinMismatch;
            return checkChain && chainOK ? TlsVerdict.Trusted : TlsVerdict.Pinned;
        }
        if (!checkChain) return TlsVerdict.Unchecked;
        return chainOK ? TlsVerdict.Trusted : TlsVerdict.Untrusted(reasons.Count == 0 ? [TlsFailureReason.UntrustedIssuer] : [.. reasons]);
    }

    /// 판정 → 상태창 문구
    public static TlsStatus Status(TlsVerdict verdict) => verdict.Kind switch
    {
        TlsVerdictKind.Trusted => TlsStatus.Verified,
        TlsVerdictKind.Pinned => TlsStatus.Pinned,
        _ => TlsStatus.TunnelUnchecked,
    };

    // MARK: - 인증서 정보

    /// DER → 주체 · 발급자 · 유효 기간 · 지문
    public static TlsCertInfo CertInfo(byte[] der)
    {
        var sha = Sha256Fingerprint(der);
        try
        {
            using var cert = X509CertificateLoader.LoadCertificate(der);
            return new TlsCertInfo(NameText(cert.SubjectName), NameText(cert.IssuerName),
                new DateTimeOffset(cert.NotBefore.ToUniversalTime(), TimeSpan.Zero),
                new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero), sha);
        }
        catch (CryptographicException)
        {
            return new TlsCertInfo("?", "?", null, null, sha);
        }
    }

    /// "CN=db.example.com, O=Example" — CN 을 먼저 (나머지는 인증서에 적힌 순서)
    internal static string NameText(X500DistinguishedName name)
    {
        static string? Label(string? oid) => oid switch
        {
            "2.5.4.3" => "CN",
            "2.5.4.10" => "O",
            "2.5.4.11" => "OU",
            "2.5.4.6" => "C",
            "2.5.4.8" => "ST",
            "2.5.4.7" => "L",
            _ => null,
        };
        var parts = new List<(string Key, string Value)>();
        try
        {
            foreach (var rdn in name.EnumerateRelativeDistinguishedNames(reversed: false))
            {
                if (rdn.HasMultipleElements) continue;
                if (Label(rdn.GetSingleElementType().Value) is { } key && rdn.GetSingleElementValue() is { } value)
                    parts.Add((key, value));
            }
        }
        catch (CryptographicException) { }
        var ordered = parts.Where(p => p.Key == "CN").Concat(parts.Where(p => p.Key != "CN"));
        var text = string.Join(", ", ordered.Select(p => $"{p.Key}={p.Value}"));
        return text.Length == 0 ? "?" : text;
    }

    // MARK: - 시스템 신뢰 저장소 확인 (X509Chain)

    /// 체인을 시스템 신뢰 저장소로 확인한다. (Mac: SecTrust + SecPolicyCreateSSL)
    /// 실패하면 체인 상태 · 이름 확인 결과를 Mac 과 같은 이름(TemporalValidity · SSLHostname · 그 밖)으로 모아 이유를 나눈다.
    /// 폐기 확인은 하지 않는다 — Mac 의 SSL 정책도 폐기 정보를 못 받으면 통과시키고(최선 노력),
    /// Windows 에서 폐기 서버에 못 닿으면 연결마다 수십 초 기다리게 되기 때문.
    /// - hostname: null 이면 이름 확인을 하지 않는다
    public static (bool Ok, List<TlsFailureReason> Reasons) Evaluate(IReadOnlyList<byte[]> ders, string? hostname, DateTime? now = null)
    {
        if (ders.Count == 0) return (false, [TlsFailureReason.UntrustedIssuer]);
        X509Certificate2 leaf;
        try { leaf = X509CertificateLoader.LoadCertificate(ders[0]); }
        catch (CryptographicException) { return (false, [TlsFailureReason.UntrustedIssuer]); }
        var extras = new List<X509Certificate2>();
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationTime = now ?? DateTime.Now;
            // 서버 인증용 (SecPolicyCreateSSL(true, …) 과 같은 용도 확인)
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            foreach (var der in ders.Skip(1))
            {
                try
                {
                    var c = X509CertificateLoader.LoadCertificate(der);
                    extras.Add(c);
                    chain.ChainPolicy.ExtraStore.Add(c);
                }
                catch (CryptographicException) { }
            }
            var failed = new HashSet<string>(StringComparer.Ordinal);
            var ok = chain.Build(leaf);
            foreach (var st in chain.ChainStatus) AddFlags(failed, st.Status);
            foreach (var el in chain.ChainElements)
                foreach (var st in el.ChainElementStatus) AddFlags(failed, st.Status);
            if (failed.Count > 0) ok = false;
            if (hostname != null && !MatchesHostname(leaf, hostname))
            {
                ok = false;
                failed.Add("SSLHostname");
            }
            return ok ? (true, []) : (false, Reasons(failed));
        }
        finally
        {
            leaf.Dispose();
            foreach (var c in extras) c.Dispose();
        }
    }

    private static void AddFlags(HashSet<string> failed, X509ChainStatusFlags flags)
    {
        foreach (X509ChainStatusFlags f in Enum.GetValues<X509ChainStatusFlags>())
        {
            if (f == X509ChainStatusFlags.NoError || (flags & f) == 0) continue;
            failed.Add(f is X509ChainStatusFlags.NotTimeValid or X509ChainStatusFlags.NotTimeNested ? "TemporalValidity" : f.ToString());
        }
    }

    private static bool MatchesHostname(X509Certificate2 cert, string hostname)
    {
        try { return cert.MatchesHostname(hostname, allowWildcards: true, allowCommonName: true); }
        catch (ArgumentException) { return false; }
    }

    /// 실패 항목 이름 → 이유 (이름 · 기간 말고는 모두 신뢰할 수 없는 발급자로 본다). 이름은 Mac SecTrust 상세와 같다.
    public static List<TlsFailureReason> Reasons(ISet<string> failedChecks)
    {
        var r = new List<TlsFailureReason>();
        if (failedChecks.Count == 0 || failedChecks.Any(c => c is not ("SSLHostname" or "TemporalValidity")))
            r.Add(TlsFailureReason.UntrustedIssuer);
        if (failedChecks.Contains("SSLHostname")) r.Add(TlsFailureReason.NameMismatch);
        if (failedChecks.Contains("TemporalValidity")) r.Add(TlsFailureReason.Expired);
        return r;
    }

    /// DER → PEM
    public static string Pem(byte[] der)
    {
        var b64 = Convert.ToBase64String(der);
        var sb = new StringBuilder("-----BEGIN CERTIFICATE-----\n");
        for (var i = 0; i < b64.Length; i += 64) sb.Append(b64, i, Math.Min(64, b64.Length - i)).Append('\n');
        return sb.Append("-----END CERTIFICATE-----\n").ToString();
    }
}

/// 연결 한 번의 인증서 확인. SslStream 콜백에서 판정하고 결과를 기억한다 (연결 오류를 인증서 오류로 바꾸는 데 씀).
public sealed class TlsVerifier(TlsPolicy policy)
{
    public TlsPolicy Policy { get; } = policy;
    private readonly Lock _lock = new();
    private TlsVerdict? _verdict;
    private TlsCertInfo? _info;
    private List<byte[]>? _chain;

    public TlsVerdict? Verdict { get { lock (_lock) return _verdict; } }
    public TlsCertInfo? CertInfo { get { lock (_lock) return _info; } }
    /// 마지막으로 판정한 체인 (서버 인증서 먼저)
    public IReadOnlyList<byte[]>? Chain { get { lock (_lock) return _chain; } }

    /// 연결 후 상태창 문구
    public TlsStatus Status
    {
        get
        {
            if (!Policy.Verify) return TlsStatus.Unchecked;
            return Verdict is { } v ? TlsVerify.Status(v) : Policy.Hostname == null ? TlsStatus.TunnelUnchecked : TlsStatus.Verified;
        }
    }

    /// 확인 결과가 거부면 화면용 오류
    public TlsCertificateException? Failure
    {
        get
        {
            lock (_lock)
            {
                if (_verdict is not { } v || _info is not { } info) return null;
                return v.Kind switch
                {
                    TlsVerdictKind.Untrusted => TlsCertificateException.Untrusted(info, v.Reasons),
                    TlsVerdictKind.PinMismatch => TlsCertificateException.PinMismatch(info, Policy.Pin ?? ""),
                    _ => null,
                };
            }
        }
    }

    /// 체인을 판정한다 (동기 — 테스트 · 콜백 공용)
    public TlsVerdict Judge(IReadOnlyList<byte[]> ders)
    {
        if (ders.Count == 0) return TlsVerdict.Untrusted(TlsFailureReason.UntrustedIssuer);
        var info = TlsVerify.CertInfo(ders[0]);
        var checkChain = Policy.Hostname != null;
        // 고정 지문이 맞으면 체인 확인을 건너뛸 수 있지만, "확인됨" 표시를 위해 직접 접속이면 확인한다
        var result = checkChain ? TlsVerify.Evaluate(ders, Policy.Hostname) : (Ok: false, Reasons: []);
        var v = TlsVerify.Decide(checkChain, result.Ok, result.Reasons, info.Sha256, Policy.Pin);
        lock (_lock)
        {
            // 여러 연결(주소 · 노드) 중 거부가 한 번이라도 있으면 그 결과를 남긴다
            if (_verdict == null || !v.AllowsConnection)
            {
                _verdict = v;
                _info = info;
                _chain = [.. ders];
            }
        }
        return v;
    }

    /// SslStream 확인 콜백 (MySqlConnector · Npgsql · Redis · SQL Server 미리 받기 공용)
    public RemoteCertificateValidationCallback Callback => (_, certificate, chain, _) =>
        certificate != null && Judge(Collect(certificate, chain)).AllowsConnection;

    /// 서버가 보낸 인증서들 (서버 인증서 먼저). 중간 인증서는 .NET 이 콜백 체인의 ExtraStore 에 넣어 준다.
    internal static List<byte[]> Collect(X509Certificate certificate, X509Chain? chain)
    {
        var leaf = certificate.GetRawCertData();
        var list = new List<byte[]> { leaf };
        void Add(byte[] der)
        {
            if (!list.Any(d => d.AsSpan().SequenceEqual(der))) list.Add(der);
        }
        if (chain != null)
        {
            foreach (var c in chain.ChainPolicy.ExtraStore) Add(c.RawData);
            if (list.Count == 1)
                foreach (var el in chain.ChainElements.Skip(1)) Add(el.Certificate.RawData);
        }
        return list;
    }

    /// 연결 오류를 인증서 오류로 바꾼다 (확인에서 거부했으면)
    public Exception Map(Exception error) => Failure ?? error;
}
