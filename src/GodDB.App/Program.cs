using Avalonia;
using Velopack;

namespace GodDB.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack: 설치·업데이트 훅을 가장 먼저 처리한다 (설치 프로그램이 앱을 불렀을 때 바로 끝남)
        VelopackApp.Build().Run();
        // 감사 로그 검사: GodDB --verify-audit [폴더] → 결과를 출력하고 문제가 있으면 종료 코드 1
        var i = Array.IndexOf(args, "--verify-audit");
        if (i >= 0)
        {
            AttachToParentConsole();
            var dir = args.Length > i + 1 ? args[i + 1] : LogFile.Shared.Directory;
            var reports = AuditChain.Verify(dir);
            foreach (var r in reports) Console.WriteLine(AuditChain.Describe(r));
            return reports.Any(r => r.Status is AuditFileStatus.Broken or AuditFileStatus.Unlinked) ? 1 : 0;
        }
        // 개발용: GodDB --export-table-spec-sample <out.xlsx> → 샘플 모델의 테이블 명세서를 저장하고 종료 (Mac 과 같음)
        var spec = Array.IndexOf(args, "--export-table-spec-sample");
        if (spec >= 0 && args.Length > spec + 1)
        {
            AttachToParentConsole();
            var m = ModelingSample.Model();
            var info = new TableSpecInfo(m.Name, Environment.UserName, Views.TableSpecExportWindow.Today());
            try { File.WriteAllBytes(args[spec + 1], TableSpecExport.Data(m, info)); }
            catch (Exception e) { Console.WriteLine(e.Message); return 1; }
            return 0;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// Windows 의 창 앱은 콘솔이 없어서 출력이 사라진다 → 부른 명령 프롬프트 · PowerShell 에 붙어 거기에 쓴다
    private static void AttachToParentConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (!AttachConsole(-1)) return;
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.WriteLine();
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
