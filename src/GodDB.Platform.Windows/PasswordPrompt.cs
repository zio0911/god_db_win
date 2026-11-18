using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using static GodDB.Core.L10n;

namespace GodDB.Platform.Windows;

/// Windows Hello 가 없을 때: Windows 자격 증명 입력창으로 지금 사용자의 로그인 암호를 받아 LogonUser 로 확인한다.
/// 입력한 암호는 확인에만 쓰고 바로 지운다 (저장 · 기록하지 않음).
[SupportedOSPlatform("windows")]
internal static class PasswordPrompt
{
    public static bool VerifyCurrentUser(string reason, IntPtr owner)
    {
        var info = new CREDUI_INFO
        {
            cbSize = Marshal.SizeOf<CREDUI_INFO>(),
            hwndParent = owner,
            pszCaptionText = "GodDB",
            pszMessageText = reason + "\n" + Tr("Windows 로그인 암호를 입력하세요."),
        };
        uint package = 0;
        const uint CREDUIWIN_ENUMERATE_CURRENT_USER = 0x200;
        int rc = CredUIPromptForWindowsCredentials(ref info, 0, ref package, IntPtr.Zero, 0,
            out var outBuf, out var outSize, IntPtr.Zero, CREDUIWIN_ENUMERATE_CURRENT_USER);
        if (rc != 0) return false; // 취소 등
        try
        {
            var user = new StringBuilder(256); var domain = new StringBuilder(256); var pass = new StringBuilder(256);
            int ul = user.Capacity, dl = domain.Capacity, pl = pass.Capacity;
            if (!CredUnPackAuthenticationBuffer(0, outBuf, outSize, user, ref ul, domain, ref dl, pass, ref pl)) return false;
            var name = user.ToString();
            string? dom = domain.Length > 0 ? domain.ToString() : null;
            // "PC\사용자" 또는 "MicrosoftAccount\메일" 형태
            int slash = name.IndexOf('\\');
            if (dom == null && slash > 0) { dom = name[..slash]; name = name[(slash + 1)..]; }
            // 지금 로그인한 사용자만 허용
            if (!string.Equals(name, Environment.UserName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(dom, "MicrosoftAccount", StringComparison.OrdinalIgnoreCase)) return false;
            const int LOGON32_LOGON_INTERACTIVE = 2, LOGON32_PROVIDER_DEFAULT = 0;
            bool ok = LogonUser(name, dom ?? ".", pass.ToString(), LOGON32_LOGON_INTERACTIVE, LOGON32_PROVIDER_DEFAULT, out var token);
            pass.Clear();
            if (ok) CloseHandle(token);
            return ok;
        }
        finally
        {
            ZeroMemory(outBuf, (UIntPtr)outSize);
            CoTaskMemFree(outBuf);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDUI_INFO
    {
        public int cbSize;
        public IntPtr hwndParent;
        public string pszMessageText;
        public string pszCaptionText;
        public IntPtr hbmBanner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern int CredUIPromptForWindowsCredentials(ref CREDUI_INFO info, int authError, ref uint authPackage,
        IntPtr inAuthBuffer, uint inAuthBufferSize, out IntPtr outAuthBuffer, out uint outAuthBufferSize, IntPtr save, uint flags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredUnPackAuthenticationBuffer(uint flags, IntPtr authBuffer, uint authBufferSize,
        StringBuilder userName, ref int maxUserName, StringBuilder domainName, ref int maxDomainName,
        StringBuilder password, ref int maxPassword);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUser(string user, string domain, string password, int logonType, int provider, out IntPtr token);

    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory")] private static extern void ZeroMemory(IntPtr dest, UIntPtr size);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr p);
}
