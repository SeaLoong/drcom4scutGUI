using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace drcom4scutGUI;

/// <summary>
/// Portal hosts are explicit user choices. Local source IP and the address sent
/// in the portal payload are distinct fields; no host or HTTP fallback is guessed.
/// </summary>
public static class PortalOptions
{
    public static void ValidateStored(Configuration configuration)
    {
        if (configuration.Mode is not ("eap" or "portal"))
            throw new InvalidDataException("认证模式不受支持，原配置未更改。");
        // Incomplete or HTTP profiles may be edited and stored, but credentials
        // embedded in URLs are never treated as non-sensitive configuration.
        if (configuration.PortalBase.Length > 0) ParseUrl(configuration.PortalBase);
        if (configuration.PortalApi.Length > 0) ParseUrl(configuration.PortalApi);
    }

    public static void ValidateLaunch(CoreLaunchOptions options)
    {
        if (options.Mode is not ("eap" or "portal")) throw new ArgumentException("认证模式不受支持。");
        if (options.Mode != "portal") return;
        Uri origin = ParseUrl(options.PortalBase);
        if (origin.Scheme == Uri.UriSchemeHttp && !options.AllowHttp)
            throw new ArgumentException("HTTP 门户会在网络中明文发送密码。只有显式勾选允许 HTTP 后才能使用。");
        if (options.PortalApi.Length > 0)
        {
            Uri api = ParseUrl(options.PortalApi);
            if (!string.Equals(api.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase)
                || api.Query.Length != 0
                || origin.Scheme == Uri.UriSchemeHttps && api.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("门户 API 必须使用同一可信主机，不能从 HTTPS 降级为 HTTP 或包含查询参数。");
            if (api.Scheme == Uri.UriSchemeHttp && !options.AllowHttp)
                throw new ArgumentException("HTTP 门户 API 需要显式允许明文网络传输。");
        }
        ValidateIpv4(options.PortalAcIp);
        ValidateIpv4(options.PortalClientIp);
    }

    private static Uri ParseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("https" or "http")
            || uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("请填写明确的 HTTP(S) 门户地址，地址中不得包含账号密码或片段。");
        foreach (string parameter in uri.Query.TrimStart('?').Split(['&', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            string key = Uri.UnescapeDataString(parameter.Split('=', 2)[0].Replace('+', ' '))
                .Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal)
                .Trim().ToLowerInvariant();
            if (key is not ("wlanacip" or "wlanuserip" or "wlanusermac" or "wlanacname"
                or "acip" or "userip" or "mac" or "clientip" or "clientmac" or "wlanvlanid"
                or "vlan" or "vlanid" or "nasip" or "nasid" or "gwport" or "gwaddress"
                or "gwid" or "ssid" or "wlanacssid" or "networkid"))
                throw new ArgumentException("门户地址查询参数仅允许公开的接入点发现信息，不得携带账号密码、令牌或会话凭据。");
        }
        return uri;
    }

    private static void ValidateIpv4(string value)
    {
        if (value.Length == 0) return;
        if (value.Split('.').Length != 4 || !IPAddress.TryParse(value, out IPAddress? address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast)
            || address.GetAddressBytes()[0] is >= 224 and <= 239)
            throw new ArgumentException("门户 AC / 客户端载荷地址必须是有效 IPv4，不填写时由门户流程发现。");
    }
}
