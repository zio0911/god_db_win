using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using static GodDB.Core.L10n;

namespace GodDB.Core.Platform;

public enum SshTunnelErrorKind { NoFreePort, Exited, Timeout, SshNotFound }

public sealed class SshTunnelException : Exception
{
    public SshTunnelErrorKind Kind { get; }
    /// Exited 일 때 ssh 가 남긴 오류 글
    public string? Detail { get; }

    public SshTunnelException(SshTunnelErrorKind kind, string? detail = null) : base(Describe(kind, detail))
    {
        Kind = kind;
        Detail = detail;
    }

    private static string Describe(SshTunnelErrorKind kind, string? detail) => kind switch
    {
        SshTunnelErrorKind.NoFreePort => Tr("로컬 포트를 확보하지 못했어요."),
        SshTunnelErrorKind.Exited => Tr("SSH 터널 연결에 실패했어요.\n%@", detail),
        SshTunnelErrorKind.Timeout => Tr("SSH 터널이 15초 안에 열리지 않았어요."),
        // Windows 판에만 있는 문장 (Mac 은 /usr/bin/ssh 가 항상 있음)
        _ => Tr("ssh 프로그램을 찾지 못했어요. Windows 설정 > 시스템 > 선택적 기능에서 \"OpenSSH 클라이언트\"를 설치해 주세요."),
    };
}

/// 시스템 OpenSSH `ssh -N -L` 로 바스티온 포트포워딩을 연다.
/// ssh-agent, ~/.ssh/config, known_hosts 를 그대로 활용하기 위해 직접 구현하지 않고 ssh 프로세스를 띄운다.
/// Windows: %SystemRoot%\System32\OpenSSH\ssh.exe (없으면 PATH 의 ssh.exe). Mac/Linux: /usr/bin/ssh (없으면 PATH).
public sealed class SshTunnel : IDisposable
{
    public int LocalPort { get; }
    private readonly SshConfig _config;
    private readonly string _remoteHost;
    private readonly int _remotePort;
    private readonly StringBuilder _stderr = new();
    private Process? _process;
    private bool _disposed;

    /// 앱이 끝날 때 남은 ssh 를 모두 끈다
    private static readonly ConcurrentDictionary<SshTunnel, byte> Live = new();
    static SshTunnel()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { foreach (var t in Live.Keys) t.Stop(); };
    }

    public SshTunnel(SshConfig config, string remoteHost, int remotePort)
    {
        LocalPort = FindFreePort() ?? throw new SshTunnelException(SshTunnelErrorKind.NoFreePort);
        _config = config;
        _remoteHost = remoteHost;
        _remotePort = remotePort;
    }

    public static List<string> Arguments(SshConfig config, int localPort, string remoteHost, int remotePort,
                                         string? knownHostsFile = null)
    {
        var args = new List<string>
        {
            "-N",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "BatchMode=yes",
        };
        // 모르는 서버 · 바뀐 키는 거부 (처음 접속은 연결 전에 지문을 보여 주고 확인받는다 — F9-4)
        args.AddRange(SshHostKeys.SshOptions(knownHostsFile));
        args.AddRange(
        [
            "-o", "ServerAliveInterval=30",
            "-o", "ConnectTimeout=10",
            "-p", config.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-L", $"127.0.0.1:{localPort}:{remoteHost}:{remotePort}",
        ]);
        var key = ExpandTilde(config.KeyPath);
        if (key.Length > 0) args.AddRange(["-i", key, "-o", "IdentitiesOnly=yes"]);
        args.Add(config.Username.Length == 0 ? config.Host : $"{config.Username}@{config.Host}");
        return args;
    }

    internal static string ExpandTilde(string path)
    {
        if (path == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return path;
    }

    /// 쓸 ssh 실행 파일. 못 찾으면 null.
    /// windows: %SystemRoot%\System32\OpenSSH\ssh.exe → PATH 의 ssh.exe / 그 밖: /usr/bin/ssh → PATH 의 ssh
    public static string? FindSsh(bool windows, string? systemRoot, string? pathEnv, Func<string, bool> fileExists) =>
        FindTool("ssh", windows, systemRoot, pathEnv, fileExists);

    /// OpenSSH 도구(ssh · ssh-keygen · ssh-keyscan) 실행 파일. ssh 와 같은 순서로 찾는다.
    public static string? FindTool(string tool, bool windows, string? systemRoot, string? pathEnv, Func<string, bool> fileExists)
    {
        if (windows)
        {
            if (!string.IsNullOrEmpty(systemRoot))
            {
                var builtIn = systemRoot.TrimEnd('\\') + @"\System32\OpenSSH\" + tool + ".exe";
                if (fileExists(builtIn)) return builtIn;
            }
        }
        else if (fileExists("/usr/bin/" + tool)) return "/usr/bin/" + tool;
        var name = windows ? tool + ".exe" : tool;
        foreach (var dir in (pathEnv ?? "").Split(windows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries))
        {
            // 검사하는 OS 의 경로 구분자로 잇는다 (테스트가 다른 OS 에서 돌아도 같은 결과)
            var sep = windows ? '\\' : '/';
            var candidate = dir.Trim().Trim('"').TrimEnd(sep) + sep + name;
            if (fileExists(candidate)) return candidate;
        }
        return null;
    }

    public static string? FindSsh() => FindTool("ssh");

    public static string? FindTool(string tool) => FindTool(tool, OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable("SystemRoot"),
        Environment.GetEnvironmentVariable("PATH"), File.Exists);

    /// ssh 를 실행하고 로컬 포트가 열릴 때까지 (최대 15초) 기다린다.
    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var exe = FindSsh() ?? throw new SshTunnelException(SshTunnelErrorKind.SshNotFound);
        // 연결 전에 known_hosts 에서 찾고, 없으면 키를 받아 확인 창용 오류를 던진다 (F9-4)
        var config = _config;
        await Task.Run(() => SshHostKeys.Check(config), ct);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var a in Arguments(_config, LocalPort, _remoteHost, _remotePort)) psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_stderr) _stderr.AppendLine(e.Data); };
        p.OutputDataReceived += (_, _) => { };
        try { p.Start(); }
        catch (System.ComponentModel.Win32Exception) { throw new SshTunnelException(SshTunnelErrorKind.SshNotFound); }
        _process = p;
        Live[this] = 0;
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        p.StandardInput.Close();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                if (p.HasExited)
                {
                    p.WaitForExit();   // 남은 stderr 를 다 받는다
                    string msg;
                    lock (_stderr) msg = _stderr.ToString().Trim();
                    Stop();
                    switch (SshHostKeys.Classify(msg))
                    {
                        case SshHostKeys.StderrKind.Changed:
                            var resolved = await Task.Run(() => SshHostKeys.Resolve(config), CancellationToken.None);
                            throw SshHostKeyException.Changed(config.Host, config.Port, resolved.Pattern);
                        case SshHostKeys.StderrKind.Unknown:
                            // 확인한 뒤 키가 바뀌었거나 다른 종류의 키만 알려진 경우 → 다시 받아 확인 창
                            await Task.Run(() => SshHostKeys.Check(config), CancellationToken.None);
                            break;
                    }
                    throw new SshTunnelException(SshTunnelErrorKind.Exited,
                        msg.Length == 0 ? Tr("ssh 종료 코드 %@", p.ExitCode) : msg);
                }
                if (await CanConnectAsync(LocalPort)) return;
                await Task.Delay(200, ct);
            }
        }
        catch (OperationCanceledException)
        {
            Stop();
            throw;
        }
        Stop();
        throw new SshTunnelException(SshTunnelErrorKind.Timeout);
    }

    public void Stop()
    {
        Live.TryRemove(this, out _);
        var p = _process;
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _process?.Dispose();
    }

    // MARK: - 소켓 유틸

    internal static int? FindFreePort()
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
        catch (SocketException) { return null; }
    }

    internal static async Task<bool> CanConnectAsync(int port)
    {
        using var c = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await c.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return true;
        }
        catch { return false; }
    }
}
