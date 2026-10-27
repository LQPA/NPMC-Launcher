using System.Text;
using NPMC.Launcher.Core.Paths;

namespace NPMC.Launcher.Infrastructure.Accounts;

/// <summary>
/// 每个微软账号一个 DPAPI 加密的 refresh token 文件，路径
/// &lt;root&gt;\microsoft-tokens\&lt;accountId&gt;.bin。
/// accounts.json 只保存账号公开信息，永远不写 token。
/// </summary>
public sealed class MicrosoftTokenStore
{
    private readonly string _directory;

    public MicrosoftTokenStore(LauncherPaths paths)
        : this(Path.Combine(paths.Root, "microsoft-tokens"))
    {
    }

    public MicrosoftTokenStore(string directory) => _directory = directory;

    public string Directory => _directory;

    public string FilePath(string accountId) =>
        Path.Combine(_directory, accountId + ".bin");

    public void Save(string accountId, string refreshToken)
    {
        System.IO.Directory.CreateDirectory(_directory);
        var encrypted = WindowsDpapi.Protect(Encoding.UTF8.GetBytes(refreshToken));
        File.WriteAllBytes(FilePath(accountId), encrypted);
    }

    public string? Load(string accountId)
    {
        var path = FilePath(accountId);
        if (!File.Exists(path)) return null;

        try
        {
            return Encoding.UTF8.GetString(WindowsDpapi.Unprotect(File.ReadAllBytes(path)));
        }
        catch
        {
            // 损坏 / 换了 Windows 用户：当成没有 token，要求重新登录
            return null;
        }
    }

    public void Delete(string accountId)
    {
        var path = FilePath(accountId);
        if (File.Exists(path)) File.Delete(path);
    }
}
