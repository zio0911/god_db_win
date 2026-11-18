using System.Collections.Concurrent;

namespace GodDB.Core.Platform;

/// 접속 비밀번호 보관소. Windows: 자격 증명 관리자 (Mac: 키체인 서비스 `GodDB`, 계정 = 접속 ID)
public interface ISecretStore
{
    /// 사용자에게 보이는 보관소 이름 (예: "자격 증명 관리자")
    string DisplayName { get; }
    string? GetPassword(Guid profileId);
    /// 저장에 실패하면 false (상태창에 경고)
    bool SetPassword(Guid profileId, string password);
    void DeletePassword(Guid profileId);
}

/// 저장된 비밀번호를 보기 전 본인 확인. Windows: Windows Hello (없으면 Windows 로그인 암호)
public interface IUserVerifier
{
    /// 본인 확인에 성공하면 true. reason 은 확인 창에 보이는 문장.
    Task<bool> VerifyAsync(string reason, IntPtr ownerWindow = default);
}

/// 앱을 끌 때까지만 메모리에 기억하는 보관소. 비밀번호 저장을 끈 접속, 그리고 Windows 가 아닌 곳(개발용 실행)에서 쓴다.
public sealed class MemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<Guid, string> _values = new();
    public string DisplayName => L10n.Tr("메모리 (앱을 끌 때까지)");
    public string? GetPassword(Guid profileId) => _values.TryGetValue(profileId, out var v) ? v : null;
    public bool SetPassword(Guid profileId, string password) { _values[profileId] = password; return true; }
    public void DeletePassword(Guid profileId) => _values.TryRemove(profileId, out _);
}

/// 본인 확인 수단이 없는 곳(개발용 실행)에서는 확인 없이 통과한다.
public sealed class NoUserVerifier : IUserVerifier
{
    public Task<bool> VerifyAsync(string reason, IntPtr ownerWindow = default) => Task.FromResult(true);
}
