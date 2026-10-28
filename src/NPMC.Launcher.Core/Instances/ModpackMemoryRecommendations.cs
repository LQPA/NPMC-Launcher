namespace NPMC.Launcher.Core.Instances;

/// <summary>
/// 常见大型整合包的内存推荐值。
///
/// 只覆盖"作者/社区有明确推荐"的包；没命中的实例按模组数量估算。
/// 匹配时不区分大小写、忽略空格与标点，避免「All the Mods 10」/「ATM10」写法差异。
/// 数值是“能舒服游玩”的常见建议，不是硬性要求。
/// </summary>
public static class ModpackMemoryRecommendations
{
    private static readonly (string[] Aliases, int MemoryMb, string Label)[] Table =
    {
        (new[] { "allthemods10", "atm10" }, 8192, "ATM10"),
        (new[] { "allthemods9", "atm9" }, 8192, "ATM9"),
        (new[] { "allthemods8", "atm8" }, 8192, "ATM8"),
        (new[] { "allthemods7", "atm7" }, 7168, "ATM7"),
        (new[] { "allthemods6", "atm6" }, 7168, "ATM6"),
        (new[] { "allthemods5", "atm5" }, 6144, "ATM5"),
        (new[] { "gregtechnologiesnewhorizons", "gtnh" }, 8192, "GTNH"),
        (new[] { "vaulthunters" }, 8192, "Vault Hunters"),
        (new[] { "dawncraft" }, 6144, "DawnCraft"),
        (new[] { "rlcraft" }, 6144, "RLCraft"),
        (new[] { "betterminecraft" }, 6144, "Better Minecraft"),
        (new[] { "createaboveandbeyond", "aboveandbeyond" }, 6144, "Create: Above and Beyond"),
        (new[] { "skyfactory" }, 5120, "SkyFactory"),
        (new[] { "stoneblock" }, 5120, "StoneBlock"),
        (new[] { "prominence" }, 6144, "Prominence"),
        (new[] { "cobblemon" }, 4096, "Cobblemon"),
    };

    /// <summary>整合包名（或版本名）对应的推荐内存；没有命中返回 null。</summary>
    public static (int MemoryMb, string Label)? Find(string? modpackName)
    {
        if (string.IsNullOrWhiteSpace(modpackName)) return null;

        var normalized = Normalize(modpackName);
        foreach (var (aliases, memoryMb, label) in Table)
        {
            if (aliases.Any(alias => normalized.Contains(alias, StringComparison.Ordinal)))
                return (memoryMb, label);
        }

        return null;
    }

    private static string Normalize(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);

        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(ch);
        }

        return builder.ToString();
    }
}
