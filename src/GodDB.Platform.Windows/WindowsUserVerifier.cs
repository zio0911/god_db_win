using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GodDB.Core.Platform;
using Windows.Security.Credentials.UI;

namespace GodDB.Platform.Windows;

/// 저장된 비밀번호를 보기 전 본인 확인 (Mac: Touch ID 또는 Mac 로그인 암호)
/// 1. Windows Hello (지문 · 얼굴 · PIN) 가 설정돼 있으면 Hello 로 확인한다.
/// 2. 없으면 Windows 자격 증명 입력창에 지금 사용자의 Windows 로그인 암호를 넣게 하고 LogonUser 로 확인한다.
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowsUserVerifier : IUserVerifier
{
    public async Task<bool> VerifyAsync(string reason, IntPtr ownerWindow = default)
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            if (availability == UserConsentVerifierAvailability.Available)
            {
                var result = ownerWindow != IntPtr.Zero
                    ? await UserConsentVerifierInterop.RequestVerificationForWindowAsync(ownerWindow, reason)
                    : await UserConsentVerifier.RequestVerificationAsync(reason);
                return result == UserConsentVerificationResult.Verified;
            }
        }
        catch (COMException) { /* Hello 를 쓸 수 없는 환경 → 암호 확인 */ }
        return await Task.Run(() => PasswordPrompt.VerifyCurrentUser(reason, ownerWindow));
    }
}
