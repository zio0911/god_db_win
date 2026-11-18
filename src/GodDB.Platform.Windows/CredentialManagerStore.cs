using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using GodDB.Core.Platform;
using static GodDB.Core.L10n;

namespace GodDB.Platform.Windows;

/// Windows 자격 증명 관리자에 접속 비밀번호를 저장한다.
/// 대상 이름 `GodDB:<접속 ID>`, 일반 자격 증명, 로컬 컴퓨터 범위 (다른 PC 로 로밍하지 않음).
/// 제어판 > 자격 증명 관리자 > Windows 자격 증명 에서 보이고 지울 수 있다.
[SupportedOSPlatform("windows")]
public sealed class CredentialManagerStore : ISecretStore
{
    public const string Prefix = "GodDB:";
    public string DisplayName => Tr("자격 증명 관리자");

    public static string TargetName(Guid id) => Prefix + id.ToString("D").ToUpperInvariant();

    public string? GetPassword(Guid profileId)
    {
        if (!Native.CredRead(TargetName(profileId), Native.CRED_TYPE_GENERIC, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<Native.CREDENTIAL>(ptr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return "";
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally { Native.CredFree(ptr); }
    }

    public bool SetPassword(Guid profileId, string password)
    {
        var blob = Encoding.Unicode.GetBytes(password);
        if (blob.Length > 5 * 512) return false; // CRED_MAX_CREDENTIAL_BLOB_SIZE
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var cred = new Native.CREDENTIAL
            {
                Type = Native.CRED_TYPE_GENERIC,
                TargetName = TargetName(profileId),
                Comment = Tr("GodDB DB 비밀번호"),
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = Native.CRED_PERSIST_LOCAL_MACHINE,
                UserName = Environment.UserName,
            };
            return Native.CredWrite(ref cred, 0);
        }
        finally
        {
            Array.Clear(blob);
            handle.Free();
        }
    }

    public void DeletePassword(Guid profileId) => Native.CredDelete(TargetName(profileId), Native.CRED_TYPE_GENERIC, 0);

    private static class Native
    {
        public const uint CRED_TYPE_GENERIC = 1;
        public const uint CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        public static extern void CredFree(IntPtr buffer);
    }
}
