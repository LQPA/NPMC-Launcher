using NPMC.Launcher.Core.Modpacks;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

public class BlockedModFileTests
{
    [Fact]
    public void Resource_pack_urls_use_the_texture_packs_section()
    {
        var file = new BlockedModFile(
            1, 2, "Pack", "my-pack", "pack.zip", "sha1", "resourcepacks");

        Assert.Equal("https://www.curseforge.com/minecraft/texture-packs/my-pack", file.ProjectUrl);
        Assert.Equal("https://www.curseforge.com/minecraft/texture-packs/my-pack/files/2", file.PageUrl);
    }

    [Fact]
    public void Shader_urls_use_the_shader_packs_section()
    {
        var file = new BlockedModFile(
            1, 2, "Shader", "my-shader", "shader.zip", "sha1", "shaderpacks");

        Assert.Equal("https://www.curseforge.com/minecraft/shader-packs/my-shader", file.ProjectUrl);
    }

    [Fact]
    public void Mod_urls_keep_using_mc_mods()
    {
        var file = new BlockedModFile(
            1, 2, "Mod", "my-mod", "mod.jar", "sha1", "mods");

        Assert.Equal("https://www.curseforge.com/minecraft/mc-mods/my-mod", file.ProjectUrl);
        Assert.Equal("https://www.curseforge.com/minecraft/mc-mods/my-mod/files/2", file.PageUrl);
    }
}
