namespace NPMC.Launcher.Core.Models;

/// <summary>
/// Maven 坐标 → 仓库内路径。
///
/// Mojang 的版本 JSON 里，每个库都自带 <c>downloads.artifact.path</c>，不需要自己算；
/// 但 **Fabric 的 profile JSON 只有 Maven 坐标**（<c>name</c> 字段）+ 一个仓库根地址，
/// 所以必须能自己推出路径，否则加载器的库根本下不下来。
///
/// 坐标格式：<c>group:artifact:version[:classifier][@extension]</c>
///   net.fabricmc:intermediary:1.20.1
///     → net/fabricmc/intermediary/1.20.1/intermediary-1.20.1.jar
///   org.lwjgl:lwjgl:3.3.1:natives-windows@zip
///     → org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.zip
/// </summary>
public static class MavenPath
{
    public static string FromCoordinate(string coordinate)
    {
        if (string.IsNullOrWhiteSpace(coordinate))
            throw new ArgumentException("Maven 坐标不能为空", nameof(coordinate));

        var text = coordinate.Trim();

        // 扩展名可以跟在最后一段后面：...:1.0@zip
        var extension = "jar";
        var atIndex = text.LastIndexOf('@');
        if (atIndex >= 0)
        {
            extension = text[(atIndex + 1)..];
            text = text[..atIndex];
        }

        var parts = text.Split(':');
        if (parts.Length < 3)
            throw new ArgumentException($"Maven 坐标至少需要 group:artifact:version，实际是「{coordinate}」", nameof(coordinate));

        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? parts[3] : null;

        var fileName = classifier is null or ""
            ? $"{artifact}-{version}.{extension}"
            : $"{artifact}-{version}-{classifier}.{extension}";

        return $"{group.Replace('.', '/')}/{artifact}/{version}/{fileName}";
    }
}
