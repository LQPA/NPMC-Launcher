using System.IO.Compression;
using System.Text;
using NPMC.Launcher.App.Diagnostics;
using NPMC.Launcher.App.Mods;
using NPMC.Launcher.App.ViewModels;
using Xunit;

namespace NPMC.Launcher.App.Tests;

/// <summary>
/// 诊断引擎的回归测试。
///
/// 用例里的日志文本取自 2026-11-08 13:45 的真实启动归档
/// （3 个模组的最小实例：iris_shader_folder + oculus，缺 Sodium / Embeddium）。
/// 那次崩溃的表层报错是 Mixin 的 Class version 警告，真实原因却是缺少
/// net.caffeinemc.mods.sodium.api.* 这个前置包 —— 所以断言必须钉住
/// 「结论指向 Sodium/Embeddium」而不是「Mixin 版本不兼容」。
/// </summary>
public class LaunchDiagnosticsTests
{
    private const string OculusLog = """
        [0811月2026 13:45:08.189] [main/INFO] [net.minecraftforge.fml.loading.moddiscovery.ModDiscoverer/SCAN]: Found mod file "oculus-mc1.20.1-1.8.0.jar" of type MOD with provider {mods folder locator}
        [0811月2026 13:45:08.754] [main/INFO] [mixin/]: Compatibility level JAVA_8 specified by mixins.oculus.json is lower than the default level supported by the current mixin service (JAVA_16).
        [0811月2026 13:45:08.913] [main/INFO] [mixin/]: Selecting config iris_shader_folder.mixins.json
        [0811月2026 13:45:09.404] [main/INFO] [mixin/]: Mixing texture.MixinAbstractTexture from mixins.oculus.json into net.minecraft.client.renderer.texture.AbstractTexture
        [0811月2026 13:45:09.407] [main/WARN] [mixin/]: mixins.oculus.json:texture.MixinAbstractTexture: Class version 61 required is higher than the class version supported by the current version of Mixin (JAVA_8 supports class version 52)
        [0811月2026 13:45:14.892] [Render thread/INFO] [mixin/]: Mixing MixinGlStateManager from mixins.oculus.json into com.mojang.blaze3d.systems.RenderSystem
        [0811月2026 13:45:14.892] [Render thread/INFO] [mixin/]: Mixing MixinRenderSystem from mixins.oculus.json into com.mojang.blaze3d.systems.RenderSystem
        [0811月2026 13:45:14.970] [Render thread/WARN] [mixin/]: Error loading class: net/caffeinemc/mods/sodium/api/memory/MemoryIntrinsics (java.lang.ClassNotFoundException: net.caffeinemc.mods.sodium.api.memory.MemoryIntrinsics)
        """;

    private const string OculusStderr = """
        Exception in thread "Render thread" java.lang.RuntimeException: java.lang.reflect.InvocationTargetException
        	at MC-BOOTSTRAP/cpw.mods.modlauncher@10.0.9/cpw.mods.modlauncher.LaunchServiceHandlerDecorator.launch(LaunchServiceHandlerDecorator.java:32)
        Caused by: java.lang.NoClassDefFoundError: Could not initialize class com.mojang.blaze3d.systems.RenderSystem
        	at TRANSFORMER/minecraft@1.20.1/net.minecraft.SystemReport.m_143522_(SystemReport.java:66)
        Caused by: java.lang.ExceptionInInitializerError: Exception org.spongepowered.asm.mixin.transformer.throwables.MixinTransformerError: An unexpected critical error was encountered [in thread "Render thread"]
        	at MC-BOOTSTRAP/org.spongepowered.mixin/org.spongepowered.asm.mixin.transformer.MixinProcessor.applyMixins(MixinProcessor.java:392)
        """;

    /// <summary>只有 Mixin 兼容级别的 WARN，没有任何缺失类 —— 此时才允许把兼容级别当结论。</summary>
    private const string MixinWarningOnlyLog = """
        [0811月2026 13:45:08.754] [main/INFO] [mixin/]: Compatibility level JAVA_8 specified by mixins.oculus.json is lower than the default level supported by the current mixin service (JAVA_16).
        [0811月2026 13:45:09.404] [main/INFO] [mixin/]: Mixing texture.MixinAbstractTexture from mixins.oculus.json into net.minecraft.client.renderer.texture.AbstractTexture
        [0811月2026 13:45:09.407] [main/WARN] [mixin/]: mixins.oculus.json:texture.MixinAbstractTexture: Class version 61 required is higher than the class version supported by the current version of Mixin (JAVA_8 supports class version 52)
        """;

    /// <summary>13:15 那次真实崩溃报告：加载器只说了最表层的一句 —— iris_shader_folder 需要 oculus。</summary>
    private const string CrashReportIrisNeedsOculus = """
        ---- Minecraft Crash Report ----
        Description: Mod loading error has occurred

        -- MOD iris_shader_folder --
        Details:
        	Mod File: /E:/aaaa/minecraft/instances/test/mods/iris_shader_folder-1.4.3-forge.jar
        	Failure message: Mod iris_shader_folder requires oculus 0.0 or above
        		Currently, oculus is not installed
        	Mod Version: 1.4.3-forge
        """;

    private static ModIndex IndexWithOculusAndIris() => new(
        new[]
        {
            new ModMetadata(
                "oculus",
                "Oculus",
                "1.8.0",
                Path.Combine("C:", "game", "mods", "oculus-mc1.20.1-1.8.0.jar"),
                new[]
                {
                    new ModDependency("minecraft", "[1.20.1]", ModDependencyKind.Required),
                    // Oculus 把 Embeddium 声明成可选：所以「依赖闭包检查」查不出它缺失，
                    // 这正是必须去日志里找 MissingClass 的原因。
                    new ModDependency("embeddium", "[0.3.1,)", ModDependencyKind.Optional),
                }),
            new ModMetadata(
                "iris_shader_folder",
                "Iris Shader Folder",
                "1.4.3",
                Path.Combine("C:", "game", "mods", "iris_shader_folder-1.4.3-forge.jar"),
                new[] { new ModDependency("oculus", "[0.0,)", ModDependencyKind.Required) }),
        });

    [Fact]
    public void MissingSodiumApi_BlamesTheRealPrerequisite_NotTheMixinVersionWarning()
    {
        var report = LaunchDiagnostics.Analyze(
            "test", 1, string.Empty, OculusLog, OculusStderr, IndexWithOculusAndIris());

        Assert.Contains("缺少前置", report.Title);
        Assert.Contains("Sodium", report.Title);
        Assert.Contains("Embeddium", report.Title);
        Assert.DoesNotContain("版本不兼容", report.Title);

        // Mixin 的 Class version 警告必须降级为「次生症状」，不能当病因
        Assert.True(report.HasSymptoms);
        Assert.Contains(report.Symptoms, s => s.Title.Contains("Class version"));

        // 建议要落到具体模组上
        Assert.Contains("oculus-mc1.20.1-1.8.0.jar", report.InvolvedModsText);
    }

    [Fact]
    public void JarPresentButReportedNotInstalled_IsExplainedAsLoadFailure()
    {
        // oculus 的 jar 明明在 mods 里，加载器却说它没装 —— 结论必须是「它自己加载失败」
        var report = LaunchDiagnostics.Analyze(
            "test", 1, CrashReportIrisNeedsOculus, string.Empty, null, IndexWithOculusAndIris());

        Assert.Contains("不是没装", report.Title);
        Assert.Contains("oculus", report.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("iris_shader_folder", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrulyMissingDependency_TellsToInstallIt()
    {
        // mods 里确实没有 oculus 时，才说「缺少前置」
        var irisOnly = new ModIndex(new[]
        {
            new ModMetadata(
                "iris_shader_folder",
                "Iris Shader Folder",
                "1.4.3",
                Path.Combine("C:", "game", "mods", "iris_shader_folder-1.4.3-forge.jar"),
                new[] { new ModDependency("oculus", "[0.0,)", ModDependencyKind.Required) }),
        });

        var report = LaunchDiagnostics.Analyze(
            "test", 1, CrashReportIrisNeedsOculus, string.Empty, null, irisOnly);

        Assert.Contains("缺少前置", report.Title);
        Assert.Contains("oculus", report.Summary);
        Assert.Contains("安装", report.Suggestion);
    }

    [Fact]
    public void MixinCompatibilityLevelAlone_StaysAWarning()
    {
        var report = LaunchDiagnostics.Analyze(
            "test", 1, string.Empty, MixinWarningOnlyLog, null, IndexWithOculusAndIris());

        Assert.Equal(LaunchDiagnosisSeverity.Warning, report.Severity);
        Assert.Contains("兼容级别", report.Title);
        Assert.Contains("oculus", report.InvolvedModsText);
    }

    [Fact]
    public void OptionalDependencyIsNotTreatedAsHardRequirement()
    {
        // embeddium 是可选依赖：光看元数据不能判定 Oculus 装不上
        var index = IndexWithOculusAndIris();
        var oculus = index.Find("oculus");
        Assert.NotNull(oculus);
        Assert.All(
            oculus!.Dependencies.Where(d => d.ModId == "embeddium"),
            d => Assert.Equal(ModDependencyKind.Optional, d.Kind));
    }

    [Fact]
    public void ModMetadataIndex_ParsesModsToml_DependenciesAndKinds()
    {
        var directory = CreateTempModsDirectory();
        try
        {
            WriteJar(Path.Combine(directory, "oculus.jar"), "META-INF/mods.toml", """
                modLoader="javafml"
                loaderVersion="[47,)"
                license="LGPL-3.0"

                [[mods]]
                modId="oculus"
                version="1.8.0"
                displayName="Oculus"

                [[dependencies.oculus]]
                modId="minecraft"
                mandatory=true
                versionRange="[1.20.1]"

                [[dependencies.oculus]]
                modId="embeddium"
                mandatory=false
                versionRange="[0.3.1,)"
                """);

            var index = ModMetadataIndex.Build(directory);

            Assert.True(index.Contains("oculus"));
            var oculus = index.Find("oculus");
            Assert.NotNull(oculus);
            Assert.Equal("Oculus", oculus!.DisplayName);
            Assert.Equal("1.8.0", oculus.Version);
            Assert.Contains(oculus.Dependencies, d => d.ModId == "minecraft" && d.IsRequired);
            Assert.Contains(
                oculus.Dependencies,
                d => d.ModId == "embeddium" && d.Kind == ModDependencyKind.Optional && d.VersionRange == "[0.3.1,)");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void ModMetadataIndex_ParsesFabricManifest_Depends()
    {
        var directory = CreateTempModsDirectory();
        try
        {
            WriteJar(Path.Combine(directory, "sodium-fabric.jar"), "fabric.mod.json", """
                {
                  "schemaVersion": 1,
                  "id": "sodium",
                  "version": "0.5.11",
                  "name": "Sodium",
                  "depends": { "minecraft": ">=1.20.1", "fabricloader": ">=0.15" }
                }
                """);

            var index = ModMetadataIndex.Build(directory);

            var sodium = index.Find("sodium");
            Assert.NotNull(sodium);
            Assert.Equal("Sodium", sodium!.DisplayName);
            Assert.Contains(sodium.Dependencies, d => d.ModId == "fabricloader" && d.IsRequired);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void ModIndexTreatsBuiltInIdsAsPresent()
    {
        var index = IndexWithOculusAndIris();
        Assert.True(index.Contains("minecraft"));
        Assert.True(index.Contains("forge"));
        Assert.False(index.Contains("embeddium"));
    }

    /// <summary>
    /// 联机模块开发暂停：入口保留在导航栏里，但置灰、点了没反应。
    /// 这条断言把「暂不开放」钉在代码里 —— 要放开时得同时改这里，
    /// 免得顺手一改就悄悄上线。
    /// </summary>
    [Fact]
    public void P2PEntranceStaysDisabledWhileDevelopmentIsPaused()
    {
        Assert.False(MainWindowViewModel.P2PFeatureEnabled);
    }

    private static string CreateTempModsDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "nmpc-modindex-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteJar(string jarPath, string entryName, string content)
    {
        using var archive = ZipFile.Open(jarPath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // 临时目录清不掉不影响测试结论
        }
    }
}
