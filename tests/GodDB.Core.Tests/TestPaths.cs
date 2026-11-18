namespace GodDB.Core.Tests;

internal static class TestPaths
{
    /// VERSION 파일이 있는 저장소 최상위 폴더
    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VERSION"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("저장소 최상위 폴더를 찾지 못했어요");
        }
    }
}
