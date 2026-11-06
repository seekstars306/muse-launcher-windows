using MuseLauncher.Core;
using Xunit;

/** LRU 注册表的驱逐决策——移植自安卓 LruAccountRegistryTest */
namespace MuseLauncher.Core.Tests;

public class LruAccountRegistryTest
{
    [Fact]
    public void PutGetRemoveContains()
    {
        var r = new LruAccountRegistry<string>(3);
        r.Put(1L, "a");
        Assert.True(r.Contains(1L));
        Assert.Equal("a", r.Get(1L));
        Assert.Equal("a", r.Remove(1L));
        Assert.Null(r.Remove(1L));
        Assert.False(r.Contains(1L));
        Assert.Equal(0, r.Size);
    }

    [Fact]
    public void UnderCapacity_NeverEvicts()
    {
        var r = new LruAccountRegistry<string>(3);
        r.Put(1L, "a");
        r.Put(2L, "b");
        var evict = r.TouchAndEvict(2L, "b2");
        Assert.Empty(evict);
        Assert.Equal(2, r.Size);
        Assert.Equal("b2", r.Get(2L));
    }

    [Fact]
    public void OverCapacity_EvictsEldest()
    {
        var r = new LruAccountRegistry<string>(3);
        r.Put(1L, "a");
        r.Put(2L, "b");
        r.Put(3L, "c");
        var evict = r.TouchAndEvict(4L, "d"); // 放入第 4 个
        Assert.Equal(1, evict.Count);
        Assert.Equal(1L, evict[0]);           // 最久未用的 1 被驱逐
        Assert.False(r.Contains(1L));
        Assert.Equal(3, r.Size);
        // 刚放入的 4 绝不会被驱逐
        Assert.True(r.Contains(4L));
    }

    [Fact]
    public void GetRefreshesRecency()
    {
        var r = new LruAccountRegistry<string>(3);
        r.Put(1L, "a");
        r.Put(2L, "b");
        r.Put(3L, "c");
        // 访问 1，使 2 变成最旧
        r.Get(1L);
        var evict = r.TouchAndEvict(4L, "d");
        Assert.Equal(1, evict.Count);
        Assert.Equal(2L, evict[0]);
        Assert.True(r.Contains(1L));
        Assert.True(r.Contains(3L));
        Assert.True(r.Contains(4L));
    }

    [Fact]
    public void TouchExisting_NeverEvictsIt()
    {
        var r = new LruAccountRegistry<string>(2);
        r.Put(1L, "a");
        r.Put(2L, "b");
        var evict = r.TouchAndEvict(1L, "a2"); // 刷新已存在的 1
        Assert.True(evict.Count == 0 || !evict.Contains(1L));
        Assert.True(r.Contains(1L));
        Assert.Equal("a2", r.Get(1L));
    }

    [Fact]
    public void CapacityOne_KeepsOnlyTouched()
    {
        var r = new LruAccountRegistry<string>(1);
        r.Put(1L, "a");
        var evict = r.TouchAndEvict(2L, "b");
        Assert.Equal(1, evict.Count);
        Assert.Equal(1L, evict[0]);
        Assert.Equal(1, r.Size);
        Assert.Equal("b", r.Get(2L));
    }

    [Fact]
    public void Snapshot_IsCopy()
    {
        var r = new LruAccountRegistry<string>(3);
        r.Put(1L, "a");
        var snap = new System.Collections.Generic.Dictionary<long, string>(r.Snapshot());
        snap.Remove(1L);
        snap[99L] = "x";
        Assert.True(r.Contains(1L));
        Assert.False(r.Contains(99L));
    }

    [Fact]
    public void MassEviction_MultipleAtOnce()
    {
        var r = new LruAccountRegistry<string>(2);
        for (long id = 1; id <= 5; id++) r.Put(id, "v" + id);
        var evict = r.TouchAndEvict(6L, "v6");
        // 6 个超出容量 2：应驱逐 1..4（按插入序），保留 5、6
        Assert.Equal(new List<long> { 1L, 2L, 3L, 4L }, evict);
        Assert.True(r.Contains(5L));
        Assert.True(r.Contains(6L));
        Assert.Equal(2, r.Size);
    }
}
