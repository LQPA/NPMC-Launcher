using System.Windows.Input;
using NPMC.Launcher.Core.Content;

namespace NPMC.Launcher.App.ViewModels;

/// <summary>
/// 内容页签（模组 / 资源包 / 光影）的两种视图：本地列表 ⇄ 下载。
///
/// 三种类型共用同一套界面模型：
///   - 列表模式：本地文件 + 本地筛选 + 「下载新 X」入口；
///   - 下载模式：搜索面板接管（搜索、来源、排序、结果详情、依赖计划、翻页），
///     本地列表与底部操作行让位。
///
/// 以前只有模组有这套，资源包/光影只有一个常驻搜索框，和列表挤在同一屏里 ——
/// 看起来就像“下载界面消失了”。
///
/// 数据包不在其中：它按世界存放、要先选世界，只保留本地管理
/// （见 <see cref="ContentKindExtensions.SupportsDownload"/>）。
/// </summary>
public sealed partial class MainWindowViewModel
{
    private bool _isContentDownloadMode;
    private string _contentFilterQuery = "";

    /// <summary>当前页签是否处于「下载新 X」模式。</summary>
    public bool IsContentDownloadMode
    {
        get => _isContentDownloadMode;
        private set
        {
            if (!SetProperty(ref _isContentDownloadMode, value)) return;
            RaiseContentViewChanged();
        }
    }

    /// <summary>列表上方搜索框：实时过滤当前内容列表（本地，不联网）。</summary>
    public string ContentFilterQuery
    {
        get => _contentFilterQuery;
        set
        {
            if (!SetProperty(ref _contentFilterQuery, value)) return;
            _ = RefreshContentAsync();
        }
    }

    public ICommand OpenContentDownloadCommand { get; private set; }
    public ICommand CloseContentDownloadCommand { get; private set; }

    /// <summary>本地内容列表（下载模式下让位给搜索结果）。</summary>
    public bool ShowContentRows => ShowContentList && !_isContentDownloadMode;

    /// <summary>搜索面板：只在下载模式里出现。</summary>
    public bool IsSearchPanelVisible => ShowContentList && !IsDataPackTab && _isContentDownloadMode;

    /// <summary>列表上方的「筛选 + 下载」那一行。</summary>
    public bool IsContentFilterVisible => ShowContentList && !IsDataPackTab && !_isContentDownloadMode;

    // ---- 随内容类型变化的文案 ----

    private string ContentKindName => SelectedTab.ContentKind?.DisplayName() ?? "内容";

    public string DownloadButtonText => $"下载{ContentKindName}";

    public string DownloadPanelTitle => $"下载新{ContentKindName}";

    public string BackToContentListText => $"← 返回{ContentKindName}列表";

    public string ContentFilterWatermark => $"筛选当前{ContentKindName}…";

    public string ContentDownloadTip => $"搜索 Modrinth / CurseForge 并安装新{ContentKindName}";

    private void InitializeContentViewCommands()
    {
        OpenContentDownloadCommand = new RelayCommand(OpenContentDownload, () => CanOpenContentDownload);
        CloseContentDownloadCommand = new RelayCommand(CloseContentDownload);
    }

    /// <summary>数据包没有下载模式（按世界存放），所以只在支持的页签上可用。</summary>
    private bool CanOpenContentDownload =>
        ShowContentList && !_isContentDownloadMode
        && (SelectedTab.ContentKind?.SupportsDownload() ?? false);

    /// <summary>
    /// 进入下载模式。留着上一次的结果：从列表回来再点「下载 X」时，
    /// 用户上一轮搜到的东西应该还在，而不是每次从空白重新搜。
    /// </summary>
    private void OpenContentDownload()
    {
        if (SelectedTab.ContentKind is not { } kind || !kind.SupportsDownload()) return;

        IsContentDownloadMode = true;
    }

    /// <summary>回到本地列表。搜索结果与待确认计划都保留，下次进来还在。</summary>
    private void CloseContentDownload()
    {
        IsContentDownloadMode = false;

        // 计划面板属于“正在确认安装”，退出下载模式后不该继续挂着
        IsPlanVisible = false;
        (ConfirmInstallCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RaiseContentViewChanged()
    {
        OnPropertyChanged(nameof(ShowContentRows));
        OnPropertyChanged(nameof(IsSearchPanelVisible));
        OnPropertyChanged(nameof(IsContentFilterVisible));
        OnPropertyChanged(nameof(IsContentEmptyOrNotList));
        RaisePaneVisibilityChanged();
        (OpenContentDownloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>页签切换后重算所有「跟着内容类型走」的文案。</summary>
    private void RaiseContentKindTextChanged()
    {
        OnPropertyChanged(nameof(DownloadButtonText));
        OnPropertyChanged(nameof(DownloadPanelTitle));
        OnPropertyChanged(nameof(BackToContentListText));
        OnPropertyChanged(nameof(ContentFilterWatermark));
        OnPropertyChanged(nameof(ContentDownloadTip));
    }
}
