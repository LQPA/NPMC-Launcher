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

    public MicrosoftTokenStore(string directory)
    {
        _directory = directory;
        TokenDirectoryAcl.Protect(_directory);
    }

    public string Directory => _directory;

    public string FilePath(string accountId) =>
        Path.Combine(_directory, accountId + ".bin");

    public void Save(string accountId, string refreshToken)
    {
        System.IO.Directory.CreateDirectory(_directory);
        var encrypted = WindowsDpapi.Protect(Encoding.UTF8.GetBytes(refreshToken));
        File.WriteAllBytes(FilePath(accountId), encrypted);

        // 新写入的 .bin 也收进“仅当前用户/SYSTEM/管理员”的 ACL
        TokenDirectoryAcl.Protect(_directory);
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

    /// <summary>删除本机保存的全部微软 refresh token；返回删除个数。账号条目本身不动。</summary>
    public int DeleteAll()
    {
        if (!System.IO.Directory.Exists(_directory)) return 0;

        var deleted = 0;

        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*.bin"))
        {
            try
            {
                System.IO.File.Delete(path);
                deleted++;
            }
            catch
            {
                // 删不掉的（占用/权限）跳过，不动其他文件
            }
        }

        return deleted;
    }
}
