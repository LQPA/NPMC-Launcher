using NPMC.Launcher.App.ViewModels;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Modpacks;
using NPMC.Launcher.Core.Mods;
using Xunit;

namespace NPMC.Launcher.App.Tests;

/// <summary>
/// 整合包来源 → 模组来源徽标。
///
/// 背景：导入 MultiMC 实例包后，所有模组都被标成"已在 Modrinth 识别"（绿色 M），
/// 但那种包里的文件是别的启动器整包带进来的，可能从未上传到任何平台。
/// 规则：MultiMC 包不给来源，交给哈希识别；认不出来就显示 `?`。
/// </summary>
public class PackSourceResolverTests
{
    [Fact]
    public void CurseForge_pack_marks_everything_as_curseforge()
    {
        var info = Create("curseforge");
        Assert.Equal(UpdateSource.CurseForge, MainWindowViewModel.ResolvePackSource(info));
    }

    [Fact]
    public void Modrinth_pack_marks_everything_as_modrinth()
    {
        var info = Create("modrinth");
        Assert.Equal(UpdateSource.Modrinth, MainWindowViewModel.ResolvePackSource(info));
    }

    [Fact]
    public void MultiMc_pack_does_not_claim_any_platform()
    {
        var info = Create("multimc");

        Assert.True(info.IsMultiMc);
        Assert.Null(MainWindowViewModel.ResolvePackSource(info));
    }

    [Fact]
    public void Non_pack_instance_has_no_source()
    {
        Assert.Null(MainWindowViewModel.ResolvePackSource(null));
    }

    private static InstanceModpackInfo Create(string source)
        => new(source, ProjectId: "", Name: "pack", Version: "1.0", Missing: Array.Empty<BlockedModFile>());
}
