using GodDB.Core;
using GodDB.Core.Platform;

namespace GodDB.Integration.Tests;

/// Mac SSHHostKeysIntegrationTests (SSHHostKeysTests.swift) — 실제 sshd 로 확인
/// (호스트 키가 다른 sshd 두 개를 이 PC 에 띄운다. 인증은 하지 않고 호스트 키 단계만 본다)
///   sshd -f cfg   (ListenAddress 127.0.0.1, Port A, HostKey k1)
///   sshd -f cfg2  (Port B, HostKey k2)
///   GODDB_SSHD_PORT=A GODDB_SSHD_OTHER_PORT=B ./scripts/test.sh
public class SSHHostKeysIntegrationTests
{
    private const string EdKeyFile = "known_hosts";

    private static SshHostKeys.StderrKind? RunSsh(int port, string knownHosts)
    {
        var config = new SshConfig { Host = "127.0.0.1", Port = port, Username = "goddb-nobody" };
        var args = SshTunnel.Arguments(config, 1, "127.0.0.1", 1, knownHosts);
        var ssh = SshHostKeys.Tool("ssh")!;
        var r = SshHostKeys.Run(ssh, ["-F", OperatingSystem.IsWindows() ? "NUL" : "/dev/null", .. args], timeout: TimeSpan.FromSeconds(15));
        Assert.NotEqual(0, r.Status);   // 인증은 실패한다 (키 없음)
        return SshHostKeys.Classify(r.Stderr);
    }

    [Fact]
    public void UnknownThenTrustThenChanged()
    {
        var portText = Environment.GetEnvironmentVariable("GODDB_SSHD_PORT");
        if (string.IsNullOrEmpty(portText)) Assert.Skip("GODDB_SSHD_PORT 없음 (sshd 를 직접 띄울 때만)");
        var port = int.Parse(portText!);
        var other = int.TryParse(Environment.GetEnvironmentVariable("GODDB_SSHD_OTHER_PORT"), out var o) ? o : 0;
        var dir = Path.Combine(Path.GetTempPath(), "goddb-kh-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, EdKeyFile);
        var noUser = Path.Combine(dir, "none");
        try
        {
            var config = new SshConfig { Host = "127.0.0.1", Port = port, Username = "u" };
            // 1) 모르는 서버 → 지문과 함께 확인 요청
            var e = Assert.Throws<SshHostKeyException>(() => SshHostKeys.Check(config, file, noUser));
            Assert.Equal(SshHostKeyErrorKind.Unknown, e.Kind);
            var s = e.Scan!;
            Assert.Equal($"[127.0.0.1]:{port}", s.Pattern);
            Assert.Contains(s.Keys, k => k.Type == "ED25519" && k.Fingerprint.StartsWith("SHA256:", StringComparison.Ordinal));

            // 2) 신뢰 → 다음에는 묻지 않고, ssh 도 호스트 키를 받아들인다 (인증 단계까지 감)
            SshHostKeys.Trust(s, file);
            SshHostKeys.Check(config, file, noUser);
            Assert.Null(RunSsh(port, file));

            // 3) 같은 이름에 다른 키를 가진 서버 → ssh 가 거부, "바뀜" 으로 판정
            if (other > 0)
            {
                var fakeLines = s.Lines.Select(l => l.Replace($"[127.0.0.1]:{port}", $"[127.0.0.1]:{other}")).ToList();
                SshHostKeys.Append(fakeLines, file);
                Assert.Equal(SshHostKeys.StderrKind.Changed, RunSsh(other, file));
                // 4) 저장된 키 지우기 → 다시 처음 접속 확인
                Assert.True(SshHostKeys.Remove($"[127.0.0.1]:{other}", file));
                Assert.Throws<SshHostKeyException>(() =>
                    SshHostKeys.Check(new SshConfig { Host = "127.0.0.1", Port = other, Username = "u" }, file, noUser));
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
