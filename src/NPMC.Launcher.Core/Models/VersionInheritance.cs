namespace NPMC.Launcher.Core.Models;

/// <summary>
/// 把"加载器 profile"与它的父版本合并成一个可启动的版本描述。
///
/// 为什么需要：Fabric（以及 Forge/NeoForge 的 profile）只写**差异** ——
/// 自己的 mainClass 与几个库，加上 <c>inheritsFrom: "1.20.1"</c>。
/// 它没有原版的几十个库、没有 assetIndex、目录里也没有 client.jar。
/// 不合并就直接启动，会缺库、缺资源、找不到 jar。
/// </summary>
public static class VersionInheritance
{
    /// <summary>
    /// 合并规则（与官方启动器一致）：
    ///   - 库：父版本在前，子版本里**同一个 group:artifact** 的条目替换父版本的同名项
    ///   - 参数：父在前、子在后
    ///   - 其余字段：子有值用子的，否则用父的
    /// </summary>
    public static VersionJson Merge(VersionJson child, VersionJson parent)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(parent);

        var libraries = new List<Library>(parent.Libraries);

        foreach (var library in child.Libraries)
        {
            var key = ArtifactKey(library.Name);
            var index = libraries.FindIndex(item =>
                string.Equals(ArtifactKey(item.Name), key, StringComparison.OrdinalIgnoreCase));

            if (index >= 0) libraries[index] = library;
            else libraries.Add(library);
        }

        return child with
        {
            Type = string.IsNullOrWhiteSpace(child.Type) ? parent.Type : child.Type,
            MainClass = string.IsNullOrWhiteSpace(child.MainClass) ? parent.MainClass : child.MainClass,
            Assets = child.Assets ?? parent.Assets,
            AssetIndex = child.AssetIndex ?? parent.AssetIndex,
            Downloads = child.Downloads ?? parent.Downloads,
            JavaVersion = child.JavaVersion ?? parent.JavaVersion,
            Logging = child.Logging ?? parent.Logging,
            MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
            Arguments = MergeArguments(parent.Arguments, child.Arguments),
            Libraries = libraries,

            // 合并完就不再有继承关系；根版本号由调用方按链尾确定
            InheritsFrom = null,
        };
    }

    /// <summary>"group:artifact:version[:classifier]" → "group:artifact"（版本不参与比较）。</summary>
    public static string ArtifactKey(string mavenCoordinate)
    {
        if (string.IsNullOrWhiteSpace(mavenCoordinate)) return "";

        var parts = mavenCoordinate.Split(':');
        return parts.Length >= 2 ? $"{parts[0]}:{parts[1]}" : mavenCoordinate;
    }

    private static VersionArguments? MergeArguments(VersionArguments? parent, VersionArguments? child)
    {
        if (parent is null && child is null) return null;

        var game = new List<ArgumentEntry>();
        var jvm = new List<ArgumentEntry>();

        if (parent is not null)
        {
            game.AddRange(parent.Game);
            jvm.AddRange(parent.Jvm);
        }

        if (child is not null)
        {
            game.AddRange(child.Game);
            jvm.AddRange(child.Jvm);
        }

        return new VersionArguments { Game = game, Jvm = jvm };
    }
}
