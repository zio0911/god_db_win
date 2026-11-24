using CommunityToolkit.Mvvm.ComponentModel;
using SP = GodDB.App.ViewModels.SecurityPrompt;

namespace GodDB.App.ViewModels;

// F9-3 TLS 서버 인증서 확인 · F9-4 SSH 호스트 키 확인 — 확인 창 상태와 처리 (Mac SecurityPrompts.swift)
// 1.4.1 저장된 비밀번호를 연결 뒤 메모리에 두지 않기 (Mac AppState.keychainPasswordProvider)

/// 연결 중 만난 인증서 · 호스트 키 문제
public abstract record SecurityPrompt
{
    /// 체인 · 이름 확인 실패 → [이 인증서 신뢰 (고정)]
    public sealed record TlsUntrusted(TlsCertInfo Info, IReadOnlyList<TlsFailureReason> Reasons) : SecurityPrompt;
    /// 고정한 인증서와 다름 → [저장된 인증서 지우기]
    public sealed record TlsPinMismatch(TlsCertInfo Info) : SecurityPrompt;
    /// 처음 접속하는 SSH 서버 → [신뢰하고 연결]
    public sealed record SshUnknown(SshHostScan Scan) : SecurityPrompt;
    /// SSH 서버 키가 바뀜 → [저장된 키 지우기]
    public sealed record SshChanged(string Host, int Port, string Pattern) : SecurityPrompt;

    /// 연결 오류에서 확인 창을 고른다 (해당 없으면 null)
    public static SecurityPrompt? From(Exception error)
    {
        for (var e = error; e != null; e = e.InnerException)
        {
            switch (e)
            {
                case TlsCertificateException t:
                    return t.Kind switch
                    {
                        TlsCertificateErrorKind.Untrusted when t.Info is { } info => new TlsUntrusted(info, t.Reasons),
                        TlsCertificateErrorKind.PinMismatch when t.Info is { } info => new TlsPinMismatch(info),
                        _ => null,
                    };
                case SshHostKeyException s:
                    return s.Kind switch
                    {
                        SshHostKeyErrorKind.Unknown when s.Scan is { } scan => new SshUnknown(scan),
                        SshHostKeyErrorKind.Changed => new SshChanged(s.Host, s.Port, s.Pattern),
                        _ => null,
                    };
            }
        }
        return null;
    }

    public string AcceptTitle => this switch
    {
        TlsUntrusted => Tr("이 인증서 신뢰 (고정)"),
        TlsPinMismatch => Tr("저장된 인증서 지우기"),
        SshUnknown => Tr("신뢰하고 연결"),
        _ => Tr("저장된 키 지우기"),
    };

    /// 지우는 동작이면 위험 표시
    public bool IsRemoval => this is TlsPinMismatch or SshChanged;

    /// 확인 창 제목
    public string Title(string profileName) => this switch
    {
        TlsUntrusted => Tr("서버 인증서를 확인할 수 없어요 — %@", profileName),
        TlsPinMismatch => Tr("서버 인증서가 바뀌었어요 — %@", profileName),
        SshUnknown u => Tr("처음 접속하는 SSH 서버예요 — %@", $"{u.Scan.Host}:{u.Scan.Port}"),
        SshChanged c => Tr("SSH 서버 키가 바뀌었어요 — %@", $"{c.Host}:{c.Port}"),
        _ => "",
    };
}

/// 접속 하나에 대한 확인 요청 (메인 창)
public sealed record SecurityPromptRequest(Guid ProfileId, string ProfileName, SecurityPrompt Prompt)
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// 고정 지문을 바꾼 결과
public abstract record PinChange
{
    public sealed record None : PinChange { public static readonly None Instance = new(); }
    public sealed record Set(string Pin) : PinChange;
    public sealed record Clear : PinChange { public static readonly Clear Instance = new(); }
}

public sealed partial class AppState
{
    /// TLS 인증서 · SSH 호스트 키 확인 창 (F9-3 · F9-4)
    [ObservableProperty] private SecurityPromptRequest? _securityPrompt;

    /// 연결 실패가 인증서 · 호스트 키 문제면 확인 창을 띄운다
    public bool PresentSecurityPrompt(Exception error, ConnectionProfile profile)
    {
        if (SP.From(error) is not { } prompt) return false;
        SecurityPrompt = new SecurityPromptRequest(profile.Id, profile.Name, prompt);
        return true;
    }

    /// 확인 창에서 [신뢰] · [지우기] 를 눌렀을 때 파일 · 감사 로그를 처리한다.
    /// TLS 고정 지문은 저장하지 않고 바꿀 값을 돌려준다 (메인 창은 접속 목록에, 접속 편집은 편집 중인 값에 넣는다).
    public PinChange ApplySecurityAccept(SP prompt, string profileName)
    {
        switch (prompt)
        {
            case SP.TlsUntrusted u:
                return new PinChange.Set(u.Info.Sha256);
            case SP.TlsPinMismatch:
                return PinChange.Clear.Instance;
            case SP.SshUnknown s:
                try
                {
                    SshHostKeys.Trust(s.Scan);
                    var fps = string.Join(", ", s.Scan.Keys.Select(k => k.Fingerprint));
                    var addr = $"{s.Scan.Host}:{s.Scan.Port}";
                    Log(LogLevel.Info, LogSource.App, Tr("SSH 호스트 키 신뢰 %@ %@", addr, fps),
                        audit: new AuditRecord(AuditAction.Settings, "", $"SSH 호스트 키 신뢰 {addr} {fps}"));
                }
                catch (Exception e)
                {
                    LogError(e, LogSource.App, Tr("SSH 호스트 키를 저장하지 못했어요"));
                }
                return PinChange.None.Instance;
            case SP.SshChanged c:
                SshHostKeys.Remove(c.Pattern);
                Log(LogLevel.Info, LogSource.App, Tr("SSH 호스트 키 지움 %@", $"{c.Host}:{c.Port}"),
                    audit: new AuditRecord(AuditAction.Settings, "", $"SSH 호스트 키 지움 {c.Host}:{c.Port}"));
                return PinChange.None.Instance;
        }
        return PinChange.None.Instance;
    }

    /// TLS 고정 지문이 바뀌었으면 감사 로그에 남긴다 (접속 정보 저장 · 확인 창 공용)
    public void AuditPinChange(string profileName, string? old, string? @new)
    {
        if ((old ?? "") == (@new ?? "")) return;
        if (!string.IsNullOrEmpty(@new))
            Log(LogLevel.Info, LogSource.App, Tr("%@ TLS 인증서 신뢰 SHA256:%@", profileName, @new),
                audit: new AuditRecord(AuditAction.Settings, profileName, $"TLS 인증서 신뢰 SHA256:{@new}"));
        else
            Log(LogLevel.Info, LogSource.App, Tr("%@ 인증서 고정 지움", profileName),
                audit: new AuditRecord(AuditAction.Settings, profileName, "인증서 고정 지움"));
    }

    /// 메인 창 확인 창의 결과
    public async Task ResolveSecurityPromptAsync(SecurityPromptRequest request, bool accept)
    {
        SecurityPrompt = null;
        if (!accept) return;
        var change = ApplySecurityAccept(request.Prompt, request.ProfileName);
        if (change is not PinChange.None && IndexOfProfile(request.ProfileId) is var i && i >= 0)
        {
            var old = Profiles[i].TlsPinSHA256;
            Profiles[i] = Profiles[i] with { TlsPinSHA256 = change is PinChange.Set s ? s.Pin : null };
            Persist();
            AuditPinChange(Profiles[i].Name, old, Profiles[i].TlsPinSHA256);
            if (request.ProfileId == SelectedProfileId) OnPropertyChanged(nameof(SelectedProfile));
        }
        // 다시 연결 (지운 경우에는 새 인증서 · 키를 확인 창으로 다시 보여 준다)
        await ConnectAsync(request.ProfileId);
    }

    /// 연결 후 상태창 한 줄
    public void LogTlsStatus(string name, TlsStatus status) =>
        Log(LogLevel.Info, LogSource.Connection, $"{name} · {status.Text()}");

    /// 자격 증명 관리자에 저장한 비밀번호면 세션이 들고 있지 않고 필요할 때 다시 읽게 한다.
    /// 이번 실행 중에만 기억하는 비밀번호(보관소에 없음)는 null — 그때는 세션이 들고 있어야 다시 연결할 수 있다.
    public Func<string?>? PasswordProvider(ConnectionProfile profile)
    {
        if (!profile.SavePassword || !profile.NeedsPassword || SessionPasswords.ContainsKey(profile.Id) || AppPaths.UiTestPassword != null)
            return null;
        var id = profile.Id;
        var secrets = Secrets;
        return () => secrets.GetPassword(id);
    }
}
