using System.Collections.ObjectModel;
using System.Windows.Input;
using NPMC.Launcher.Core.Instances;
using NPMC.Launcher.Core.Paths;
using NPMC.Launcher.Infrastructure.Storage;

namespace NPMC.Launcher.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly LauncherPaths _launcher;
    private InstanceListItem? _selectedInstance;
    private string _status = "正在读取实例…";
    private bool _isBusy;

    public MainWindowViewModel(LauncherPaths launcher)
    {
        _launcher = launcher;

        RefreshCommand = new AsyncRelayCommand(LoadAsync, onError: ex => Status = $"刷新失败：{ex.Message}");
        OpenGameFolderCommand = new RelayCommand(OpenGameFolder, () => SelectedInstance is not null);
        OpenModsFolderCommand = new RelayCommand(OpenModsFolder, () => SelectedInstance is not null);
    }

    // ---- 顶部信息 ----

    public string RootPath => _launcher.Root;

    public string RootHint => $"用 --root <目录> 或环境变量 {LauncherRoot.EnvironmentVariable} 指定";

    // ---- 实例列表 ----

    public ObservableCollection<InstanceListItem> Instances { get; } = new();

    public InstanceListItem? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!SetProperty(ref _selectedInstance, value)) return;

            OnPropertyChanged(nameof(HasSelection));
            (OpenGameFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenModsFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelection => SelectedInstance is not null;

    public bool IsEmpty => Instances.Count == 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand OpenGameFolderCommand { get; }
    public ICommand OpenModsFolderCommand { get; }

    // ---- 加载 ----

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var catalog = new InstanceCatalog(await new JsonInstanceStore(_launcher).LoadAsync());

            Instances.Clear();
            foreach (var instance in catalog.Instances)
                Instances.Add(new InstanceListItem(_launcher, instance));

            OnPropertyChanged(nameof(IsEmpty));

            SelectedInstance = Instances.FirstOrDefault();

            var problems = catalog.Validate();
            Status = Instances.Count == 0
                ? "还没有实例。可用 npmc instance create 创建（界面创建在后续阶段接入）。"
                : problems.Count > 0
                    ? $"共 {Instances.Count} 个实例，其中 {problems.Count} 项数据有问题"
                    : $"共 {Instances.Count} 个实例";
        }
        catch (Exception ex)
        {
            Instances.Clear();
            OnPropertyChanged(nameof(IsEmpty));
            SelectedInstance = null;
            Status = $"读取实例失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- 操作 ----

    private void OpenGameFolder()
    {
        if (SelectedInstance is null) return;
        OpenDirectory(SelectedInstance.GameDirectory);
    }

    private void OpenModsFolder()
    {
        if (SelectedInstance is null) return;
        OpenDirectory(SelectedInstance.ModsDirectory);
    }

    private void OpenDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Status = $"打开目录失败：{ex.Message}";
        }
    }
}
