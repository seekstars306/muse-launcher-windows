using MuseLauncher.Core;
using Xunit;

namespace MuseLauncher.Core.Tests;

/** 账号存储备份/自愈/通知逻辑（HashMap 假 KV，纯托管测试）——移植自安卓 AccountsStoreTest */
public class AccountsStoreTest
{
    /// <summary>最小 Dictionary 键值实现：putAll 覆盖写，模拟 SharedPreferences</summary>
    private sealed class FakeKv : IKeyValueStore
    {
        public readonly Dictionary<string, object?> M = new();

        public string? GetString(string key, string? defValue)
            => M.TryGetValue(key, out var v) && v is string s ? s : defValue;

        public long GetLong(string key, long defValue)
            => M.TryGetValue(key, out var v) && v is long l ? l : defValue;

        public bool GetBoolean(string key, bool defValue)
            => M.TryGetValue(key, out var v) && v is bool b ? b : defValue;

        public void PutAll(IReadOnlyDictionary<string, object?> values)
        {
            foreach (var (k, v) in values) M[k] = v;
        }

        public void PutString(string key, string? value) => M[key] = value;
        public void PutLong(string key, long value) => M[key] = value;
        public void PutBoolean(string key, bool value) => M[key] = value;
    }

    [Fact]
    public void RoundTripAcrossInstances()
    {
        var kv = new FakeKv();
        var s1 = new AccountsStore(kv);
        var a = s1.AddDefault();
        s1.Rename(a.Id, "主力号");
        var b = s1.AddDefault();
        b.Proxy = "socks5://127.0.0.1:1080";
        b.Avatar = "M";
        b.Tags.Add("测试");
        s1.SetCurrent(a.Id);
        s1.Save();

        var s2 = new AccountsStore(kv);
        Assert.Equal(2, s2.All().Count);
        Assert.Equal(a.Id, s2.GetCurrentId());
        Assert.Equal("主力号", s2.FindById(a.Id)!.Name);
        Assert.Equal("socks5://127.0.0.1:1080", s2.FindById(b.Id)!.Proxy);
        Assert.Equal("测试", s2.FindById(b.Id)!.Tags[0]);
        Assert.Null(s2.TakeLoadNotice());
    }

    [Fact]
    public void Remove_FixesCurrentAndPersists()
    {
        var kv = new FakeKv();
        var s = new AccountsStore(kv);
        var a = s.AddDefault();
        var b = s.AddDefault();
        s.SetCurrent(b.Id);
        s.Remove(b.Id);
        Assert.Equal(a.Id, s.GetCurrentId());
        Assert.Null(s.FindById(b.Id));

        var s2 = new AccountsStore(kv);
        Assert.Equal(1, s2.All().Count);
        Assert.Equal(a.Id, s2.GetCurrentId());
    }

    [Fact]
    public void RemoveLastAccount_CurrentBecomesMinusOne()
    {
        var kv = new FakeKv();
        var s = new AccountsStore(kv);
        var a = s.AddDefault();
        s.Remove(a.Id);
        Assert.Equal(-1L, s.GetCurrentId());
        var s2 = new AccountsStore(kv);
        Assert.Empty(s2.All());
        Assert.Equal(-1L, s2.GetCurrentId());
    }

    /// <summary>list 损坏但 list_prev 完好 → 自愈并通知 recovered</summary>
    [Fact]
    public void CorruptList_RecoversFromPrev()
    {
        var kv = new FakeKv();
        var s = new AccountsStore(kv);
        var a = s.AddDefault();              // save#1（prev 为空）
        a.Proxy = "http://1.2.3.4:3129";
        s.Save();                            // save#2（prev = save#1 的值，proxy 还是空）
        s.Save();                            // save#3（prev = save#2 的值，带 proxy）
        var goodList = kv.GetString("list", null);
        Assert.NotNull(goodList);
        Assert.StartsWith("[", kv.GetString("list_prev", "MISSING"));

        // 破坏主键
        kv.PutString("list", "{这不是JSON");
        // list_prev 仍是上一次的好数据
        var s2 = new AccountsStore(kv);
        Assert.Equal("recovered", s2.TakeLoadNotice());
        Assert.Equal(1, s2.All().Count);
        Assert.Equal("http://1.2.3.4:3129", s2.All()[0].Proxy);
        // 自愈后主键被写回好数据
        Assert.Equal(goodList, kv.GetString("list", null));
        // 损坏原始值已留底
        Assert.Equal("{这不是JSON", kv.GetString("list_corrupt", null));
        // 回归（真机踩过的坑）：自愈绝不能把 nextId 重置为 1，否则后续建号 id 冲突。
        // 本用例只建过一个账号（id=1），恢复后 nextId 必须仍是 2
        Assert.Equal(2L, kv.GetLong("nextId", -1));
        var added = s2.AddDefault();
        Assert.Equal(2L, added.Id);
    }

    /// <summary>list 与 list_prev 双双损坏 → 留底 + 通知 corrupt + 空列表，不再抛异常</summary>
    [Fact]
    public void CorruptEverything_BacksUpAndDegrades()
    {
        var kv = new FakeKv();
        kv.PutString("list", "{{{坏数据");
        kv.PutString("list_prev", "]]]也是坏的");
        var s = new AccountsStore(kv);
        Assert.Equal("corrupt", s.TakeLoadNotice());
        Assert.Empty(s.All());
        Assert.Equal("{{{坏数据", kv.GetString("list_corrupt", null));
        // 取走即清
        Assert.Null(s.TakeLoadNotice());
        // 后续仍可正常添加账号（新数据会覆盖，但坏数据已留底）
        s.AddDefault();
        Assert.Equal(1, s.All().Count);
    }

    /// <summary>list 为合法数组但其中一条损坏 → 跳过坏条目，不算损坏事件</summary>
    [Fact]
    public void PartialCorruption_NoNotice()
    {
        var kv = new FakeKv();
        kv.PutString("list", "[{\"id\":2,\"name\":\"好的\"},{\"id\":\"x\"}]");
        var s = new AccountsStore(kv);
        Assert.Null(s.TakeLoadNotice());
        Assert.Equal(1, s.All().Count);
        Assert.Equal("好的", s.All()[0].Name);
    }

    /// <summary>滚动备份：第二次 save 后 list_prev 应是第一次的 list 值</summary>
    [Fact]
    public void RollingPrevBackup()
    {
        var kv = new FakeKv();
        var s = new AccountsStore(kv);
        s.AddDefault(); // 第一次 save：无 prev
        Assert.Null(kv.GetString("list_prev", null));
        var first = kv.GetString("list", null);
        s.Rename(1L, "改名"); // 第二次 save：prev = 第一次的 list
        Assert.Equal(first, kv.GetString("list_prev", null));
    }

    /// <summary>新建环境继承代理：proxy 传入即写入并持久化；空/null 则保持直连</summary>
    [Fact]
    public void AddDefault_InheritsProxy()
    {
        var kv = new FakeKv();
        var s = new AccountsStore(kv);
        var a = s.AddDefault("http://u:p@1.2.3.4:3129");
        Assert.Equal("http://u:p@1.2.3.4:3129", a.Proxy);
        // 持久化校验：新实例读回仍有代理
        var s2 = new AccountsStore(kv);
        Assert.Equal("http://u:p@1.2.3.4:3129", s2.FindById(a.Id)!.Proxy);

        var b = s2.AddDefault(null);
        Assert.Equal("", b.Proxy);
        var c = s2.AddDefault("");
        Assert.Equal("", c.Proxy);
    }

    /// <summary>setCurrent 只写 currentId 独立键，不全量重写 list（账号多时切换零开销）</summary>
    [Fact]
    public void SetCurrent_WritesOnlyCurrentKey()
    {
        var kv = new FakeKv();
        var s = new AccountsStore(kv);
        s.AddDefault();
        s.AddDefault();
        var listBefore = kv.GetString("list", null);
        s.SetCurrent(2L);
        Assert.Equal(listBefore, kv.GetString("list", null)); // list 未被重写
        Assert.Equal(2L, kv.GetLong("currentId", -1));
        // 相同值不写盘
        var listAfter = kv.GetString("list", null);
        s.SetCurrent(2L);
        Assert.Equal(listAfter, kv.GetString("list", null));
        // 新实例读取正常
        var s2 = new AccountsStore(kv);
        Assert.Equal(2L, s2.GetCurrentId());
        Assert.Equal(2, s2.All().Count);
    }

    /// <summary>takeLoadNotice 取走即清，不重复提示</summary>
    [Fact]
    public void Notice_TakeOnce()
    {
        var kv = new FakeKv();
        kv.PutString("list", "坏的");
        var s = new AccountsStore(kv);
        Assert.Equal("corrupt", s.TakeLoadNotice());
        Assert.Null(s.TakeLoadNotice());
    }

    /// <summary>reload() 与构造行为一致</summary>
    [Fact]
    public void Reload_RerunsRecovery()
    {
        var kv = new FakeKv();
        var s = new AccountsStore(kv);
        s.AddDefault();
        s.Save();
        kv.PutString("list", "又坏了");
        s.Reload();
        Assert.Equal("recovered", s.TakeLoadNotice());
        Assert.Equal(1, s.All().Count);
    }
}
