using System.IO.Compression;
using System.Text.Json;

namespace NPMC.Launcher.Core.Modpacks;

/// <summary>
/// 整合包自带的"预设参数"：JVM 参数与一键进服地址。
///
/// 为什么要支持：社区整合包（尤其一键进服那种）习惯把参数钉在包里，
/// 让玩家导入后直接连到作者的服务器。
///
/// 安全前提：这些参数是**任意代码执行入口**（`-javaagent:` 之类），
/// 所以只在导入时展示一次原文、由用户知情后再写入实例，绝不在启动时偷偷注入。
///
/// 来源优先级：MultiMC 的 <c>instance.cfg</c>（标准字段）优先，
/// 其次包根的 <c>.nmpc-pack.json</c>（我们自己的约定，其它格式也能用）。
/// </summary>
public sealed record ModpackExtras(
    IReadOnlyList<string> JvmArguments,
    string? QuickPlayServer)
{
    public static ModpackExtras Empty { get; } = new(Array.Empty<string>(), null);

    public bool IsEmpty => JvmArguments.Count == 0 && string.IsNullOrWhiteSpace(QuickPlayServer);

    /// <summary>从包根的 <c>.nmpc-pack.json</c> 读取（文件不存在就返回 Empty）。</summary>
    public static ModpackExtras FromArchive(string archivePath)
    {
        try
        {
            if (!File.Exists(archivePath)) return Empty;

            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.GetEntry(PackExtrasEntry);
            if (entry is null) return Empty;

            using var reader = new StreamReader(entry.Open());
            return Parse(reader.ReadToEnd());
        }
        catch
        {
            return Empty;
        }
    }

    public const string PackExtrasEntry = ".nmpc-pack.json";

    /// <summary>解析 JSON。字段：jvmArgs（字符串或字符串数组）、quickPlayServer。</summary>
    public static ModpackExtras Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Empty;

            var jvm = new List<string>();

            if (root.TryGetProperty("jvmArgs", out var jvmElement))
            {
                if (jvmElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in jvmElement.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                        {
                            jvm.AddRange(SplitArguments(text));
                        }
                    }
                }
                else if (jvmElement.ValueKind == JsonValueKind.String && jvmElement.GetString() is { Length: > 0 } raw)
                {
                    jvm.AddRange(SplitArguments(raw));
                }
            }

            var server = root.TryGetProperty("quickPlayServer", out var serverElement)
                         && serverElement.ValueKind == JsonValueKind.String
                ? serverElement.GetString()?.Trim()
                : null;

            if (string.IsNullOrWhiteSpace(server)) server = null;

            return new ModpackExtras(jvm, server);
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>
    /// 一键进服的启动参数：1.20+ 用 <c>--quickPlayMultiplayer host:port</c>，
    /// 更老的版本退回 <c>--server host --port port</c>。
    /// </summary>
    public static List<string> BuildQuickPlayArguments(string? server, string gameVersion)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(server)) return args;

        var address = server.Trim();

        var host = address;
        var port = "25565";
        var separator = address.LastIndexOf(':');
        if (separator > 0 && separator < address.Length - 1
            && int.TryParse(address[(separator + 1)..], out var parsedPort) && parsedPort is > 0 and < 65536)
        {
            host = address[..separator];
            port = parsedPort.ToString();
        }

        if (SupportsQuickPlay(gameVersion))
        {
            args.Add("--quickPlayMultiplayer");
            args.Add($"{host}:{port}");
        }
        else
        {
            args.Add("--server");
            args.Add(host);
            args.Add("--port");
            args.Add(port);
        }

        return args;
    }

    /// <summary>1.20 起才有 quickPlayMultiplayer；快照号解析不出来时按老版本处理。</summary>
    public static bool SupportsQuickPlay(string gameVersion)
    {
        var parts = gameVersion.Split('.');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return false;

        return major > 1 || (major == 1 && minor >= 20);
    }
    /// <summary>
    /// 按空白切分参数，支持双引号括起的片段（`-Dfoo="bar baz"`）。
    /// 整合包里的 JvmArgs 是一整行，不能简单按空格切。
    /// </summary>
    public static List<string> SplitArguments(string raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var ch in raw)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>把预设参数合并：标准字段优先，我们的清单只补缺口。</summary>
    public static ModpackExtras Merge(ModpackExtras primary, ModpackExtras fallback)
    {
        if (primary.IsEmpty) return fallback;
        if (fallback.IsEmpty) return primary;

        return new ModpackExtras(
            primary.JvmArguments.Count > 0 ? primary.JvmArguments : fallback.JvmArguments,
            primary.QuickPlayServer ?? fallback.QuickPlayServer);
    }
}
