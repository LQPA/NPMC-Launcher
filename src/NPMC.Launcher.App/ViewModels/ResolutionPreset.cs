namespace NPMC.Launcher.App.ViewModels;

/// <summary>窗口分辨率预设，用于下拉快速填入。</summary>
public sealed record ResolutionPreset(string Label, int Width, int Height)
{
    public override string ToString() => Label;
}
