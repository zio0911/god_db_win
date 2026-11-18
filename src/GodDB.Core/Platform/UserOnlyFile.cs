using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace GodDB.Core.Platform;

/// 파일을 본인 계정만 읽고 쓸 수 있게 한다 (Mac 판의 권한 600).
/// - Windows: 상속을 끊고 현재 사용자에게만 모든 권한을 주는 ACL
/// - 그 밖: 유닉스 권한 600
public static class UserOnlyFile
{
    /// 실패해도 예외를 던지지 않는다 (권한을 못 바꿔도 기록은 남긴다)
    public static bool Restrict(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) RestrictWindows(path);
            else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return true;
        }
        catch { return false; }
    }

    /// 본인만 접근할 수 있는지 (테스트용)
    public static bool IsRestricted(string path)
    {
        if (OperatingSystem.IsWindows()) return IsRestrictedWindows(path);
        return File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string path)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("no user SID");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsRestrictedWindows(string path)
    {
        var user = WindowsIdentity.GetCurrent().User;
        var security = new FileInfo(path).GetAccessControl();
        if (!security.AreAccessRulesProtected) return false;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        return rules.Count > 0 && rules.All(r => r.IdentityReference.Equals(user));
    }
}
