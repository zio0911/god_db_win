using GodDB.Core.Audit;
using GodDB.Core.Platform;

namespace GodDB.Core.Tests;

/// Mac SSHHostKeysTests.swift — SSH 호스트 키 확인 (F9-4).
/// ssh · ssh-keygen · ssh-keyscan 을 실제로 부르는 테스트는 OpenSSH 가 없는 PC 에서 건너뛴다 (Windows: 선택적 기능 "OpenSSH 클라이언트").
public class SSHHostKeysTests
{
    /// 테스트용 공개키 (ssh-keygen 으로 만든 ED25519 — 어느 서버의 키도 아님)
    public const string EdKey = "AAAAC3NzaC1lZDI1NTE5AAAAIChtIjIrffCgho75EL9ZKPIMs9wEuDDnaOjuD4Q/Ep5M";

    public static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "goddb-kh-" + Guid.NewGuid().ToString("N"), "known_hosts");

    private static string NeedTool(string name)
    {
        var path = SshHostKeys.Tool(name);
        if (path == null) Assert.Skip($"{name} 없음 (OpenSSH 클라이언트)");
        return path!;
    }

    [Fact]
    public void PatternUsesBracketsForNonDefaultPort()
    {
        Assert.Equal("bastion.example.com", SshHostKeys.Pattern("bastion.example.com", 22));
        Assert.Equal("[bastion.example.com]:2222", SshHostKeys.Pattern("bastion.example.com", 2222));
    }

    [Fact]
    public void SshOptionsQuoteThePathWithSpace()
    {
        var opts = SshHostKeys.SshOptions("/Users/me/Library/Application Support/GodDB/known_hosts");
        Assert.Equal([
            "-o", "StrictHostKeyChecking=yes",
            "-o", "UserKnownHostsFile=\"~/.ssh/known_hosts\" \"/Users/me/Library/Application Support/GodDB/known_hosts\"",
        ], opts);
        // Windows 경로: 역슬래시는 ssh 설정 해석(argv_split)에서 이스케이프라 두 번 쓴다
        var win = SshHostKeys.SshOptions(@"C:\Users\Hong Gil\AppData\Roaming\GodDB\known_hosts");
        Assert.Equal("UserKnownHostsFile=\"~/.ssh/known_hosts\" \"C:\\\\Users\\\\Hong Gil\\\\AppData\\\\Roaming\\\\GodDB\\\\known_hosts\"", win[3]);
    }

    [Fact]
    public void TunnelArgumentsUseStrictChecking()
    {
        var args = SshTunnel.Arguments(new SshConfig { Host = "bastion", Port = 2222, Username = "ec2-user" },
            40000, "db", 5432, knownHostsFile: "/tmp/a b/known_hosts");
        Assert.Contains("StrictHostKeyChecking=yes", args);
        Assert.DoesNotContain("StrictHostKeyChecking=accept-new", args);
        Assert.Contains("UserKnownHostsFile=\"~/.ssh/known_hosts\" \"/tmp/a b/known_hosts\"", args);
        Assert.Equal("ec2-user@bastion", args[^1]);
    }

    /// ssh 가 옵션을 실제로 두 파일로 읽는지 (ssh -G: 접속하지 않고 설정만 출력)
    [Fact]
    public void SshParsesQuotedKnownHostsOption()
    {
        var ssh = NeedTool("ssh");
        var file = OperatingSystem.IsWindows() ? @"C:\x y\known_hosts" : "/tmp/x y/known_hosts";
        var r = SshHostKeys.Run(ssh, ["-G", .. SshHostKeys.SshOptions(file), "example.invalid"], timeout: TimeSpan.FromSeconds(5));
        var line = r.Stdout.Split('\n').FirstOrDefault(l => l.StartsWith("userknownhostsfile ", StringComparison.Ordinal))?.TrimEnd('\r');
        Assert.NotNull(line);
        Assert.EndsWith(" " + file.Replace('\\', '/'), line.Replace('\\', '/'));
        Assert.Contains("/.ssh/known_hosts ", line.Replace('\\', '/'));
    }

    [Fact]
    public void ScanOutputParsing()
    {
        var output = $"""
            # bastion:2222 SSH-2.0-OpenSSH_9.6
            [bastion]:2222 ssh-ed25519 {EdKey}
            # bastion:2222 SSH-2.0-OpenSSH_9.6

            [bastion]:2222 ecdsa-sha2-nistp256 AAAAE2VjZHNh
            garbage line
            """;
        Assert.Equal([$"[alias]:2222 ssh-ed25519 {EdKey}", "[alias]:2222 ecdsa-sha2-nistp256 AAAAE2VjZHNh"],
            SshHostKeys.KeyLines(output, "[alias]:2222"));
        // Windows 줄바꿈도 같게
        Assert.Equal(2, SshHostKeys.KeyLines(output.Replace("\n", "\r\n"), "[alias]:2222").Count);
    }

    [Fact]
    public void FingerprintOutputParsing()
    {
        const string output = """
            256 SHA256:9cP3abcDEF/xk+0 [bastion]:2222 (ED25519)
            3072 SHA256:RSAfp bastion (RSA)
            not a fingerprint
            """;
        Assert.Equal([
            new SshHostKeyFingerprint("ED25519", "SHA256:9cP3abcDEF/xk+0", 256),
            new SshHostKeyFingerprint("RSA", "SHA256:RSAfp", 3072),
        ], SshHostKeys.ParseFingerprints(output));
    }

    [Fact]
    public void RealFingerprintFromSSHKeygen()
    {
        var keygen = NeedTool("ssh-keygen");
        var r = SshHostKeys.Run(keygen, ["-l", "-f", "-"], stdin: $"[h]:2222 ssh-ed25519 {EdKey}\n", timeout: TimeSpan.FromSeconds(5));
        var keys = SshHostKeys.ParseFingerprints(r.Stdout);
        Assert.Single(keys);
        Assert.Equal("ED25519", keys[0].Type);
        Assert.StartsWith("SHA256:", keys[0].Fingerprint);
    }

    [Fact]
    public void ConfigParsing()
    {
        const string output = """
            user ec2-user
            hostname 10.1.2.3
            port 2200
            hostkeyalias none
            """;
        var r = SshHostKeys.ParseConfig(output, "b", 22);
        Assert.Equal(new SshHostKeys.Resolved("10.1.2.3", 2200, null), r);
        Assert.Equal("[10.1.2.3]:2200", r.Pattern);
        Assert.Equal("prod-bastion", SshHostKeys.ParseConfig("hostname h\nport 2200\nhostkeyalias prod-bastion\n", "b", 22).Pattern);
        Assert.Equal("b", SshHostKeys.ParseConfig("", "b", 22).Pattern);
    }

    [Fact]
    public void ChangedKeyDetection()
    {
        const string changed = """
            @@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@
            @    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @
            @@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@
            Host key verification failed.
            """;
        Assert.Equal(SshHostKeys.StderrKind.Changed, SshHostKeys.Classify(changed));
        const string unknown = """
            No ED25519 host key is known for [bastion]:2222 and you have requested strict checking.
            Host key verification failed.
            """;
        Assert.Equal(SshHostKeys.StderrKind.Unknown, SshHostKeys.Classify(unknown));
        Assert.Equal(SshHostKeys.StderrKind.Changed, SshHostKeys.Classify("Host key verification failed."));
        Assert.Null(SshHostKeys.Classify("Permission denied (publickey)."));
    }

    [Fact]
    public void AppendLookupAndRemoveInTempFile()
    {
        NeedTool("ssh-keygen");
        var file = TempFile();
        try
        {
            var pattern = SshHostKeys.Pattern("bastion.test", 2222);
            Assert.False(SshHostKeys.IsKnown(pattern, [file]));

            var scan = new SshHostScan("bastion.test", 2222, pattern, [$"{pattern} ssh-ed25519 {EdKey}"], []);
            SshHostKeys.Trust(scan, file);
            // 본인만 읽고 쓰기 (Mac 0600 · Windows ACL)
            Assert.True(UserOnlyFile.IsRestricted(file));
            Assert.True(SshHostKeys.IsKnown(pattern, [file]));
            Assert.False(SshHostKeys.IsKnown("[bastion.test]:2223", [file]));
            Assert.False(SshHostKeys.IsKnown("bastion.test", [file]));

            // 줄바꿈 없이 끝난 파일에 더해도 줄이 붙지 않는다
            File.WriteAllText(file, $"other ssh-ed25519 {EdKey}");
            SshHostKeys.Append([$"{pattern} ssh-ed25519 {EdKey}"], file);
            Assert.Equal(2, File.ReadAllText(file).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

            // 지우기 (GodDB 파일에서만, 백업 .old 는 남기지 않음)
            Assert.True(SshHostKeys.Remove(pattern, file));
            Assert.False(SshHostKeys.IsKnown(pattern, [file]));
            Assert.True(SshHostKeys.IsKnown("other", [file]));
            Assert.False(File.Exists(file + ".old"));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(file)!, true); } catch { } }
    }

    [Fact]
    public void CheckPassesForKnownHostWithoutScanning()
    {
        NeedTool("ssh");
        NeedTool("ssh-keygen");
        var file = TempFile();
        try
        {
            var config = new SshConfig { Host = "127.0.0.1", Port = 2222, Username = "u" };
            var pattern = SshHostKeys.Resolve(config).Pattern;
            SshHostKeys.Append([$"{pattern} ssh-ed25519 {EdKey}"], file);
            var missing = Path.Combine(Path.GetDirectoryName(file)!, "none");
            SshHostKeys.Check(config, file, missing);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(file)!, true); } catch { } }
    }

    [Fact]
    public void CheckReportsScanFailureForUnreachableHost()
    {
        NeedTool("ssh-keyscan");
        var file = TempFile();
        var missing = Path.Combine(Path.GetDirectoryName(file)!, "none");
        // 이 PC 의 닫힌 포트 → 키를 받지 못함
        var config = new SshConfig { Host = "127.0.0.1", Port = 1, Username = "u" };
        Assert.Throws<SshHostKeyException>(() => SshHostKeys.Check(config, file, missing));
    }

    [Fact]
    public void ErrorMessages()
    {
        var scan = new SshHostScan("b", 22, "b", [], [new SshHostKeyFingerprint("ED25519", "SHA256:x")]);
        Assert.Equal("처음 접속하는 SSH 서버예요 — b:22\nED25519  SHA256:x", SshHostKeyException.Unknown(scan).Message);
        Assert.StartsWith("SSH 서버 키가 바뀌었어요.", SshHostKeyException.Changed("b", 22, "b").Message);
        // 상태창에도 같은 문구
        Assert.Equal(SshHostKeyException.Unknown(scan).Message, ErrorText.Describe(SshHostKeyException.Unknown(scan)).Message);
    }

    /// Windows: ssh 와 같은 곳(%SystemRoot%\System32\OpenSSH)에서 ssh-keygen · ssh-keyscan 을 찾는다
    [Fact]
    public void FindsOpenSSHTools()
    {
        var keygen = @"C:\Windows\System32\OpenSSH\ssh-keygen.exe";
        Assert.Equal(keygen, SshTunnel.FindTool("ssh-keygen", true, @"C:\Windows", "", p => p == keygen));
        Assert.Equal("/usr/bin/ssh-keyscan", SshTunnel.FindTool("ssh-keyscan", false, null, "", p => p == "/usr/bin/ssh-keyscan"));
        Assert.Null(SshTunnel.FindTool("ssh-keyscan", true, @"C:\Windows", "", _ => false));
    }
}
