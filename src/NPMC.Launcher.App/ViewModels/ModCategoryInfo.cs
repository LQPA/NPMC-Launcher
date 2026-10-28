namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// Modrinth 模组分类的中文显示名与说明。
///
/// 分类本身来自 Modrinth 官方 /tag/category（不自己发明），
/// 这里只做中文显示和"这个分类适合怎么用"的提示，避免用户对着
/// technology / utility 这种词不知道选哪个。
/// </summary>
public static class ModCategoryInfo
{
    private static readonly Dictionary<string, (string Display, string Description)> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["technology"] = ("科技", "机器、能源、自动化；想找细分可加选「存储」「运输」「装备」"),
            ["storage"] = ("存储", "箱子、抽屉、AE2/RS 类存储与检索；常和「科技」组合"),
            ["transportation"] = ("运输", "传送、载具、管道、物流；常和「科技」组合"),
            ["utility"] = ("实用", "便捷工具、信息显示、配方查看；和「科技」有重叠，找纯机器时避免选它"),
            ["equipment"] = ("装备 / 工具", "武器、护甲、工具；可和「科技」组合找科技装备"),
            ["adventure"] = ("冒险", "地牢、Boss、探索目标；可与「世界生成」组合"),
            ["worldgen"] = ("世界生成", "生物群系、结构、地形；常和「冒险」组合"),
            ["magic"] = ("魔法", "法术、祭坛、魔法体系"),
            ["decoration"] = ("装饰", "家具、建材、美化"),
            ["food"] = ("食物 / 农业", "作物、烹饪、饮食"),
            ["mobs"] = ("生物", "新怪物、动物、NPC"),
            ["optimization"] = ("优化 / 性能", "帧数、内存、启动优化；通常单独选"),
            ["library"] = ("库 / API", "其他模组的前置；一般不用来筛玩法"),
            ["economy"] = ("经济", "货币、商店、交易"),
            ["game-mechanics"] = ("机制", "改变原版规则（如难度、掉落、合成）"),
            ["cursed"] = ("整活", "奇怪或故意整活的内容"),
            ["social"] = ("社交", "聊天、表情、多人互动"),
            ["minigame"] = ("小游戏", "小游戏 / 地图玩法"),
        };

    /// <summary>常用分类排在前面，其余按名字排。</summary>
    private static readonly string[] PreferredOrder =
    {
        "technology", "storage", "transportation", "equipment", "utility",
        "magic", "adventure", "worldgen", "mobs", "food",
        "decoration", "optimization", "game-mechanics", "economy", "library",
    };

    public static (string Display, string Description) Describe(string slug)
    {
        if (Map.TryGetValue(slug, out var value)) return value;

        // 没翻译的分类：显示原 slug，说明里注明来自 Modrinth
        return (slug, $"Modrinth 官方分类：{slug}");
    }

    public static int Order(string slug)
    {
        var index = Array.IndexOf(PreferredOrder, slug.ToLowerInvariant());
        return index < 0 ? PreferredOrder.Length : index;
    }
}
