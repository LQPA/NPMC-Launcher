using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPMC.Launcher.Core.Models;

/// <summary>
/// 版本元数据中的规则单元。求值语义见 <see cref="Rules.RuleEvaluator"/>。
/// </summary>
public sealed record Rule
{
    [JsonPropertyName("action")] public string Action { get; init; } = "allow";
    [JsonPropertyName("os")] public RuleOs? Os { get; init; }
    [JsonPropertyName("features")] public Dictionary<string, bool>? Features { get; init; }
}

public sealed record RuleOs
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("arch")] public string? Arch { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
}

/// <summary>
/// arguments.game / arguments.jvm 的单个元素。
/// 既可能是纯字符串，也可能是 { "rules": [...], "value": "..." | ["..."] }。
/// </summary>
[JsonConverter(typeof(ArgumentEntryConverter))]
public sealed record ArgumentEntry(IReadOnlyList<Rule>? Rules, IReadOnlyList<string> Values);

public sealed class ArgumentEntryConverter : JsonConverter<ArgumentEntry>
{
    public override ArgumentEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new ArgumentEntry(null, new[] { reader.GetString() ?? string.Empty });

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        List<Rule>? rules = null;
        if (root.TryGetProperty("rules", out var rulesEl) && rulesEl.ValueKind == JsonValueKind.Array)
            rules = JsonSerializer.Deserialize<List<Rule>>(rulesEl.GetRawText(), options);

        var values = new List<string>();
        if (root.TryGetProperty("value", out var valueEl))
        {
            if (valueEl.ValueKind == JsonValueKind.String)
            {
                values.Add(valueEl.GetString() ?? string.Empty);
            }
            else if (valueEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in valueEl.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String)
                        values.Add(item.GetString()!);
            }
        }

        return new ArgumentEntry(rules, values);
    }

    public override void Write(Utf8JsonWriter writer, ArgumentEntry value, JsonSerializerOptions options)
        => throw new NotSupportedException("ArgumentEntry 仅用于反序列化。");
}

public sealed record DownloadArtifact
{
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("sha1")] public string Sha1 { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("url")] public string Url { get; init; } = "";
}

public sealed record LibraryDownloads
{
    [JsonPropertyName("artifact")] public DownloadArtifact? Artifact { get; init; }
    [JsonPropertyName("classifiers")] public Dictionary<string, DownloadArtifact>? Classifiers { get; init; }
}

public sealed record Library
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("downloads")] public LibraryDownloads? Downloads { get; init; }
    [JsonPropertyName("rules")] public List<Rule>? Rules { get; init; }
    [JsonPropertyName("natives")] public Dictionary<string, string>? Natives { get; init; }
}

public sealed record AssetIndexRef
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("sha1")] public string Sha1 { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("totalSize")] public long TotalSize { get; init; }
    [JsonPropertyName("url")] public string Url { get; init; } = "";
}

public sealed record JavaVersionInfo
{
    [JsonPropertyName("component")] public string Component { get; init; } = "";
    [JsonPropertyName("majorVersion")] public int MajorVersion { get; init; }
}

public sealed record LoggingFileRef
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("sha1")] public string Sha1 { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("url")] public string Url { get; init; } = "";
}

public sealed record LoggingClient
{
    [JsonPropertyName("argument")] public string Argument { get; init; } = "";
    [JsonPropertyName("file")] public LoggingFileRef? File { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
}

public sealed record LoggingInfo
{
    [JsonPropertyName("client")] public LoggingClient? Client { get; init; }
}

public sealed record VersionDownloads
{
    [JsonPropertyName("client")] public DownloadArtifact? Client { get; init; }
}

public sealed record VersionArguments
{
    [JsonPropertyName("game")] public List<ArgumentEntry> Game { get; init; } = new();
    [JsonPropertyName("jvm")] public List<ArgumentEntry> Jvm { get; init; } = new();
}

public sealed record VersionJson
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("mainClass")] public string MainClass { get; init; } = "";
    [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; init; }
    [JsonPropertyName("assets")] public string? Assets { get; init; }
    [JsonPropertyName("assetIndex")] public AssetIndexRef? AssetIndex { get; init; }
    [JsonPropertyName("downloads")] public VersionDownloads? Downloads { get; init; }
    [JsonPropertyName("libraries")] public List<Library> Libraries { get; init; } = new();
    [JsonPropertyName("arguments")] public VersionArguments? Arguments { get; init; }
    [JsonPropertyName("minecraftArguments")] public string? MinecraftArguments { get; init; }
    [JsonPropertyName("javaVersion")] public JavaVersionInfo? JavaVersion { get; init; }
    [JsonPropertyName("logging")] public LoggingInfo? Logging { get; init; }
    [JsonPropertyName("minimumLauncherVersion")] public int MinimumLauncherVersion { get; init; }
}

public static class VersionJsonSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static VersionJson Parse(string json) =>
        JsonSerializer.Deserialize<VersionJson>(json, Options)
        ?? throw new InvalidDataException("版本 JSON 解析结果为空。");
}
