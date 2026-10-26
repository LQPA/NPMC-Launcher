using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NPMC.Launcher.Infrastructure.Http;
using NPMC.Launcher.Infrastructure.Storage;

namespace NPMC.Launcher.Infrastructure.Skins;

/// <summary>取回来的皮肤：本地缓存 PNG + 模型（classic / slim）。</summary>
public sealed record FetchedSkin(string Path, string Model);

/// <summary>
/// 按用户名查 Mojang 的正版皮肤并把 PNG 缓存到本地。
///
/// 这是"同名正版账户皮肤"的来源；离线账号本身没有令牌，进游戏能不能显示
/// 取决于皮肤站 / 模组（见 handoff），这里只负责把皮肤文件和模型取回来。
/// </summary>
public static class MojangSkinClient
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<FetchedSkin?> TryFetchAsync(
        string username,
        string cacheDirectory,
        CancellationToken cancellationToken = default)
    {
        var profileJson = await TryGetAsync(
            $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(username)}",
            cancellationToken);

        if (profileJson is null) return null;

        var profile = JsonSerializer.Deserialize<MojangProfile>(profileJson, JsonConfig.Options);
        if (profile?.Id is not { Length: > 0 } profileId) return null;

        var sessionJson = await TryGetAsync(
            $"https://sessionserver.mojang.com/session/minecraft/profile/{profileId}",
            cancellationToken);

        if (sessionJson is null) return null;

        var session = JsonSerializer.Deserialize<MojangSession>(sessionJson, JsonConfig.Options);
        var encoded = session?.Properties?
            .FirstOrDefault(property => string.Equals(property.Name, "textures", StringComparison.OrdinalIgnoreCase))?
            .Value ?? session?.Properties?.FirstOrDefault()?.Value;

        if (encoded is null) return null;

        string? skinUrl = null;
        string model = "classic";

        try
        {
            var payload = JsonSerializer.Deserialize<TexturesPayload>(
                Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), JsonConfig.Options);

            skinUrl = payload?.Textures?.Skin?.Url;
            model = payload?.Textures?.Skin?.Metadata?.Model ?? "classic";
        }
        catch
        {
            return null;
        }

        if (skinUrl is not { Length: > 0 }) return null;

        Directory.CreateDirectory(cacheDirectory);

        var safeName = new string(username.Where(char.IsLetterOrDigit).ToArray());
        var path = Path.Combine(cacheDirectory, $"{safeName}-{DateTime.UtcNow.Ticks}.png");

        try
        {
            using var response = await Shared.GetAsync(skinUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var file = File.Create(path);
            await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        return new FetchedSkin(path, model == "slim" ? "slim" : "classic");
    }

    private static async Task<string?> TryGetAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Shared.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private sealed class MojangProfile
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class MojangSession
    {
        [JsonPropertyName("properties")] public List<SessionProperty>? Properties { get; set; }
    }

    private sealed class SessionProperty
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("value")] public string? Value { get; set; }
    }

    private sealed class TexturesPayload
    {
        [JsonPropertyName("textures")] public Textures? Textures { get; set; }
    }

    private sealed class Textures
    {
        [JsonPropertyName("SKIN")] public SkinTexture? Skin { get; set; }
    }

    private sealed class SkinTexture
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("metadata")] public SkinMetadata? Metadata { get; set; }
    }

    private sealed class SkinMetadata
    {
        [JsonPropertyName("model")] public string? Model { get; set; }
    }
}
