using System.Diagnostics;
using System.Globalization;
using System.Text;
using static GodDB.Core.L10n;

namespace GodDB.Core.Platform;

// F9-4. SSH 호스트 키 확인 (Mac SSHHostKeys.swift)
// 연결 전에 known_hosts(~/.ssh/known_hosts + GodDB 전용)에서 찾고, 없으면 ssh-keyscan 으로 키를 받아 지문을 보여 준 뒤
// 사용자가 신뢰하면 GodDB 전용 known_hosts 에 더한다. ssh 는 StrictHostKeyChecking=yes 로 실행한다.
// Windows: 내장 OpenSSH 의 ssh.exe · ssh-keygen.exe · ssh-keyscan.exe (%SystemRoot%\System32\OpenSSH) 를 같은 옵션으로 쓴다.
// 사용자 known_hosts 는 %USERPROFILE%\.ssh\known_hosts (OpenSSH 가 ~ 를 이 폴더로 읽는다),
// GodDB 전용은 %APPDATA%\GodDB\known_hosts (Mac 의 권한 0600 대신 본인만 접근하는 ACL).

/// 서버 키 하나의 지문
/// - Type: "ED25519", "ECDSA", "RSA"
/// - Fingerprint: "SHA256:9cP3…"
public sealed record SshHostKeyFingerprint(string Type, string Fingerprint, int? Bits = null);

/// 처음 접속하는 서버의 키 (확인 창에 보여 주고, 신뢰하면 Lines 를 저장)
/// - Host · Port: 화면에 보이는 주소
/// - Pattern: known_hosts 에서 찾는 이름 ("host" 또는 "[host]:port")
/// - Lines: known_hosts 에 더할 줄 ("pattern keytype base64")
public sealed record SshHostScan(string Host, int Port, string Pattern, IReadOnlyList<string> Lines, IReadOnlyList<SshHostKeyFingerprint> Keys)
{
    public string Id => $"{Host}:{Port}";

    public bool Equals(SshHostScan? o) => o is not null && Host == o.Host && Port == o.Port && Pattern == o.Pattern
                                          && Lines.SequenceEqual(o.Lines) && Keys.SequenceEqual(o.Keys);
    public override int GetHashCode() => HashCode.Combine(Host, Port, Pattern);
}

public enum SshHostKeyErrorKind
{
    /// 모르는 서버 — 확인 창
    Unknown,
    /// 서버 키가 바뀜 — 연결 거부, [저장된 키 지우기]
    Changed,
    /// 키를 받지 못함
    ScanFailed,
}

public sealed class SshHostKeyException : Exception
{
    public SshHostKeyErrorKind Kind { get; }
    public SshHostScan? Scan { get; }
    public string Host { get; }
    public int Port { get; }
    /// Changed: known_hosts 에서 지울 이름
    public string Pattern { get; }

    private SshHostKeyException(SshHostKeyErrorKind kind, string host, int port, string pattern, SshHostScan? scan, string message)
        : base(message)
    {
        Kind = kind;
        Host = host;
        Port = port;
        Pattern = pattern;
        Scan = scan;
    }

    public static SshHostKeyException Unknown(SshHostScan scan) =>
        new(SshHostKeyErrorKind.Unknown, scan.Host, scan.Port, scan.Pattern, scan,
            string.Join("\n", new[] { Tr("처음 접속하는 SSH 서버예요 — %@", $"{scan.Host}:{scan.Port}") }
                .Concat(scan.Keys.Select(k => $"{k.Type}  {k.Fingerprint}"))));

    public static SshHostKeyException Changed(string host, int port, string pattern) =>
        new(SshHostKeyErrorKind.Changed, host, port, pattern, null,
            Tr("SSH 서버 키가 바뀌었어요. 서버를 다시 설치한 게 아니라면 중간에서 가로채는 공격일 수 있어요. 관리자에게 확인한 뒤 [저장된 키 지우기] (GodDB 전용 파일에서만 지움)."));

    public static SshHostKeyException ScanFailed(string host, int port, string message) =>
        new(SshHostKeyErrorKind.ScanFailed, host, port, "", null,
            Tr("SSH 서버 키를 받지 못했어요 — %@", $"{host}:{port}") + (message.Length == 0 ? "" : "\n" + message));
}

public static class SshHostKeys
{
    /// GodDB 전용 known_hosts (%APPDATA%\GodDB\known_hosts, 본인만 접근)
    public static string GodDBFile => Path.Combine(AppPaths.DataDirectory, "known_hosts");

    /// 사용자 known_hosts (%USERPROFILE%\.ssh\known_hosts)
    public static string UserFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");

    // MARK: - 이름 · 옵션

    /// known_hosts 에서 찾는 이름: 22 번이면 host, 아니면 "[host]:port"
    public static string Pattern(string host, int port) => port == 22 ? host : $"[{host}]:{port}";

    /// ssh 옵션 값 안에서 쓰는 큰따옴표 경로 (공백이 있는 경로 — Windows 는 사용자 이름에 공백이 흔하다)
    internal static string Quoted(string path) => "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// `-o` 에 넘길 옵션들
    public static List<string> SshOptions(string? godDBFile = null) =>
    [
        "-o", "StrictHostKeyChecking=yes",
        "-o", "UserKnownHostsFile=" + Quoted("~/.ssh/known_hosts") + " " + Quoted(godDBFile ?? GodDBFile),
    ];

    // MARK: - 출력 해석

    /// ssh-keyscan 출력에서 키 줄만 (주석 · 빈 줄 빼고). 이름 칸은 pattern 으로 바꾼다.
    public static List<string> KeyLines(string output, string pattern)
    {
        var list = new List<string>();
        foreach (var raw in output.Split('\n', '\r'))
        {
            var line = raw.Trim(' ', '\t');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !IsKeyType(parts[1])) continue;
            list.Add($"{pattern} {parts[1]} {parts[2]}");
        }
        return list;
    }

    internal static bool IsKeyType(string s) =>
        s.StartsWith("ssh-", StringComparison.Ordinal) || s.StartsWith("ecdsa-", StringComparison.Ordinal) || s.StartsWith("sk-", StringComparison.Ordinal);

    /// `ssh-keygen -lf -` 출력: "256 SHA256:abc… [host]:2222 (ED25519)"
    public static List<SshHostKeyFingerprint> ParseFingerprints(string output)
    {
        var list = new List<SshHostKeyFingerprint>();
        foreach (var raw in output.Split('\n', '\r'))
        {
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts.FirstOrDefault(p => p.StartsWith("SHA256:", StringComparison.Ordinal)) is not { } fp) continue;
            var type = "?";
            var last = parts[^1];
            if (last.StartsWith('(') && last.EndsWith(')')) type = last[1..^1];
            list.Add(new SshHostKeyFingerprint(type, fp, int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : null));
        }
        return list;
    }

    /// ssh 오류 출력으로 키 문제를 가린다
    public enum StderrKind { Changed, Unknown }

    public static StderrKind? Classify(string stderr)
    {
        if (stderr.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.Ordinal)) return StderrKind.Changed;
        if (stderr.Contains("host key is known for", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("No matching host key", StringComparison.Ordinal)) return StderrKind.Unknown;
        if (stderr.Contains("Host key verification failed", StringComparison.Ordinal)) return StderrKind.Changed;
        return null;
    }

    // MARK: - ssh -G (~/.ssh/config 의 HostName · Port · HostKeyAlias 반영)

    public sealed record Resolved(string Hostname, int Port, string? HostKeyAlias)
    {
        /// known_hosts 에서 찾을 이름 (HostKeyAlias 가 있으면 그것 그대로 — ssh 와 같은 규칙)
        public string Pattern => HostKeyAlias ?? SshHostKeys.Pattern(Hostname, Port);
    }

    /// `ssh -G` 출력 해석
    public static Resolved ParseConfig(string output, string fallbackHost, int fallbackPort)
    {
        var r = new Resolved(fallbackHost, fallbackPort, null);
        foreach (var raw in output.Split('\n', '\r'))
        {
            var parts = raw.Split(' ', 2);
            if (parts.Length != 2) continue;
            switch (parts[0].ToLowerInvariant())
            {
                case "hostname": r = r with { Hostname = parts[1] }; break;
                case "port": r = r with { Port = int.TryParse(parts[1], out var p) ? p : r.Port }; break;
                case "hostkeyalias": r = r with { HostKeyAlias = parts[1] == "none" ? null : parts[1] }; break;
            }
        }
        return r;
    }

    public static Resolved Resolve(SshConfig config)
    {
        var args = new List<string> { "-G", "-p", config.Port.ToString(CultureInfo.InvariantCulture) };
        if (config.Username.Length > 0) args.AddRange(["-l", config.Username]);
        args.Add(config.Host);
        var output = Tool("ssh") is { } ssh ? Run(ssh, args, timeout: TimeSpan.FromSeconds(5)) : RunResult.Failed;
        return ParseConfig(output.Status == 0 ? output.Stdout : "", config.Host, config.Port);
    }

    // MARK: - 찾기 · 받기 · 저장 · 지우기

    /// known_hosts 파일들에서 이름을 찾는다 (해시된 줄도 ssh-keygen 이 찾는다)
    public static bool IsKnown(string pattern, IEnumerable<string> files)
    {
        if (Tool("ssh-keygen") is not { } keygen) return false;
        foreach (var file in files.Where(File.Exists))
        {
            var r = Run(keygen, ["-F", pattern, "-f", file], timeout: TimeSpan.FromSeconds(5));
            if (r.Status == 0 && r.Stdout.Trim().Length > 0) return true;
        }
        return false;
    }

    /// 서버 키를 받아 지문을 계산한다
    public static SshHostScan Scan(string host, int port, string pattern, string displayHost, int displayPort, int timeout = 5)
    {
        var keyscan = Tool("ssh-keyscan") ?? throw new SshTunnelException(SshTunnelErrorKind.SshNotFound);
        var r = Run(keyscan, ["-T", timeout.ToString(CultureInfo.InvariantCulture), "-p", port.ToString(CultureInfo.InvariantCulture), host],
            timeout: TimeSpan.FromSeconds(timeout + 5));
        var lines = KeyLines(r.Stdout, pattern);
        if (lines.Count == 0)
        {
            var msg = string.Join("\n", r.Stderr.Split('\n', '\r').Where(l => l.Length > 0 && !l.StartsWith('#')));
            throw SshHostKeyException.ScanFailed(displayHost, displayPort, msg);
        }
        var keygen = Tool("ssh-keygen") ?? throw new SshTunnelException(SshTunnelErrorKind.SshNotFound);
        var fp = Run(keygen, ["-l", "-f", "-"], stdin: string.Join("\n", lines) + "\n", timeout: TimeSpan.FromSeconds(5));
        return new SshHostScan(displayHost, displayPort, pattern, lines, ParseFingerprints(fp.Stdout));
    }

    /// 연결 전 확인: 알려진 서버면 그냥 지나가고, 모르면 키를 받아 Unknown 을 던진다.
    public static void Check(SshConfig config, string? godDBFile = null, string? userFile = null)
    {
        var resolved = Resolve(config);
        if (IsKnown(resolved.Pattern, [userFile ?? UserFile, godDBFile ?? GodDBFile])) return;
        var scan = Scan(resolved.Hostname, resolved.Port, resolved.Pattern, config.Host, config.Port);
        throw SshHostKeyException.Unknown(scan);
    }

    /// 신뢰한 키를 GodDB 전용 known_hosts 에 더한다 (본인만 접근)
    public static void Trust(SshHostScan scan, string? file = null) => Append(scan.Lines, file ?? GodDBFile);

    public static void Append(IReadOnlyList<string> lines, string file)
    {
        AppPaths.Ensure(Path.GetDirectoryName(file)!);
        var isNew = !File.Exists(file);
        if (isNew) File.WriteAllBytes(file, []);
        UserOnlyFile.Restrict(file);
        using var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var text = string.Join("\n", lines) + "\n";
        if (fs.Length > 0)
        {
            // 마지막 줄이 줄바꿈으로 끝나지 않았으면 붙여 쓰지 않게
            fs.Seek(-1, SeekOrigin.End);
            if (fs.ReadByte() != '\n') text = "\n" + text;
        }
        fs.Seek(0, SeekOrigin.End);
        fs.Write(Encoding.UTF8.GetBytes(text));
    }

    /// GodDB 전용 파일에서만 이 서버의 키를 지운다 (`ssh-keygen -R`)
    public static bool Remove(string pattern, string? file = null)
    {
        file ??= GodDBFile;
        if (!File.Exists(file) || Tool("ssh-keygen") is not { } keygen) return false;
        var r = Run(keygen, ["-R", pattern, "-f", file], timeout: TimeSpan.FromSeconds(5));
        // ssh-keygen 이 남기는 백업(.old)은 지운다 (지운 키가 남지 않게)
        try { File.Delete(file + ".old"); } catch { }
        UserOnlyFile.Restrict(file);
        return r.Status == 0;
    }

    // MARK: - 프로세스

    /// OpenSSH 도구 경로 (ssh · ssh-keygen · ssh-keyscan). ssh 와 같은 곳에서 찾는다.
    public static string? Tool(string name) => SshTunnel.FindTool(name);

    public sealed record RunResult(int Status, string Stdout, string Stderr)
    {
        public static readonly RunResult Failed = new(-1, "", "");
    }

    /// 실행하고 출력을 모은다. 끝나기를 제한 시간까지만 기다린다
    /// (출력 끝만 기다리면, 파이프를 물려받은 자식이 남아 있을 때 영원히 멈춘다 — Mac CI 에서 실제로 멈췄다)
    public static RunResult Run(string path, IReadOnlyList<string> args, string? stdin = null, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var o = new StringBuilder();
        var e = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, d) => { if (d.Data != null) lock (o) o.Append(d.Data).Append('\n'); };
        p.ErrorDataReceived += (_, d) => { if (d.Data != null) lock (e) e.Append(d.Data).Append('\n'); };
        try { p.Start(); }
        catch (Exception ex) { return new RunResult(-1, "", ex.Message); }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            if (stdin != null) p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        catch (IOException) { }
        var ms = (int)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;
        if (!p.WaitForExit(ms))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            p.WaitForExit(1000);
        }
        // 끝났으면 인자 없는 WaitForExit 로 비동기 출력까지 다 받는다 (시간 인자 버전은 출력이 남아 있어도 돌아온다)
        else p.WaitForExit();
        int status;
        try { status = p.HasExited ? p.ExitCode : -1; } catch { status = -1; }
        lock (o) lock (e) return new RunResult(status, o.ToString(), e.ToString());
    }
}
