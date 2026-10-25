using NPMC.Launcher.Core.Mirrors;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 镜像 URL 重写规则。全部按 2026-10 的实测拓扑锚定。
/// </summary>
public class MirrorRouterTests
{
    private const string Base = "https://bmclapi2.bangbang93.com";

    private static MirrorRouter OfficialFirst() => new(new MirrorSettings { Source = DownloadSource.Official });
    private static MirrorRouter MirrorFirst() => new(new MirrorSettings { Source = DownloadSource.Bmclapi });

    // ------------------------------------------------------ 五条重写规则

    [Fact]
    public void Version_manifest_keeps_the_path_shape()
    {
        var url = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";
        Assert.Equal($"{Base}/mc/game/version_manifest_v2.json",
            OfficialFirst().TryBuildMirrorUrl(url, ResourceKind.VersionManifest));
    }

    [Fact]
    public void Piston_meta_keeps_the_path_shape()
    {
        var url = "https://piston-meta.mojang.com/v1/packages/38d412af3b247e485c7f7e50ac25febc4e79ab1d/1.20.1.json";
        Assert.Equal($"{Base}/v1/packages/38d412af3b247e485c7f7e50ac25febc4e79ab1d/1.20.1.json",
            OfficialFirst().TryBuildMirrorUrl(url, ResourceKind.VersionMetadata));
    }

    [Fact]
    public void Piston_data_keeps_the_path_shape()
    {
        var url = "https://piston-data.mojang.com/v1/objects/0c3ec587af28e5a785c0b4a7b8a30f9a8f78f838/client.jar";
        Assert.Equal($"{Base}/v1/objects/0c3ec587af28e5a785c0b4a7b8a30f9a8f78f838/client.jar",
            OfficialFirst().TryBuildMirrorUrl(url, ResourceKind.ClientJar));
    }

    [Fact]
    public void Libraries_get_the_maven_prefix()
    {
        var url = "https://libraries.minecraft.net/com/google/code/gson/gson/2.10/gson-2.10.jar";
        Assert.Equal($"{Base}/maven/com/google/code/gson/gson/2.10/gson-2.10.jar",
            OfficialFirst().TryBuildMirrorUrl(url, ResourceKind.Library));
    }

    [Fact]
    public void Asset_objects_get_the_assets_prefix()
    {
        const string hash = "b62ca8ec10d07e6bf5ac8dae0c8c1d2e6a1e3356";
        var url = $"https://resources.download.minecraft.net/{hash[..2]}/{hash}";
        Assert.Equal($"{Base}/assets/{hash[..2]}/{hash}",
            OfficialFirst().TryBuildMirrorUrl(url, ResourceKind.AssetObject));
    }

    // -------------------------------------------------- 不可镜像的两种情况

    [Fact]
    public void Logging_config_is_never_mirrored()
    {
        // 实测：这个文件在 BMCLAPI 的 /v1/objects/ 下返回 404
        var url = "https://piston-data.mojang.com/v1/objects/bd65e7d2e3c237be76cfbef4c2405033d7f91521/client-1.12.xml";

        Assert.Null(OfficialFirst().TryBuildMirrorUrl(url, ResourceKind.LoggingConfig));
        Assert.False(MirrorRouter.IsMirrorable(ResourceKind.LoggingConfig));
        Assert.True(MirrorRouter.IsMirrorable(ResourceKind.ClientJar));
    }

    [Fact]
    public void Unknown_host_is_not_mirrored()
    {
        var url = "https://api.modrinth.com/v2/project/sodium";
        Assert.Null(OfficialFirst().TryBuildMirrorUrl(url, ResourceKind.Library));
    }

    [Fact]
    public void Custom_base_url_is_honoured_and_trailing_slash_trimmed()
    {
        var router = new MirrorRouter(new MirrorSettings { BmclapiBaseUrl = "https://mirror.example.com/bmclapi/" });
        Assert.Equal("https://mirror.example.com/bmclapi/assets/ab/abcdef",
            router.TryBuildMirrorUrl("https://resources.download.minecraft.net/ab/abcdef", ResourceKind.AssetObject));
    }

    // ------------------------------------------------------ 排序与回退语义

    [Fact]
    public void Official_first_lists_official_then_mirror()
    {
        var targets = OfficialFirst().Resolve("https://libraries.minecraft.net/a/b/c.jar", ResourceKind.Library);

        Assert.Equal(2, targets.Count);
        Assert.Equal(DownloadSource.Official, targets[0].Source);
        Assert.False(targets[0].IsMirrored);
        Assert.Equal(DownloadSource.Bmclapi, targets[1].Source);
        Assert.True(targets[1].IsMirrored);
    }

    [Fact]
    public void Mirror_first_lists_mirror_then_official()
    {
        var targets = MirrorFirst().Resolve("https://libraries.minecraft.net/a/b/c.jar", ResourceKind.Library);

        Assert.Equal(2, targets.Count);
        Assert.Equal(DownloadSource.Bmclapi, targets[0].Source);
        Assert.Equal(DownloadSource.Official, targets[1].Source);
    }

    [Fact]
    public void Mirror_first_without_fallback_is_mirror_only()
    {
        var router = new MirrorRouter(new MirrorSettings
        {
            Source = DownloadSource.Bmclapi,
            AllowFallback = false,
        });

        var targets = router.Resolve("https://libraries.minecraft.net/a/b/c.jar", ResourceKind.Library);

        Assert.Single(targets);
        Assert.Equal(DownloadSource.Bmclapi, targets[0].Source);
    }

    [Fact]
    public void Logging_config_falls_back_to_official_even_without_fallback()
    {
        // 首选镜像且禁止回退时，这个文件也必须给官方地址，否则永远拿不到
        var router = new MirrorRouter(new MirrorSettings
        {
            Source = DownloadSource.Bmclapi,
            AllowFallback = false,
        });

        var targets = router.Resolve(
            "https://piston-data.mojang.com/v1/objects/xxx/client-1.12.xml", ResourceKind.LoggingConfig);

        Assert.Single(targets);
        Assert.Equal(DownloadSource.Official, targets[0].Source);
    }

    [Fact]
    public void ResolvePrimary_returns_the_first_candidate()
    {
        var primary = MirrorFirst().ResolvePrimary("https://libraries.minecraft.net/a/b/c.jar", ResourceKind.Library);
        Assert.NotNull(primary);
        Assert.StartsWith(Base, primary!);
    }

    [Fact]
    public void Blank_url_resolves_to_empty()
        => Assert.Empty(OfficialFirst().Resolve("   ", ResourceKind.Library));
}
