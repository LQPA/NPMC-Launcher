using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NPMC.Launcher.Core.Abstractions;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Paths;

namespace NPMC.Launcher.Infrastructure.Storage;

/// <summary>
/// 以单个 JSON 文件保存实例列表。
///
/// 为什么用 JSON 而不是 SQLite：
///   自用场景实例数不会超过几十个，单文件可手改、可用 diff 审阅，
///   这比数据库的运维成本低得多。
/// </summary>
public sealed class JsonInstanceStore : IInstanceStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },

        // 默认编码器会把中文转成 \uXXXX，那样"单文件可手改"就失去意义了
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;

    public JsonInstanceStore(LauncherPaths paths) => _filePath = Path.Combine(paths.Root, "instances.json");

    public JsonInstanceStore(string filePath) => _filePath = filePath;

    public string FilePath => _filePath;

    public async Task<IReadOnlyList<LauncherInstance>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return Array.Empty<LauncherInstance>();

        var json = await File.ReadAllTextAsync(_filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<LauncherInstance>();

        var document = JsonSerializer.Deserialize<InstancesDocument>(json, Options);
        return document?.Instances ?? (IReadOnlyList<LauncherInstance>)Array.Empty<LauncherInstance>();
    }

    public async Task SaveAsync(IReadOnlyList<LauncherInstance> instances, CancellationToken cancellationToken = default)
    {
        var document = new InstancesDocument { Instances = instances.ToList() };
        var json = JsonSerializer.Serialize(document, Options);
        await AtomicFile.WriteAllTextAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
    }

    private sealed class InstancesDocument
    {
        [JsonPropertyName("instances")] public List<LauncherInstance> Instances { get; set; } = new();
    }
}
