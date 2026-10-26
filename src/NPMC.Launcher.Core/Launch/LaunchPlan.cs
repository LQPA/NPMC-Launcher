using System.Globalization;
using NPMC.Launcher.Core.Models;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Core.Rules;

namespace NPMC.Launcher.Core.Launch;

/// <summary>一次启动所需的账号与外观参数。</summary>
public sealed record LaunchContext(
    string PlayerName,
    string Uuid,
    string AccessToken,
    string ClientId,
    string Xuid,
    string UserType,
    string LauncherName,
    string LauncherVersion,
    int Width,
    int Height)
{
    /// <summary>
    /// 离线用的上下文。accessToken 填占位值即可——单人游戏不需要真实令牌。
    /// </summary>
    /// <remarks>
    /// user_type 用 legacy：离线账号不是微软账号，标成 legacy 才不会让游戏
    /// 按 MSA 账号去处理皮肤、聊天等依赖在线身份的功能。
    ///
    /// 注意：即便如此，1.20.1 启动时仍会请求
    /// <c>api.minecraftservices.com/player/attributes</c> 并拿到 401，
    /// 日志里会出现 YggdrasilUserApiService.fetchProperties 的异常。
    /// 这是**无害的**：实测游戏继续初始化并进入主菜单
    /// （有 "Setting user"、Sound engine started、贴图集创建完毕）。
    /// 看到这条异常不要误判为启动失败。
    /// </remarks>
    public static LaunchContext OfflineProbe(string playerName = "Probe") =>
        new(playerName,
            Uuid: "00000000000000000000000000000000",
            AccessToken: "0",
            ClientId: "0",
            Xuid: "0",
            UserType: "legacy",
            LauncherName: "NPMC-Launcher",
            LauncherVersion: "0.1.0",
            Width: 1280,
            Height: 720);
}

/// <summary>
/// 最终要交给 java.exe 的命令。JvmArguments 已完成占位符替换，
/// MainClass 与 GameArguments 分开保存便于调试打印。
/// </summary>
public sealed record LaunchPlan(
    string JavaPath,
    string WorkingDirectory,
    string MainClass,
    IReadOnlyList<string> JvmArguments,
    IReadOnlyList<string> GameArguments)
{
    /// <summary>传给 ProcessStartInfo.ArgumentList 的完整参数序列（不含 java 自身路径）。</summary>
    public IReadOnlyList<string> ToProcessArguments()
    {
        var list = new List<string>(JvmArguments.Count + GameArguments.Count + 1);
        list.AddRange(JvmArguments);
        list.Add(MainClass);
        list.AddRange(GameArguments);
        return list;
    }

    /// <summary>人类可读的命令行，用于 last-command.txt 与排错。</summary>
    public string ToDisplayString()
    {
        var parts = new List<string> { Quote(JavaPath) };
        parts.AddRange(ToProcessArguments().Select(Quote));
        return string.Join(' ', parts);
    }

    private static string Quote(string value)
    {
        if (value.Length == 0) return "\"\"";
        return value.Any(char.IsWhiteSpace) || value.Contains(';')
            ? "\"" + value + "\""
            : value;
    }
}

public static class ClasspathBuilder
{
    /// <summary>
    /// 组装 classpath。
    /// client jar 放在**最后**（与官方启动器一致）：Fabric 依赖这个顺序定位游戏 jar。
    /// </summary>
    public static IReadOnlyList<string> Build(
        LauncherPaths paths,
        IEnumerable<Library> effectiveLibraries,
        string clientJarPath)
    {
        var list = new List<string>();
        foreach (var library in effectiveLibraries)
        {
            var artifact = library.Downloads?.Artifact;

            if (artifact is { Path.Length: > 0 })
            {
                list.Add(paths.LibraryFile(artifact.Path));
                continue;
            }

            // Fabric / Quilt 的库来自各自的 meta，只有 name（group:artifact:version）
            // 与 url，**没有 downloads 段**。只认 downloads 的话这些库会被整包跳过，
            // 表现就是"找不到或无法加载主类 net.fabricmc.loader.impl.launch.knot.KnotClient"。
            // 这类库的落盘位置由 Maven 坐标决定，安装器也是按坐标放的。
            if (library.Name.Split(':', StringSplitOptions.RemoveEmptyEntries).Length >= 3)
                list.Add(paths.LibraryFile(MavenPath.FromCoordinate(library.Name)));
        }


        // client jar 放**最后**：与官方启动器一致，Fabric 依赖这个顺序定位游戏 jar。
        list.Add(clientJarPath);
        return list;
    }
}

/// <summary>把 arguments 模板展开成实际参数列表。</summary>
public sealed class ArgumentBuilder
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public ArgumentBuilder Set(string placeholder, string value)
    {
        _values[placeholder] = value;
        return this;
    }

    public string Expand(string value)
    {
        var result = value;
        foreach (var (key, replacement) in _values)
            result = result.Replace(key, replacement, StringComparison.Ordinal);
        return result;
    }

    public IReadOnlyList<string> Build(IReadOnlyList<ArgumentEntry> entries, OsInfo os, FeatureSet features)
    {
        var result = new List<string>();
        foreach (var entry in entries)
        {
            if (!RuleEvaluator.IsAllowed(entry.Rules, os, features)) continue;
            foreach (var value in entry.Values)
                result.Add(Expand(value));
        }
        return result;
    }

    public static ArgumentBuilder CreateDefault(
        string gameDirectory,
        string assetsDirectory,
        string nativesDirectory,
        string librariesDirectory,
        string versionId,
        VersionJson version,
        LaunchContext context,
        IReadOnlyList<string> classpath)
    {
        var builder = new ArgumentBuilder();

        builder.Set("${auth_player_name}", context.PlayerName);
        builder.Set("${version_name}", versionId);
        builder.Set("${game_directory}", gameDirectory);
        builder.Set("${assets_root}", assetsDirectory);
        // Forge 1.17+ 的版本 JSON 用这两个占位符拼 -p 模块路径与 ignoreList（PCL2/HMCL 同样要替换）
        builder.Set("${library_directory}", librariesDirectory);
        builder.Set("${classpath_separator}", Path.PathSeparator.ToString());
        builder.Set("${assets_index_name}", version.AssetIndex?.Id ?? version.Assets ?? versionId);
        builder.Set("${auth_uuid}", context.Uuid);
        builder.Set("${auth_access_token}", context.AccessToken);
        builder.Set("${clientid}", context.ClientId);
        builder.Set("${auth_xuid}", context.Xuid);
        builder.Set("${user_type}", context.UserType);
        builder.Set("${version_type}", version.Type);
        builder.Set("${natives_directory}", nativesDirectory);
        builder.Set("${launcher_name}", context.LauncherName);
        builder.Set("${launcher_version}", context.LauncherVersion);
        builder.Set("${classpath}", string.Join(';', classpath));
        // 旧版（1.13 之前）专用占位符。
        // ${user_properties} 尤其关键：它必须是合法 JSON（"{}"），
        // 否则游戏会拿字面量去解析，报 "Expected BEGIN_OBJECT but was STRING"。
        builder.Set("${user_properties}", "{}");
        builder.Set("${auth_session}", context.AccessToken);
        builder.Set("${profile_name}", context.PlayerName);
        builder.Set("${game_assets}", assetsDirectory);

        builder.Set("${resolution_width}", context.Width.ToString(CultureInfo.InvariantCulture));
        builder.Set("${resolution_height}", context.Height.ToString(CultureInfo.InvariantCulture));

        // quickPlay 系列：只有对应 feature 打开时才会被 arguments 引用，这里填空避免留下未替换的占位符。
        builder.Set("${quickPlayPath}", string.Empty);
        builder.Set("${quickPlaySingleplayer}", string.Empty);
        builder.Set("${quickPlayMultiplayer}", string.Empty);
        builder.Set("${quickPlayRealms}", string.Empty);

        return builder;
    }
}

public static class LaunchPlanFactory
{
    /// <summary>
    /// 由一个版本的元数据生成完整启动计划。
    /// 实测基准（1.20.1 / Windows x64 / 自定义分辨率 1280x720）：
    ///   生效 libraries = 64，classpath 条目 = 65，JVM 参数 = 12，游戏参数 = 26。
    /// </summary>
    public static LaunchPlan Create(
        LauncherPaths paths,
        string versionId,
        VersionJson version,
        LaunchContext context,
        OsInfo os,
        int memoryMb = 4096,
        bool hasCustomResolution = true,
        string? logConfigFile = null,
        string? gameDirectory = null)
    {
        // 1.13 之前只有 minecraftArguments（一整串空格分隔的参数），没有 arguments 结构
        var isLegacyArguments = version.Arguments is null;

        if (isLegacyArguments && string.IsNullOrWhiteSpace(version.MinecraftArguments))
            throw new NotSupportedException(
                $"版本 {versionId} 既没有 arguments 也没有 minecraftArguments，无法组装启动参数。");

        var features = FeatureSet.ForLaunch(hasCustomResolution);

        var effectiveLibraries = version.Libraries
            .Where(lib => RuleEvaluator.IsAllowed(lib.Rules, os, features))
            .ToList();

        var clientJar = paths.ClientJar(versionId);
        var classpath = ClasspathBuilder.Build(paths, effectiveLibraries, clientJar);
        var natives = paths.NativesDir(versionId);
        var gameDir = gameDirectory ?? paths.Root;

        var builder = ArgumentBuilder.CreateDefault(
            gameDir, paths.Assets, natives, paths.Libraries, versionId, version, context, classpath);

        var jvm = new List<string>
        {
            $"-Xms{Math.Max(512, memoryMb / 2)}M",
            $"-Xmx{memoryMb}M",
        };

        if (!string.IsNullOrEmpty(logConfigFile))
            jvm.Add($"-Dlog4j.configurationFile={logConfigFile}");

        jvm.AddRange(builder.Build(
            version.Arguments?.Jvm ?? (IReadOnlyList<ArgumentEntry>)Array.Empty<ArgumentEntry>(), os, features));

        IReadOnlyList<string> game;

        if (isLegacyArguments)
        {
            // 旧版把 -Djava.library.path 与 -cp 也省略了，需要自己补
            jvm.Add($"-Djava.library.path={natives}");
            jvm.Add("-cp");
            jvm.Add(string.Join(';', classpath));

            game = ExpandLegacyArguments(builder, version.MinecraftArguments!);
        }
        else
        {
            game = builder.Build(version.Arguments!.Game, os, features);
        }

        var javaPath = version.JavaVersion is { Component.Length: > 0 } jv
            ? paths.JavaExe(jv.Component)
            : paths.JavaExe("java-runtime-gamma");

        return new LaunchPlan(javaPath, gameDir, version.MainClass, jvm, game);
    }

    /// <summary>
    /// 展开旧版 minecraftArguments。
    ///
    /// 先按空格切分**再**替换：占位符本身不含空格，而替换进去的值（例如游戏目录）
    /// 可能含空格，反过来的话参数会被切碎。
    /// </summary>
    public static IReadOnlyList<string> ExpandLegacyArguments(ArgumentBuilder builder, string minecraftArguments)
        => minecraftArguments
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(builder.Expand)
            .ToList();

    /// <summary>检查是否还残留 ${...} 占位符——有残留说明模板替换有漏项。</summary>
    public static IReadOnlyList<string> FindUnresolvedPlaceholders(LaunchPlan plan)
    {
        var all = string.Join('\n', plan.JvmArguments.Concat(plan.GameArguments));
        return System.Text.RegularExpressions.Regex
            .Matches(all, @"\$\{[A-Za-z_]+\}")
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }
}
