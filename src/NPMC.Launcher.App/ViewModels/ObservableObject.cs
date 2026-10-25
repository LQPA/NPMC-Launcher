using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 极简 INotifyPropertyChanged 基类。
///
/// 为什么不用 CommunityToolkit.Mvvm：
///   本项目需要通知的属性只有十几个，手写 SetProperty 足够；
///   少一个第三方依赖，就少一个版本升级时的破坏性变更风险。
///   将来界面复杂到需要源生成器时再换，成本是一次性替换基类。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>值变化时通知并返回 true，便于 <c>if (!SetProperty(...)) return;</c>。</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
