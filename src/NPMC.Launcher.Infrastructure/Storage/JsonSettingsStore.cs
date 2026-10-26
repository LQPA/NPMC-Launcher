using System.Text;
using System.Text.Json;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Core.Settings;

namespace NPMC.Launcher.Infrastructure.Storage;

/// <summary>全局设置：&lt;root&gt;\settings.json，原子写入。</summary>
public sealed class JsonSettingsStore
{
    private readonly string _filePath;

    public JsonSettingsStore(LauncherPaths paths) => _filePath = Path.Combine(paths.Root, "settings.json");

    public string FilePath => _filePath;

    /// <summary>文件不存在或内容为空时返回默认设置；解析失败抛异常，由调用方决定是否兜底。</summary>
    public async Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return new LauncherSettings();

        var json = await File.ReadAllTextAsync(_filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return new LauncherSettings();

        return JsonSerializer.Deserialize<LauncherSettings>(json, JsonConfig.Options) ?? new LauncherSettings();
    }

    /// <summary>
    /// 同步读一次，给"建 HttpClient 之前"用（代理与超时只能在建客户端时设定）。
    /// 读不出来就返回默认值 —— 启动路径不该因为 settings.json 写坏而失败。
    /// </summary>
    public LauncherSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new LauncherSettings();

            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return new LauncherSettings();

            return JsonSerializer.Deserialize<LauncherSettings>(json, JsonConfig.Options) ?? new LauncherSettings();
        }
        catch
        {
            return new LauncherSettings();
        }
    }

    public async Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(settings, JsonConfig.Options);
        await AtomicFile.WriteAllTextAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
    }
}
