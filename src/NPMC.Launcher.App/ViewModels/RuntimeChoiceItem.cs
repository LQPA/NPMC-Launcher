using NPMC.Launcher.Infrastructure.Runtime;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 设置页「预下载 Java 运行时」下拉里的一项：一个 Mojang 运行时组件
/// （java-runtime-gamma / java-runtime-delta / ...）。
///
/// 组件名就是 &lt;root&gt;\runtime\&lt;组件名&gt; 的目录名，所以"是否已安装"
/// 等价于那个目录下的 bin\java.exe 是否存在。
/// </summary>
public sealed class RuntimeChoiceItem
{
    public RuntimeChoiceItem(RuntimeComponent component, bool isInstalled)
    {
        Component = component.Name;
        Version = component.Version;
        IsInstalled = isInstalled;
    }

    public string Component { get; }

    public string Version { get; }

    public bool IsInstalled { get; }

    /// <summary>下拉里显示的文案：组件名 + 版本 + 已安装标记。</summary>
    public string Label => IsInstalled
        ? $"{Component}  {Version}  已安装"
        : $"{Component}  {Version}";

    public string Detail => IsInstalled
        ? "本机已有这个运行时（对应目录下能找到 java.exe）"
        : "尚未安装";

    public override string ToString() => Label;
}
