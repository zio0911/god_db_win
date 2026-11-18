using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace GodDB.Core.Audit;

/// 이 PC 와 사용자 정보 (감사 로그용). 외부 서비스에 묻지 않고 PC 안에서만 구한다.
public static class ClientInfo
{
    /// <param name="Interface">어댑터 이름 (Windows: "이더넷", "Wi-Fi" …, Mac: en0, utun3 …)</param>
    public sealed record Address(string Interface, string Ip, bool IsVpn = false);

    /// 로그인 계정
    public static string UserName => Environment.UserName;

    /// 계정의 전체 이름. Mac 은 NSFullUserName() 을 쓰지만 Windows 에는 네트워크를 쓰지 않는 간단한 API 가 없어 비워 둔다.
    public static string FullName => "";

    /// 컴퓨터 이름 (Windows: NetBIOS 컴퓨터 이름, DNS 조회 없음)
    public static string ComputerName
    {
        get
        {
            try { if (Environment.MachineName is { Length: > 0 } n) return n; } catch { }
            return HostName;
        }
    }

    /// 로컬 호스트 이름 (gethostname, DNS 조회 없음)
    public static string HostName
    {
        get
        {
            try { if (Dns.GetHostName() is { Length: > 0 } n) return n; } catch { }
            return "localhost";
        }
    }

    /// 운영체제 버전 (Mac: "macOS 15.1.0", Windows: "Windows 10.0.22631")
    public static string OsVersion
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var v = Environment.OSVersion.Version;
                return $"Windows {v.Major}.{v.Minor}.{v.Build}";
            }
            return RuntimeInformation.OSDescription;
        }
    }

    /// 로그 줄마다 붙이는 "사용자@컴퓨터" (공백은 - 로)
    public static readonly string Identity = $"{UserName}@{ComputerName.Replace(" ", "-")}";

    private static readonly string[] VpnNamePrefixes = ["utun", "ppp", "ipsec", "tun", "tap"];
    private static readonly string[] VpnDescriptionWords =
        ["vpn", "wireguard", "wintun", "tap-windows", "openvpn", "anyconnect", "fortinet", "forticlient", "globalprotect", "pangp", "zscaler"];

    /// VPN 어댑터처럼 보이는지. Mac 은 이름(utun/ppp/ipsec/tun/tap)으로, Windows 는 어댑터 종류(PPP·터널)와 설명으로 본다.
    public static bool LooksLikeVpn(string name, string description = "", NetworkInterfaceType type = NetworkInterfaceType.Unknown)
    {
        if (VpnNamePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return true;
        if (type is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel) return true;
        var d = description.ToLowerInvariant();
        return VpnDescriptionWords.Any(d.Contains);
    }

    /// 이 PC 의 IP 들 (루프백, 링크로컬 제외). IPv4 먼저.
    public static List<Address> Addresses()
    {
        var output = new List<Address>();
        NetworkInterface[] list;
        try { list = NetworkInterface.GetAllNetworkInterfaces(); } catch { return output; }
        foreach (var ni in list)
        {
            try
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var vpn = LooksLikeVpn(ni.Name, SafeDescription(ni), ni.NetworkInterfaceType);
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var a = ua.Address;
                    if (a.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) continue;
                    if (IPAddress.IsLoopback(a)) continue;
                    var ip = StripScope(a.ToString());
                    if (ip.StartsWith("fe80", StringComparison.OrdinalIgnoreCase)) continue;
                    output.Add(new Address(ni.Name, ip, vpn));
                }
            }
            catch { }
        }
        return output
            .OrderBy(a => a.Ip.Contains(':') ? 1 : 0)
            .ThenBy(a => a.Interface, StringComparer.Ordinal)
            .ToList();
    }

    private static string SafeDescription(NetworkInterface ni)
    {
        try { return ni.Description ?? ""; } catch { return ""; }
    }

    private static string StripScope(string ip) => ip.Split('%')[0];

    /// 상대 호스트로 나갈 때 쓰이는 이 PC 의 IP (라우팅 표 기준, 실제로 패킷은 보내지 않음)
    public static string? SourceIp(string host, int port)
    {
        IPAddress[] targets;
        if (IPAddress.TryParse(host, out var literal)) targets = [literal];
        else
        {
            try { targets = Dns.GetHostAddresses(host); } catch { return null; }
        }
        foreach (var target in targets)
        {
            try
            {
                using var s = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                s.Connect(new IPEndPoint(target, port));
                if (s.LocalEndPoint is IPEndPoint local) return StripScope(local.Address.ToString());
            }
            catch { }
        }
        return null;
    }

    /// 앱 시작 로그 상세
    public static string Summary()
    {
        var ips = Addresses().Select(a => $"{a.Ip} ({a.Interface}{(a.IsVpn ? ", VPN" : "")})").ToList();
        return string.Join('\n',
            $"user: {UserName}" + (FullName.Length == 0 || FullName == UserName ? "" : $" ({FullName})"),
            $"computer: {ComputerName}",
            $"host: {HostName}",
            $"os: {OsVersion}",
            "ip: " + (ips.Count == 0 ? "-" : string.Join(", ", ips)));
    }

    /// 접속할 때 남길 한 줄: 이 PC 에서 나가는 IP (SSH 면 바스티온으로 나가는 IP)
    public static string ConnectionNote(string host, int port, SshConfig? ssh)
    {
        if (ssh != null)
        {
            var viaIp = SourceIp(ssh.Host, ssh.Port) ?? "?";
            return $"client {viaIp} → ssh {ssh.Host}:{ssh.Port} → {host}:{port}";
        }
        var ip = SourceIp(host, port) ?? "?";
        return $"client {ip} → {host}:{port}";
    }
}
