using System.Net;
using System.Net.Sockets;

namespace GuliERP.Crm.Infrastructure.Services;

/// <summary>Restricts CRM website probes to public DNS names and public IPv4 destinations.</summary>
public static class PublicWebTarget
{
    public static string Host(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 500)
            throw new ArgumentException("网址无效。");
        var value = input.Trim();
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort)
            throw new ArgumentException("只允许标准 HTTP/HTTPS 网站域名。");
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length > 253 || host.IndexOf('.') < 1 || IPAddress.TryParse(host, out _)
            || host.Split('.').Any(label => label.Length is < 1 or > 63
                || label[0] == '-' || label[^1] == '-'
                || label.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))))
            throw new ArgumentException("只允许公开网站域名，不允许 IP 或本地域名。");
        return host;
    }

    public static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        // The connection callback uses only this IPv4 address. Ignore AAAA answers,
        // but fail closed for IPv6-only sites and any private IPv4 answer.
        var ipv4 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
        if (ipv4.Length == 0 || ipv4.Any(a => !IsPublicIpv4(a)))
            throw new ArgumentException("网站域名解析到非公网地址，已拒绝检测。");
        return ipv4[0];
    }

    public static bool IsPublicIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] is > 0 and < 224
            && b[0] != 10 && b[0] != 127
            && !(b[0] == 100 && b[1] is >= 64 and <= 127)
            && !(b[0] == 169 && b[1] == 254)
            && !(b[0] == 172 && b[1] is >= 16 and <= 31)
            && !(b[0] == 192 && (b[1] == 0 || b[1] == 168))
            && !(b[0] == 198 && (b[1] == 18 || b[1] == 19 || (b[1] == 51 && b[2] == 100)))
            && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var address = await ResolveAsync(context.DnsEndPoint.Host, ct);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
