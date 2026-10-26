using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NPMC.Launcher.Core.Abstractions;
using NPMC.Launcher.Core.Accounts;
using NPMC.Launcher.Core.Paths;

namespace NPMC.Launcher.Infrastructure.Storage;

/// <summary>
/// 以单个 JSON 文件保存账号列表（&lt;root&gt;\accounts.json）。
/// 离线账号没有敏感信息；以后接入微软令牌时，令牌会单独存到受保护的位置，
/// 不会写进这个可手改的文件。
/// </summary>
public sealed class JsonAccountStore : IAccountStore
{
    private static readonly JsonSerializerOptions Options = JsonConfig.Options;

    private readonly string _filePath;

    public JsonAccountStore(LauncherPaths paths) => _filePath = Path.Combine(paths.Root, "accounts.json");

    public JsonAccountStore(string filePath) => _filePath = filePath;

    public string FilePath => _filePath;

    public async Task<IReadOnlyList<PlayerAccount>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return Array.Empty<PlayerAccount>();

        var json = await File.ReadAllTextAsync(_filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<PlayerAccount>();

        var document = JsonSerializer.Deserialize<AccountsDocument>(json, Options);
        return document?.Accounts ?? (IReadOnlyList<PlayerAccount>)Array.Empty<PlayerAccount>();
    }

    public async Task SaveAsync(IReadOnlyList<PlayerAccount> accounts, CancellationToken cancellationToken = default)
    {
        var document = new AccountsDocument { Accounts = accounts.ToList() };
        var json = JsonSerializer.Serialize(document, Options);
        await AtomicFile.WriteAllTextAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
    }

    private sealed class AccountsDocument
    {
        [JsonPropertyName("accounts")] public List<PlayerAccount> Accounts { get; set; } = new();
    }
}
