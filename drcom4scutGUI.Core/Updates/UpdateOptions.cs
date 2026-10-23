using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace drcom4scutGUI;

/// <summary>Nonsecret updater routing, independent of account and password storage.</summary>
public sealed record UpdateOptions
{
    [JsonPropertyName("proxyUrl")]
    public string ProxyUrl { get; init; } = UpdateService.ProxyUrl;

    internal Uri Validate()
    {
        if (ProxyUrl is null || ProxyUrl != ProxyUrl.Trim() || ProxyUrl.IndexOfAny(['\r', '\n', '\t', '\0']) >= 0
            || !Uri.TryCreate(ProxyUrl, UriKind.Absolute, out Uri? address) || address.Scheme != "https"
            || address.Host.Length == 0 || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0
            || !address.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
            throw new InvalidDataException("更新代理 proxyUrl 必须是以 / 结尾的 HTTPS 前缀，且不能包含用户名、密码、查询参数或片段。");
        return address;
    }
}

public sealed class UpdateOptionsStore(string? path = null)
{
    public string FilePath { get; } = Path.GetFullPath(path ?? Path.Combine(PlatformPaths.ConfigurationDirectory, "updates.json"));

    public UpdateOptions Load()
    {
        if (!File.Exists(FilePath)) return new UpdateOptions();
        try
        {
            if (new FileInfo(FilePath).Length > 8192) throw new InvalidDataException("更新代理设置文件过大。");
            UpdateOptions options = JsonSerializer.Deserialize<UpdateOptions>(File.ReadAllBytes(FilePath),
                new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 4 })
                ?? throw new InvalidDataException("更新代理设置必须是 JSON 对象。");
            _ = options.Validate();
            return options;
        }
        catch (JsonException error) { throw new InvalidDataException("更新代理设置 updates.json 无效，请检查 proxyUrl；不会直接连接下载源。", error); }
    }
}
