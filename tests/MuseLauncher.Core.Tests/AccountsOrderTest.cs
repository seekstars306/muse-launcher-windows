using MuseLauncher.Core;
using Xunit;

namespace MuseLauncher.Core.Tests;

public sealed class AccountsOrderTest
{
    // 与现有账号测试一致的内存/故障 KV；所有数据仅保留在本测试实例中。
    private sealed class MemoryKv : IKeyValueStore
    {
        public readonly Dictionary<string, object?> Data = new();
        public int WriteAttempts;
        public bool FailWrites;
        public Action? BeforeWrite;
        public string? GetString(string key, string? fallback) => Data.TryGetValue(key, out var value) && value is string s ? s : fallback;
        public long GetLong(string key, long fallback) => Data.TryGetValue(key, out var value) && value is long l ? l : fallback;
        public bool GetBoolean(string key, bool fallback) => Data.TryGetValue(key, out var value) && value is bool b ? b : fallback;
        public void PutAll(IReadOnlyDictionary<string, object?> values)
        {
            WriteAttempts++;
            BeforeWrite?.Invoke();
            if (FailWrites) throw new IOException("Synthetic order write failure");
            foreach (var (key, value) in values) Data[key] = value;
        }
        public void PutString(string key, string? value) => PutAll(new Dictionary<string, object?> { [key] = value });
        public void PutLong(string key, long value) => PutAll(new Dictionary<string, object?> { [key] = value });
        public void PutBoolean(string key, bool value) => PutAll(new Dictionary<string, object?> { [key] = value });
    }

    private static AccountsStore CreateStore(MemoryKv kv, int count = 4)
    {
        var accounts = Enumerable.Range(1, count).Select(id => new Account(id, "synthetic-" + id)
        {
            Avatar = "猫" + id,
            AvatarImage = "synthetic/avatar-" + id + ".png",
            Proxy = "http://127.0.0.1:" + (8000 + id),
            ForceProxy = id % 2 != 0,
            Tags = new List<string> { "synthetic", "tag-" + id, "synthetic" },
        }).ToList();
        kv.Data["list"] = AccountJsonCodec.ToJson(accounts);
        kv.Data["list_prev"] = "[]";
        // 有意留出 ID 空档，防止移动时错误地按账号数或最大 ID 重算 nextId。
        kv.Data["nextId"] = 40L;
        kv.Data["currentId"] = count > 1 ? 2L : count == 1 ? 1L : -1L;
        var store = new AccountsStore(kv);
        foreach (var account in accounts) store.SetLogged(account.Id, account.Id % 2 != 0);
        return store;
    }

    private static void AssertAccountFieldsUnchanged(IReadOnlyList<Account> before, IReadOnlyList<Account> after)
    {
        Assert.Equal(before.Count, after.Count);
        foreach (var expected in before)
        {
            var actual = Assert.Single(after, account => account.Id == expected.Id);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Proxy, actual.Proxy);
            Assert.Equal(expected.ForceProxy, actual.ForceProxy);
            Assert.Equal(expected.Avatar, actual.Avatar);
            Assert.Equal(expected.AvatarImage, actual.AvatarImage);
            Assert.Equal(expected.Tags, actual.Tags);
            Assert.Equal(expected.Logged, actual.Logged);
        }
    }

    private static void AssertKvUnchanged(Dictionary<string, object?> before, MemoryKv kv)
    {
        Assert.Equal(before.Count, kv.Data.Count);
        foreach (var (key, value) in before)
        {
            Assert.True(kv.Data.TryGetValue(key, out var actual));
            Assert.Equal(value, actual);
        }
    }

    [Theory]
    [InlineData(4L, 1L, false, new long[] { 4, 1, 2, 3 })] // 顶部
    [InlineData(1L, 4L, true, new long[] { 2, 3, 4, 1 })] // 底部
    [InlineData(3L, 2L, false, new long[] { 1, 3, 2, 4 })] // 相邻上移
    [InlineData(2L, 3L, true, new long[] { 1, 3, 2, 4 })] // 相邻下移
    [InlineData(1L, 3L, false, new long[] { 2, 1, 3, 4 })]
    [InlineData(4L, 2L, true, new long[] { 1, 2, 4, 3 })]
    public void Move_InsertsRelativeToTarget_PersistsOnceBeforePublishingAndSurvivesReconstruction(
        long id, long targetId, bool after, long[] expectedOrder)
    {
        var kv = new MemoryKv();
        var store = CreateStore(kv);
        var before = store.All();
        var oldJson = kv.GetString("list", null);
        kv.BeforeWrite = () =>
        {
            Assert.Equal(new[] { 1L, 2L, 3L, 4L }, store.All().Select(account => account.Id));
            AssertAccountFieldsUnchanged(before, store.All());
            Assert.Equal(2L, store.GetCurrentId());
            Assert.Equal(oldJson, kv.GetString("list", null));
        };

        store.Move(id, targetId, after);

        kv.BeforeWrite = null;
        Assert.Equal(1, kv.WriteAttempts);
        Assert.Equal(expectedOrder, store.All().Select(account => account.Id));
        AssertAccountFieldsUnchanged(before, store.All());
        Assert.Equal(new[] { 1L, 2L, 3L, 4L }, before.Select(account => account.Id));
        Assert.Equal(2L, store.GetCurrentId());
        Assert.Equal(2L, kv.GetLong("currentId", -1));
        Assert.Equal(40L, kv.GetLong("nextId", -1));
        Assert.Equal(oldJson, kv.GetString("list_prev", null));
        Assert.Equal(expectedOrder, AccountJsonCodec.FromJsonOrNull(kv.GetString("list", null))!.Select(account => account.Id));

        var reloaded = new AccountsStore(kv);
        Assert.Equal(expectedOrder, reloaded.All().Select(account => account.Id));
        Assert.Equal(AccountJsonCodec.ToJson(store.All()), AccountJsonCodec.ToJson(reloaded.All()));
        Assert.Equal(2L, reloaded.GetCurrentId());
        Assert.Null(reloaded.TakeLoadNotice());
        Assert.Equal(1, kv.WriteAttempts);
    }

    [Theory]
    [InlineData(4, 1L, 1L, false)]
    [InlineData(4, 2L, 2L, true)]
    [InlineData(4, 4L, 4L, true)]
    [InlineData(4, 1L, 2L, false)]
    [InlineData(4, 2L, 1L, true)]
    [InlineData(4, 2L, 3L, false)]
    [InlineData(4, 3L, 2L, true)]
    [InlineData(4, 3L, 4L, false)]
    [InlineData(4, 4L, 3L, true)]
    [InlineData(1, 1L, 1L, false)]
    [InlineData(1, 1L, 1L, true)]
    public void Move_SameIdOrAlreadyInPosition_DoesNotWrite(int count, long id, long targetId, bool after)
    {
        var kv = new MemoryKv();
        var store = CreateStore(kv, count);
        var before = store.All();
        var persisted = new Dictionary<string, object?>(kv.Data);
        var current = store.GetCurrentId();
        kv.FailWrites = true;

        store.Move(id, targetId, after);

        Assert.Equal(0, kv.WriteAttempts);
        Assert.Equal(before.Select(account => account.Id), store.All().Select(account => account.Id));
        AssertAccountFieldsUnchanged(before, store.All());
        Assert.Equal(current, store.GetCurrentId());
        AssertKvUnchanged(persisted, kv);
    }

    [Theory]
    [InlineData(4, 99L, 1L, false, "id")]
    [InlineData(4, 1L, 99L, true, "targetId")]
    [InlineData(4, 98L, 99L, false, "id")]
    [InlineData(4, 99L, 99L, true, "id")]
    [InlineData(4, 0L, 1L, true, "id")]
    [InlineData(4, 1L, -1L, false, "targetId")]
    [InlineData(0, 1L, 2L, true, "id")]
    [InlineData(0, 1L, 1L, false, "id")]
    public void Move_MissingIdOrTarget_ThrowsWithoutWriting(int count, long id, long targetId, bool after, string parameter)
    {
        var kv = new MemoryKv();
        var store = CreateStore(kv, count);
        var before = store.All();
        var persisted = new Dictionary<string, object?>(kv.Data);
        var current = store.GetCurrentId();
        kv.FailWrites = true;

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => store.Move(id, targetId, after));

        Assert.Equal(parameter, error.ParamName);
        Assert.Equal(0, kv.WriteAttempts);
        Assert.Equal(before.Select(account => account.Id), store.All().Select(account => account.Id));
        AssertAccountFieldsUnchanged(before, store.All());
        Assert.Equal(current, store.GetCurrentId());
        AssertKvUnchanged(persisted, kv);
    }

    [Theory]
    [InlineData(2L, 4L, true)] // 移动当前账号
    [InlineData(4L, 1L, false)]
    public void Move_FailedSave_PreservesPublishedStateJsonBackupCurrentAndNextId(long id, long targetId, bool after)
    {
        var kv = new MemoryKv();
        var store = CreateStore(kv);
        var before = store.All();
        var persisted = new Dictionary<string, object?>(kv.Data);
        kv.FailWrites = true;

        Assert.Throws<IOException>(() => store.Move(id, targetId, after));

        Assert.Equal(1, kv.WriteAttempts);
        Assert.Equal(new[] { 1L, 2L, 3L, 4L }, store.All().Select(account => account.Id));
        AssertAccountFieldsUnchanged(before, store.All());
        Assert.Equal(2L, store.GetCurrentId());
        AssertKvUnchanged(persisted, kv);
        var reloaded = new AccountsStore(kv);
        Assert.Equal(new[] { 1L, 2L, 3L, 4L }, reloaded.All().Select(account => account.Id));
        Assert.Equal(2L, reloaded.GetCurrentId());

        kv.FailWrites = false;
        Assert.Equal(40L, store.AddDefault().Id);
        Assert.Equal(new[] { 1L, 2L, 3L, 4L, 40L }, store.All().Select(account => account.Id));
    }

    [Fact]
    public void Move_AddDefaultStillAppendsAndUsesUnchangedNextId()
    {
        var kv = new MemoryKv();
        var store = CreateStore(kv);

        store.Move(4, 1, false);

        Assert.Equal(40L, kv.GetLong("nextId", -1));
        Assert.Equal(2L, store.GetCurrentId());
        var added = store.AddDefault();
        Assert.Equal(40L, added.Id);
        Assert.Equal(new[] { 4L, 1L, 2L, 3L, 40L }, store.All().Select(account => account.Id));
        Assert.Equal(41L, kv.GetLong("nextId", -1));
        var reloaded = new AccountsStore(kv);
        Assert.Equal(new[] { 4L, 1L, 2L, 3L, 40L }, reloaded.All().Select(account => account.Id));
        Assert.Equal(41L, reloaded.AddDefault().Id);
    }

    [Fact]
    public void Move_ImportAppendsAfterMovedAccountsInSourceOrder()
    {
        var kv = new MemoryKv();
        var store = CreateStore(kv);
        store.Move(4, 1, false);
        var reordered = store.All();
        var writes = kv.WriteAttempts;

        var imported = store.AppendImported(new[]
        {
            new Account(900, "source-first"),
            new Account(2, "source-second"),
            new Account(100, "source-third"),
        });

        Assert.Equal(writes + 1, kv.WriteAttempts);
        Assert.Equal(new[] { 40L, 41L, 42L }, imported.Select(account => account.Id));
        Assert.Equal(new[] { 4L, 1L, 2L, 3L, 40L, 41L, 42L }, store.All().Select(account => account.Id));
        Assert.Equal(new[] { "source-first", "source-second", "source-third" }, store.All().Skip(4).Select(account => account.Name));
        AssertAccountFieldsUnchanged(reordered, store.All().Take(4).ToArray());
        Assert.Equal(2L, store.GetCurrentId());
        Assert.Equal(43L, kv.GetLong("nextId", -1));
        var reloaded = new AccountsStore(kv);
        Assert.Equal(store.All().Select(account => account.Id), reloaded.All().Select(account => account.Id));
        Assert.Equal(new[] { "source-first", "source-second", "source-third" }, reloaded.All().Skip(4).Select(account => account.Name));
        Assert.Equal(2L, reloaded.GetCurrentId());
    }
}
