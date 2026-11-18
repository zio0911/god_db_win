using System.Runtime.CompilerServices;

namespace GodDB.Core.Tests;

/// 테스트가 실제 사용자 폴더(%APPDATA%\GodDB, %LOCALAPPDATA%\GodDB)를 건드리지 않도록
/// 테스트 프로세스 시작 때 GODDB_DATA_DIR 를 임시 폴더로 정한다.
internal static class TestDataDir
{
    [ModuleInitializer]
    internal static void Init()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GODDB_DATA_DIR")))
            Environment.SetEnvironmentVariable("GODDB_DATA_DIR",
                Path.Combine(Path.GetTempPath(), "goddb-test-data-" + Environment.ProcessId));
    }
}

/// 테스트마다 쓰고 지우는 임시 폴더
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "goddb-" + Guid.NewGuid());

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
