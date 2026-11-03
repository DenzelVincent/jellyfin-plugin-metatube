namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 演员合集的纯计算：把「影片 → 演员」折算成「演员 → 影片」，再与库里现有合集算差集。
/// 这里不引用任何服务端类型，因此能脱离 Jellyfin 进程单测；Jellyfin 大版本改接口时
/// 也不用动这个文件（设计 5.3-8 把服务端调用全部关在扩展与任务里）。
/// </summary>
public static class ActorCollectionSync
{
    /// <summary>一条「某影片里的某演员」。Provider/ActorId 允许为空串——演员没刮到 pid 是常态。</summary>
    public readonly record struct ActorRef(Guid MovieId, string Name, string Provider, string ActorId);

    /// <summary>一位演员在本次扫描里的期望合集形态。</summary>
    public sealed class ActorGroup
    {
        public ActorGroup(string key, string displayName)
        {
            Key = key;
            DisplayName = displayName;
        }

        /// <summary>分组键：显示名 Trim + 小写。</summary>
        public string Key { get; }

        /// <summary>合集名，原样取演员显示名（设计 4.5：不加前后缀、不做模板）。</summary>
        public string DisplayName { get; }

        /// <summary>组内第一个非空 pid，只当合集封面来源，不参与分组。</summary>
        public string Provider { get; set; } = string.Empty;

        public string ActorId { get; set; } = string.Empty;

        public HashSet<Guid> MovieIds { get; } = new();
    }

    /// <summary>库里已有 BoxSet 的比对视图。</summary>
    public sealed class ExistingCollection
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Provider { get; set; } = string.Empty;

        public string ActorId { get; set; } = string.Empty;

        public HashSet<Guid> MemberIds { get; set; } = new();
    }

    public enum SyncAction
    {
        None,
        Create,
        Update
    }

    /// <summary>对一位演员的同步决定。</summary>
    public sealed class SyncPlan
    {
        public SyncAction Action { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Provider { get; init; } = string.Empty;

        public string ActorId { get; init; } = string.Empty;

        /// <summary>Create 时为 null，Update 时为命中的合集 id。</summary>
        public Guid? CollectionId { get; init; }

        public IReadOnlyList<Guid> AddItemIds { get; init; } = Array.Empty<Guid>();

        public IReadOnlyList<Guid> RemoveItemIds { get; init; } = Array.Empty<Guid>();

        /// <summary>同名命中但合集上没有 pid，需要补写，供 CollectionImageProvider 解析封面。</summary>
        public bool NeedsProviderId { get; init; }

        /// <summary>非空表示本轮跳过这位演员的原因（只由最小片数阈值门控产生）。</summary>
        public string SkipReason { get; init; } = string.Empty;
    }

    public static string GroupKey(string name)
    {
        return (name ?? string.Empty).Trim().ToLowerInvariant();
    }

    public static Dictionary<string, ActorGroup> Build(IEnumerable<ActorRef> refs)
    {
        var groups = new Dictionary<string, ActorGroup>();
        foreach (var r in refs)
        {
            var key = GroupKey(r.Name);
            // 演员替换表把演员置空时库里留下的就是空显示名，不能给它建合集。
            if (key.Length == 0)
                continue;

            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = new ActorGroup(key, r.Name.Trim());

            group.MovieIds.Add(r.MovieId);
            // 键是显示名而不是 pid：同一演员在库里完全可能一部分影片带 pid、一部分不带
            // （MovieProvider.SetActorImageUrl 只在演员有照片时才 SetPid），
            // 按 pid 分组会把这位演员裂成两个合集。
            if (group.ActorId.Length == 0 && !string.IsNullOrEmpty(r.Provider) && !string.IsNullOrEmpty(r.ActorId))
            {
                group.Provider = r.Provider;
                group.ActorId = r.ActorId;
            }
        }

        return groups;
    }

    /// <summary>
    /// 合集/演员的 MetaTube pid 串，格式与 MovieProvider 写入的一致（provider:id，id 做 URL 转义），
    /// 这样合集上的 pid 能用 ProviderIdsExtensions.GetPid 原样解回来。
    /// </summary>
    public static string EncodePid(string provider, string actorId)
    {
        return new ProviderId
        {
            Provider = provider,
            Id = Uri.EscapeDataString(actorId ?? string.Empty)
        }.ToString();
    }

    /// <summary>
    /// 现有合集索引。10.11 的 ICollectionManager 没有按名查找（设计第 3 节反射实测），
    /// 所以一次查询的结果在这里建两份索引，不能一位演员一查。
    /// </summary>
    public sealed class Index
    {
        private readonly Dictionary<string, ExistingCollection> _byPid = new(StringComparer.OrdinalIgnoreCase);

        // 键已经是 GroupKey 后的小写名，用默认序数比较即可。
        private readonly Dictionary<string, ExistingCollection> _byName = new();

        public Index(IEnumerable<ExistingCollection> collections)
        {
            foreach (var c in collections)
            {
                if (!string.IsNullOrEmpty(c.ActorId))
                    _byPid.TryAdd(EncodePid(c.Provider, c.ActorId), c);
                _byName.TryAdd(GroupKey(c.Name), c);
            }
        }

        /// <summary>先按 pid（尊重用户改名，只对齐成员不改名），再按同名（复用用户手建的合集并补写 pid）。</summary>
        public ExistingCollection Find(ActorGroup group)
        {
            if (!string.IsNullOrEmpty(group.ActorId) &&
                _byPid.TryGetValue(EncodePid(group.Provider, group.ActorId), out var byPid))
                return byPid;

            return _byName.TryGetValue(group.Key, out var byName) ? byName : null;
        }
    }

    /// <summary>
    /// 幂等判定：成员集合相等且 pid 不缺 → 不写库（设计 4.3）。
    /// 阈值只门控创建（设计 D12），跌破阈值不回收、不删除已有合集。
    /// </summary>
    public static SyncPlan Plan(ActorGroup group, ExistingCollection existing, ICollection<Guid> scopedMovieIds,
        int minMovieCount)
    {
        // 非法阈值按 1 处理；Warn 由调用方按配置值只记一次。
        var threshold = minMovieCount < 1 ? 1 : minMovieCount;

        if (existing is null)
        {
            if (group.MovieIds.Count < threshold)
                return new SyncPlan
                {
                    Action = SyncAction.None,
                    Name = group.DisplayName,
                    Provider = group.Provider,
                    ActorId = group.ActorId,
                    SkipReason = $"{group.DisplayName}: 影片数 {group.MovieIds.Count} 低于最小片数阈值 {threshold}"
                };

            return new SyncPlan
            {
                Action = SyncAction.Create,
                Name = group.DisplayName,
                Provider = group.Provider,
                ActorId = group.ActorId,
                // 排序让日志与差集稳定，重复运行不因顺序差异产生写入。
                AddItemIds = group.MovieIds.OrderBy(id => id).ToList()
            };
        }

        var add = group.MovieIds.Except(existing.MemberIds).OrderBy(id => id).ToList();
        // 只移除本次扫描范围内的影片：合集里手工加入的其它条目（别的库、别的来源）一律不动，
        // 否则一次同步就能把用户整理好的合集吃干净（设计 4.2-4）。
        var remove = existing.MemberIds.Intersect(scopedMovieIds).Except(group.MovieIds).OrderBy(id => id).ToList();
        var needsProviderId = string.IsNullOrEmpty(existing.ActorId) && !string.IsNullOrEmpty(group.ActorId);

        return new SyncPlan
        {
            Action = add.Count == 0 && remove.Count == 0 && !needsProviderId
                ? SyncAction.None
                : SyncAction.Update,
            Name = group.DisplayName,
            Provider = group.Provider,
            ActorId = group.ActorId,
            CollectionId = existing.Id,
            AddItemIds = add,
            RemoveItemIds = remove,
            NeedsProviderId = needsProviderId
        };
    }
}
