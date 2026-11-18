using GodDB.Core.Platform;

namespace GodDB.App.Services;

/// OS 별 구현을 고른다. Windows 배포본: 자격 증명 관리자 · Windows Hello.
/// Mac/Linux 개발 실행(net10.0): 메모리 보관소 · 확인 없음 (비밀번호는 앱을 끌 때까지만).
public static class PlatformServices
{
    public static ISecretStore Secrets { get; } = CreateSecrets();
    public static IUserVerifier Verifier { get; } = CreateVerifier();

    private static ISecretStore CreateSecrets()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows()) return new GodDB.Platform.Windows.CredentialManagerStore();
#endif
        return new MemorySecretStore();
    }

    private static IUserVerifier CreateVerifier()
    {
#if WINDOWS
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return new GodDB.Platform.Windows.WindowsUserVerifier();
#endif
        return new NoUserVerifier();
    }
}
