using GodDB.Core.Platform;

namespace GodDB.Core.Tests;

public class SshTunnelTests
{
    [Fact]
    public void BuildsArguments()
    {
        var cfg = new SshConfig { Host = "bastion.example.com", Port = 2222, Username = "ec2-user", KeyPath = "" };
        var args = SshTunnel.Arguments(cfg, 50000, "10.0.0.5", 6379);
        Assert.Equal([
            "-N",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", "ServerAliveInterval=30",
            "-o", "ConnectTimeout=10",
            "-p", "2222",
            "-L", "127.0.0.1:50000:10.0.0.5:6379",
            "ec2-user@bastion.example.com",
        ], args);
    }

    [Fact]
    public void KeyFileAndNoUser()
    {
        var cfg = new SshConfig { Host = "bastion.example.com", Username = "", KeyPath = "~/.ssh/bastion.pem" };
        var args = SshTunnel.Arguments(cfg, 1, "db", 3306);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        int i = args.IndexOf("-i");
        Assert.True(i > 0);
        Assert.Equal(Path.Combine(home, ".ssh/bastion.pem"), args[i + 1]);
        Assert.Equal(["-o", "IdentitiesOnly=yes"], args.Skip(i + 2).Take(2));
        Assert.Equal("bastion.example.com", args[^1]);
        Assert.Contains("22", args);
    }

    [Fact]
    public void PicksSshExecutable()
    {
        var sys = Path.Join(@"C:\Windows", "System32", "OpenSSH", "ssh.exe");
        // Windows: 내장 OpenSSH 먼저
        Assert.Equal(sys, SshTunnel.FindSsh(true, @"C:\Windows", @"C:\Tools", p => p == sys || p == Path.Join(@"C:\Tools", "ssh.exe")));
        // 없으면 PATH 의 ssh.exe
        Assert.Equal(Path.Join(@"C:\Git\usr\bin", "ssh.exe"),
            SshTunnel.FindSsh(true, @"C:\Windows", @"C:\Nope;""C:\Git\usr\bin""", p => p == Path.Join(@"C:\Git\usr\bin", "ssh.exe")));
        // 아무 데도 없으면 null (→ OpenSSH 클라이언트 설치 안내)
        Assert.Null(SshTunnel.FindSsh(true, @"C:\Windows", @"C:\Nope", _ => false));
        // Mac/Linux: /usr/bin/ssh 먼저, 없으면 PATH
        Assert.Equal("/usr/bin/ssh", SshTunnel.FindSsh(false, null, "/opt/bin", p => p is "/usr/bin/ssh" or "/opt/bin/ssh"));
        Assert.Equal("/opt/bin/ssh", SshTunnel.FindSsh(false, null, "/x:/opt/bin", p => p == "/opt/bin/ssh"));
    }

    [Fact]
    public void ErrorMessages()
    {
        Assert.Contains("OpenSSH 클라이언트", new SshTunnelException(SshTunnelErrorKind.SshNotFound).Message);
        Assert.Equal("SSH 터널 연결에 실패했어요.\nPermission denied", new SshTunnelException(SshTunnelErrorKind.Exited, "Permission denied").Message);
        Assert.True(L10n.EnglishTable.ContainsKey(
            "ssh 프로그램을 찾지 못했어요. Windows 설정 > 시스템 > 선택적 기능에서 \"OpenSSH 클라이언트\"를 설치해 주세요."));
    }

    [Fact]
    public void FindsFreePort()
    {
        var p = SshTunnel.FindFreePort();
        Assert.NotNull(p);
        Assert.InRange(p!.Value, 1, 65535);
    }
}
