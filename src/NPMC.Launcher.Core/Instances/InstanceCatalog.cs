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

    /// <summary>
    /// 实例列表：先按用户拖出来的 <see cref="LauncherInstance.SortIndex"/>，
    /// 再按名称。旧数据全是 0，因此仍然等价于"按名称排序"。
    /// </summary>
    public IReadOnlyList<LauncherInstance> Instances => _instances
        .OrderBy(i => i.SortIndex)
        .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
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

        // 已经拖过顺序的集合：新实例排到最后；没拖过就保持 0（继续按名称排）
        var maxSort = _instances.Count == 0 ? 0 : _instances.Max(i => i.SortIndex);
        var sortIndex = maxSort > 0 ? maxSort + 1 : 0;

        var instance = LauncherInstance.Create(
            id, name.Trim(), versionId.Trim(), isolation, settings, sortIndex);

        _instances.Add(instance);
        return instance;
    }

    /// <summary>
    /// 按给定 id 顺序重排，并写入 0..n-1 的 SortIndex（随后持久化 instances.json 的数组顺序）。
    /// 没列到的实例保持原有相对顺序排到最后，避免因为调用方漏传而丢实例。
    /// </summary>
    public void Reorder(IReadOnlyList<string> orderedIds)
    {
        var remaining = _instances.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<LauncherInstance>(_instances.Count);

        foreach (var id in orderedIds)
            if (remaining.Remove(id, out var instance)) ordered.Add(instance);

        ordered.AddRange(_instances.Where(i => remaining.ContainsKey(i.Id)));

        for (var index = 0; index < ordered.Count; index++)
            ordered[index] = ordered[index] with { SortIndex = index };

        _instances.Clear();
        _instances.AddRange(ordered);
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

    /// <summary>
    /// 同时改显示名与 Id（Id 就是实例目录名，界面改名后目录也要跟着变）。
    /// 目录移动由调用方在落盘前完成；这里只校验新 Id 没被别的实例占用。
    /// </summary>
    public LauncherInstance? Relocate(string id, string newName, string newId)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("实例名不能为空。", nameof(newName));
        if (!InstanceId.IsValid(newId)) throw new ArgumentException($"实例目录名非法：'{newId}'", nameof(newId));

        var index = _instances.FindIndex(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;

        if (_instances.Any(i => !string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(i.Id, newId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"已有实例使用目录名 '{newId}'");
        }

        _instances[index] = _instances[index] with { Id = newId, Name = newName.Trim() };
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
