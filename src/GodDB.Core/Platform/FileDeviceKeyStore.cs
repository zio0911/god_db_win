namespace GodDB.Core.Platform;

/// 기기 키를 %LOCALAPPDATA%\GodDB\keys\&lt;이름&gt;.key 파일로 보관한다 (본인 계정만 읽기·쓰기).
/// Mac 판은 키체인(이 기기 전용)에 둔다. Windows 판은 DPAPI·TPM 저장소로 바꿔 끼울 수 있다 (<see cref="IDeviceKeyStore"/>).
public sealed class FileDeviceKeyStore(string? directory = null) : IDeviceKeyStore
{
    public string Directory { get; } = directory ?? Path.Combine(AppPaths.LocalDirectory, "keys");

    private string PathFor(string account) => Path.Combine(Directory, account + ".key");

    public byte[]? Load(string account)
    {
        try
        {
            var path = PathFor(account);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch { return null; }
    }

    public bool Save(string account, byte[] data)
    {
        try
        {
            AppPaths.Ensure(Directory);
            var path = PathFor(account);
            // 키체인 SecItemAdd 처럼 이미 있으면 덮어쓰지 않는다
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                UserOnlyFile.Restrict(path);
                fs.Write(data);
            }
            return true;
        }
        catch { return false; }
    }
}
