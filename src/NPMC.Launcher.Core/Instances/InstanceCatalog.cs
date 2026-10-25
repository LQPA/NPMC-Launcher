namespace NPMC.Launcher.Core.Instances;

/// <summary>
/// 实例集合的纯逻辑操作，不做任何 I/O。
/// 持久化交给 <see cref="Abstractions.IInstanceStore"/>，目录创建交给 <see cref="InstanceLayout"/>。
/// </summary>
public sealed class InstanceCatalog
{
    private readonly List<LauncherInstance> _instances;

    public InstanceCatalog(IEnumerable<LauncherInstance>? instances = null)
        => _instances = (instances ?? Enumerable.Empty<LauncherInstance>()).ToList();

    /// <summary>按名称排序的实例列表。</summary>
    public IReadOnlyList<LauncherInstance> Instances => _instances
        .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public int Count => _instances.Count;

    public LauncherInstance? Find(string id) =>
        _instances.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

    public LauncherInstance Add(
        string name,
        string versionId,
        IsolationLevel isolation = IsolationLevel.Version,
        InstanceSettings? settings = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("实例名不能为空。", nameof(name));
        if (string.IsNullOrWhiteSpace(versionId)) throw new ArgumentException("必须指定游戏版本。", nameof(versionId));

        var id = InstanceId.MakeUnique(name, _instances.Select(i => i.Id));
        var instance = LauncherInstance.Create(id, name.Trim(), versionId.Trim(), isolation, settings);
        _instances.Add(instance);
        return instance;
    }

    public bool Remove(string id)
    {
        var instance = Find(id);
        return instance is not null && _instances.Remove(instance);
    }

    public LauncherInstance? Rename(string id, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("实例名不能为空。", nameof(newName));

        var index = _instances.FindIndex(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;

        _instances[index] = _instances[index] with { Name = newName.Trim() };
        return _instances[index];
    }

    public LauncherInstance? UpdateSettings(string id, InstanceSettings settings)
    {
        var index = _instances.FindIndex(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;

        _instances[index] = _instances[index] with { Settings = settings };
        return _instances[index];
    }

    public LauncherInstance? SetIsolation(string id, IsolationLevel isolation)
    {
        var index = _instances.FindIndex(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;

        _instances[index] = _instances[index] with { Isolation = isolation };
        return _instances[index];
    }

    /// <summary>返回问题描述；空列表表示数据健康。</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var instance in _instances)
        {
            if (!InstanceId.IsValid(instance.Id))
                problems.Add($"实例 Id 非法：'{instance.Id}'");

            if (string.IsNullOrWhiteSpace(instance.Name))
                problems.Add($"实例 {instance.Id} 缺少名称");

            if (string.IsNullOrWhiteSpace(instance.VersionId))
                problems.Add($"实例 {instance.Id} 缺少游戏版本");

            if (!seen.Add(instance.Id))
                problems.Add($"实例 Id 重复：'{instance.Id}'");
        }

        return problems;
    }
}
