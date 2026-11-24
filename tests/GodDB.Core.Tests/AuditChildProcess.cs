using System.Diagnostics;
using System.Globalization;

namespace GodDB.Core.Tests;

/// 감사 로그 동시 기록 테스트용 자식 프로세스: 이 테스트 실행 파일을 다시 띄워 `AuditChildWriter` 하나만 돌린다
/// (xUnit v3 테스트 프로젝트는 실행 파일이라 `-method` 로 테스트 하나를 고를 수 있다).
internal static class AuditChildProcess
{
    public const string DirVar = "GODDB_AUDIT_CHILD_DIR";
    public const string KeyVar = "GODDB_AUDIT_CHILD_KEY";
    public const string TagVar = "GODDB_AUDIT_CHILD_TAG";
    public const string CountVar = "GODDB_AUDIT_CHILD_COUNT";
    public const string StartVar = "GODDB_AUDIT_CHILD_START";

    private static readonly DateTime StartAt = DateTime.UtcNow.AddSeconds(4);

    /// 실행 중인 자식 (출력은 미리 읽어 둔다 — 버퍼가 차서 멈추지 않게)
    public sealed record Run(Process Process, Task<string> Output, Task<string> Error)
    {
        public bool WaitForExit(int ms) => Process.WaitForExit(ms);
        public int ExitCode => Process.ExitCode;
        public string Log => Output.Result + "\n" + Error.Result;
    }

    public static Run Start(string directory, byte[] pkcs8, string tag, int count)
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var assembly = typeof(AuditChildProcess).Assembly.Location;
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = host;
            psi.ArgumentList.Add(assembly);
        }
        else
        {
            // 테스트 실행 파일 자체(apphost)로 돌고 있으면 같은 실행 파일. 아니면 dotnet + dll
            var apphost = Path.ChangeExtension(assembly, OperatingSystem.IsWindows() ? ".exe" : null);
            if (File.Exists(apphost)) psi.FileName = apphost;
            else
            {
                psi.FileName = "dotnet";
                psi.ArgumentList.Add(assembly);
            }
        }
        psi.ArgumentList.Add("-method");
        psi.ArgumentList.Add("GodDB.Core.Tests.AuditIntegrityTests.AuditChildWriter");
        psi.ArgumentList.Add("-noLogo");
        psi.Environment[DirVar] = directory;
        psi.Environment[KeyVar] = Convert.ToBase64String(pkcs8);
        psi.Environment[TagVar] = tag;
        psi.Environment[CountVar] = count.ToString(CultureInfo.InvariantCulture);
        psi.Environment[StartVar] = StartAt.ToBinary().ToString(CultureInfo.InvariantCulture);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("child not started");
        return new Run(p, p.StandardOutput.ReadToEndAsync(), p.StandardError.ReadToEndAsync());
    }
}
