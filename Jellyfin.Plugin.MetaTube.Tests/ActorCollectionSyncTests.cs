using Jellyfin.Plugin.MetaTube.Helpers;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

/// <summary>
/// 演员合集的离线防线：分组、匹配与差集。服务端调用只能实机验（见 B8），
/// 这 20 条必须在没有 Jellyfin 进程的环境下就把逻辑卡死。
/// </summary>
public class ActorCollectionSyncTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid C = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid D = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static ActorCollectionSync.ActorRef Ref(Guid movieId, string name, string provider = "",
        string actorId = "")
    {
        return new(movieId, name, provider, actorId);
    }

    private static ActorCollectionSync.ActorGroup Group(string displayName, string provider, string actorId,
        params Guid[] movieIds)
    {
        var group = new ActorCollectionSync.ActorGroup(ActorCollectionSync.GroupKey(displayName), displayName)
        {
            Provider = provider,
            ActorId = actorId
        };
        // MovieIds 是只读属性，只能这样填。
        group.MovieIds.UnionWith(movieIds);
        return group;
    }

    private static ActorCollectionSync.ExistingCollection Existing(Guid id, string name, string provider = "",
        string actorId = "", params Guid[] memberIds)
    {
        return new()
        {
            Id = id,
            Name = name,
            Provider = provider,
            ActorId = actorId,
            MemberIds = new HashSet<Guid>(memberIds)
        };
    }

    [Fact]
    // 空名演员丢弃：演员替换表把演员置空时库里留下的就是空显示名。
    public void Build_IgnoresBlankActorNames()
    {
        Assert.Empty(ActorCollectionSync.Build(new[]
        {
            Ref(A, ""), Ref(A, "   "), Ref(A, null)
        }));
    }

    [Fact]
    // 分组键只用于匹配，合集名必须保持库里看到的原样显示名。
    public void Build_GroupsNamesIgnoringCaseAndSpacesAndKeepsFirstDisplayName()
    {
        var groups = ActorCollectionSync.Build(new[]
        {
            Ref(A, "Yui Hatsumi"), Ref(B, " yui hatsumi ")
        });

        var group = Assert.Single(groups.Values);
        Assert.Equal("yui hatsumi", group.Key);
        Assert.Equal("Yui Hatsumi", group.DisplayName);
        Assert.Equal(new[] { A, B }, group.MovieIds.OrderBy(x => x));
    }

    [Fact]
    // 这就是把键从 pid 改成显示名的原因（设计 4.2-3）：MovieProvider 只在演员有照片时才 SetPid。
    public void Build_KeepsOneGroupWhenSomeMoviesHavePidAndOthersNot()
    {
        var groups = ActorCollectionSync.Build(new[]
        {
            Ref(A, "有栖"), Ref(B, "有栖", "Gfriends", "g-1"), Ref(C, "有栖", "JavBus", "j-9")
        });

        var group = Assert.Single(groups.Values);
        Assert.Equal(3, group.MovieIds.Count);
        Assert.Equal("Gfriends", group.Provider);
        Assert.Equal("g-1", group.ActorId);
    }

    [Fact]
    // 设计 D1：一部影片可同时归属多个演员合集。
    public void Build_PutsOneMovieInEveryActorGroup()
    {
        var groups = ActorCollectionSync.Build(new[]
        {
            Ref(A, "演员甲"), Ref(A, "演员乙")
        });

        Assert.Equal(2, groups.Count);
        Assert.Contains(A, groups["演员甲"].MovieIds);
        Assert.Contains(A, groups["演员乙"].MovieIds);
    }

    [Fact]
    // pid 串必须与 MovieProvider 写入的格式一致，否则 GetPid 解不回来、封面解析不到。
    public void EncodePid_MatchesProviderFormatAndRoundTrips()
    {
        var raw = ActorCollectionSync.EncodePid("Gfriends", "有栖");
        var pid = ProviderId.Parse(raw);

        Assert.Equal("Gfriends", pid.Provider);
        Assert.Equal("有栖", pid.Id);
        Assert.StartsWith("Gfriends:", raw);
    }

    [Fact]
    // 用户改过合集名时按 pid 命中：只对齐成员，不改名。
    public void Index_PidHitWinsOverName()
    {
        var renamed = Existing(D, "我改过的名字", "Gfriends", "g-1", A);
        var index = new ActorCollectionSync.Index(new[] { renamed });

        Assert.Same(renamed, index.Find(Group("有栖", "Gfriends", "g-1", B)));
    }

    [Fact]
    // 复用用户手建的同名合集；都命中不了才新建。
    public void Index_FallsBackToNameHit()
    {
        var manual = Existing(D, "有栖", memberIds: A);
        var index = new ActorCollectionSync.Index(new[] { manual });

        Assert.Same(manual, index.Find(Group("有栖", "Gfriends", "g-1", B)));
        Assert.Null(index.Find(Group("别人", "Gfriends", "g-2", B)));
    }

    [Fact]
    // 演员 id 只差大小写是两个不同的人，按 pid 查必须区分开，否则两人被并成一个合集；
    // provider 段仍大小写不敏感（与 Jellyfin 取 provider id 的口径一致）。
    public void Index_FindsCaseVaryingActorIdsSeparately()
    {
        var lower = Existing(D, "合集甲", "JavBus", "j-9", A);
        var upper = Existing(C, "合集乙", "JavBus", "J-9", B);
        var index = new ActorCollectionSync.Index(new[] { lower, upper });

        Assert.Same(lower, index.Find(Group("合集甲", "JavBus", "j-9")));
        Assert.Same(upper, index.Find(Group("合集乙", "JavBus", "J-9")));
        // 第三个断言证明 pid 命中先于同名命中：名字要的是「合集乙」，provider 换写法后仍指向 lower。
        Assert.Same(lower, index.Find(Group("合集乙", "javbus", "j-9")));
    }

    [Fact]
    // 设计 D3 + D12：阈值只门控创建。
    public void Plan_SkipsCreationBelowThreshold()
    {
        var plan = ActorCollectionSync.Plan(Group("有栖", "Gfriends", "g-1", A, B), null, new[] { A, B }, 3);

        Assert.Equal(ActorCollectionSync.SyncAction.None, plan.Action);
        Assert.NotEmpty(plan.SkipReason);
    }

    [Fact]
    public void Plan_CreatesWithAllMembersWhenThresholdReached()
    {
        var plan = ActorCollectionSync.Plan(Group("有栖", "Gfriends", "g-1", A, B, C), null, new[] { A, B, C }, 3);

        Assert.Equal(ActorCollectionSync.SyncAction.Create, plan.Action);
        Assert.Equal("有栖", plan.Name);
        Assert.Null(plan.CollectionId);
        Assert.Equal(new[] { A, B, C }, plan.AddItemIds.OrderBy(x => x));
        Assert.Empty(plan.RemoveItemIds);
    }

    [Fact]
    // 阈值设 1 等价于「不做过滤」，即本轮改造最初的形态。
    public void Plan_CreatesForEveryActorWhenThresholdIsOne()
    {
        var plan = ActorCollectionSync.Plan(Group("有栖", "", "", A), null, new[] { A }, 1);

        Assert.Equal(ActorCollectionSync.SyncAction.Create, plan.Action);
    }

    [Fact]
    public void Plan_NormalizesInvalidThresholdToOne()
    {
        Assert.Equal(ActorCollectionSync.SyncAction.Create,
            ActorCollectionSync.Plan(Group("有栖", "", "", A), null, new[] { A }, 0).Action);
    }

    [Fact]
    // 跌破阈值不回收、永不删除合集实体（设计 D12 / 4.3）。
    public void Plan_KeepsAligningExistingCollectionBelowThreshold()
    {
        var existing = Existing(D, "有栖", "Gfriends", "g-1", A, B, C);
        var plan = ActorCollectionSync.Plan(Group("有栖", "Gfriends", "g-1", A), existing, new[] { A, B, C }, 3);

        Assert.Equal(ActorCollectionSync.SyncAction.Update, plan.Action);
        Assert.Equal(new[] { B, C }, plan.RemoveItemIds.OrderBy(x => x));
        Assert.Empty(plan.AddItemIds);
    }

    [Fact]
    // 幂等的判定点：重复跑任务不产生写库。
    public void Plan_DoesNothingWhenMembersMatch()
    {
        var existing = Existing(D, "有栖", "Gfriends", "g-1", A, B);
        var plan = ActorCollectionSync.Plan(Group("有栖", "Gfriends", "g-1", A, B), existing, new[] { A, B }, 3);

        Assert.Equal(ActorCollectionSync.SyncAction.None, plan.Action);
        Assert.Empty(plan.SkipReason);
        Assert.False(plan.NeedsProviderId);
    }

    [Fact]
    // 防一次同步吃掉用户手工整理的合集（设计 4.2-4）。
    public void Plan_RemovalLimitedToScannedMovies()
    {
        var handAdded = Guid.NewGuid();
        var existing = Existing(D, "有栖", "Gfriends", "g-1", A, B, handAdded);
        var plan = ActorCollectionSync.Plan(Group("有栖", "Gfriends", "g-1", A, C), existing, new[] { A, B, C }, 3);

        Assert.Equal(new[] { C }, plan.AddItemIds);
        Assert.Equal(new[] { B }, plan.RemoveItemIds);
    }

    [Fact]
    // 同名命中要补写 pid，否则 CollectionImageProvider 解析不到封面。
    public void Plan_RequestsPidWriteWhenNameHitHasNoPid()
    {
        var manual = Existing(D, "有栖", memberIds: A);
        var plan = ActorCollectionSync.Plan(Group("有栖", "Gfriends", "g-1", A), manual, new[] { A }, 3);

        Assert.True(plan.NeedsProviderId);
        Assert.Equal(ActorCollectionSync.SyncAction.Update, plan.Action);
    }

    [Fact]
    // 两位演员撞同一个舞台名是这里的常态：同名命中但两边 pid 不同，必须让写入方看得见（设计 4.5 记 Warn）。
    public void Plan_ReportsNameConflictWhenNameHitHasDifferentPid()
    {
        var other = Existing(D, "有栖", "Gfriends", "g-1", A, B);
        var group = Group("有栖", "JavBus", "j-9", A, C);
        var existing = new ActorCollectionSync.Index(new[] { other }).Find(group, out var byPid);

        Assert.False(byPid);
        Assert.Same(other, existing);

        var plan = ActorCollectionSync.Plan(group, existing, new[] { A, B, C }, 3);

        Assert.NotEqual(ActorCollectionSync.SyncAction.Create, plan.Action);
        Assert.True(plan.NameConflict);
        Assert.False(plan.NeedsProviderId);
        Assert.NotNull(plan.CollectionId);
        Assert.Equal(D, plan.CollectionId.Value);
    }

    [Fact]
    // 合集那边没 pid 时不算撞名：这是设计 4.2 第 99 行的「复用并补写 pid」。
    public void Plan_NameHitWithoutPidOnExistingSideIsNotAConflict()
    {
        var manual = Existing(D, "有栖", memberIds: A);
        var group = Group("有栖", "Gfriends", "g-1", A, B);
        var existing = new ActorCollectionSync.Index(new[] { manual }).Find(group, out var byPid);

        Assert.False(byPid);

        var plan = ActorCollectionSync.Plan(group, existing, new[] { A, B }, 3);

        Assert.False(plan.NameConflict);
        Assert.True(plan.NeedsProviderId);
    }

    [Fact]
    // 「手工条目不动」只保护扫描范围外的条目：范围内的影片本次不含这位演员，就正常移除。
    public void Plan_RemovesInScopeHandAddedMember()
    {
        var existing = Existing(D, "有栖", "Gfriends", "g-1", A, B);
        var plan = ActorCollectionSync.Plan(Group("有栖", "Gfriends", "g-1", A), existing, new[] { A, B, C }, 3);

        Assert.Empty(plan.AddItemIds);
        Assert.Equal(new[] { B }, plan.RemoveItemIds);
    }

    [Fact]
    // 服务端给回 null（LinkedChildren 实际可空，仓库又没开可空注解）不能把纯计算崩掉：
    // 空成员集按「全要加」算，空范围按「什么都不移除」算。
    public void Plan_ToleratesNullMemberSetAndNullScanScope()
    {
        var noMembers = Existing(D, "有栖", "Gfriends", "g-1");
        noMembers.MemberIds = null;
        var plan = ActorCollectionSync.Plan(Group("有栖", "Gfriends", "g-1", A, B), noMembers, null, 3);

        Assert.Equal(ActorCollectionSync.SyncAction.Update, plan.Action);
        Assert.Equal(new[] { A, B }, plan.AddItemIds.OrderBy(x => x));
        Assert.Empty(plan.RemoveItemIds);
        Assert.False(plan.NameConflict);
    }
}
