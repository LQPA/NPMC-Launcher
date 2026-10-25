namespace NPMC.Launcher.App.ViewModels;

/// <summary>实例可选的 Java。Path 为空表示"交给版本元数据决定"。</summary>
public sealed class JavaChoice
{
    public string Label { get; init; } = "";

    public string? Path { get; init; }

    /// <summary>启动器自带的运行时组件名（用于随实例记录）。</summary>
    public string? Component { get; init; }

    /// <summary>该 Java 的主版本（21、17 等）；读不出来时为 0。</summary>
    public int Major { get; init; }

    public override string ToString() => Label;
}
