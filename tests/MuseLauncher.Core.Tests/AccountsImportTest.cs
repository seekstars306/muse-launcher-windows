using MuseLauncher.Core;
using Xunit;

namespace MuseLauncher.Core.Tests;

public sealed class AccountsImportTest
{
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
            if (FailWrites) throw new IOException("Synthetic import write failure");
            foreach (var (key, value) in values) Data[key] = value;
        }
        public void PutString(string key, string? value) => PutAll(new Dictionary<string, object?> { [key] = value });
        public void PutLong(string key, long value) => PutAll(new Dictionary<string, object?> { [key] = value });
        public void PutBoolean(string key, bool value) => PutAll(new Dictionary<string, object?> { [key] = value });
    }

    [Fact]
    public void SameNames_AppendNewIdsAndKeepExistingAccountsAndCurrent()
    {
        var kv = new MemoryKv();
        var store = new AccountsStore(kv);
        var first = store.AddDefault();
        first.Name = "same";
        first.AvatarImage = "synthetic/existing.png";
        first.Tags.Add("existing");
        store.Update(first);
        var second = store.AddDefault();
        store.SetCurrent(first.Id);
        store.SetLogged(first.Id, true);
        var previous = kv.GetString("list", null);
        var writes = kv.WriteAttempts;
        var draft = new Account(first.Id, "same")
        {
            Avatar = "猫",
            AvatarImage = "synthetic/imported.png",
            Proxy = "http://127.0.0.1:8888",
            ForceProxy = true,
            Tags = new List<string> { "one", "two", "one" },
            Logged = true,
        };

        var added = store.AppendImported(new[] { draft, draft });

        Assert.Equal(new[] { 3L, 4L }, added.Select(account => account.Id));
        Assert.Equal(new[] { first.Id, second.Id, 3L, 4L }, store.All().Select(account => account.Id));
        Assert.Equal(first.Id, store.GetCurrentId());
        Assert.Equal(writes + 1, kv.WriteAttempts);
        Assert.Equal(5L, kv.GetLong("nextId", -1));
        Assert.Equal(previous, kv.GetString("list_prev", null));
        Assert.Equal("same", store.FindById(first.Id)!.Name);
        Assert.Equal("synthetic/existing.png", store.FindById(first.Id)!.AvatarImage);
        Assert.Equal(new[] { "existing" }, store.FindById(first.Id)!.Tags);
        Assert.True(store.FindById(first.Id)!.Logged);
        Assert.Equal(second.Name, store.FindById(second.Id)!.Name);
        foreach (var account in added)
        {
            Assert.Equal(draft.Name, account.Name);
            Assert.Equal(draft.Avatar, account.Avatar);
            Assert.Equal(draft.AvatarImage, account.AvatarImage);
            Assert.Equal(draft.Proxy, account.Proxy);
            Assert.Equal(draft.ForceProxy, account.ForceProxy);
            Assert.Equal(draft.Tags, account.Tags);
            Assert.False(account.Logged);
        }
        var reloaded = new AccountsStore(kv);
        Assert.Equal(first.Id, reloaded.GetCurrentId());
        Assert.Equal(AccountJsonCodec.ToJson(store.All()), AccountJsonCodec.ToJson(reloaded.All()));
        Assert.Equal(5L, Assert.Single(reloaded.AppendImported(new[] { draft })).Id);
    }

    [Fact]
    public void Allocation_SkipsIdsReservedForDirectories()
    {
        var kv = new MemoryKv();
        var reservedDirectoryIds = new HashSet<long> { 2, 3, 5 };
        var store = new AccountsStore(kv, reservedDirectoryIds.Contains);
        var existing = store.AddDefault();

        var added = store.AppendImported(new[]
        {
            new Account(80, "same"), new Account(81, "same"), new Account(82, "same"),
        });

        Assert.Equal(new[] { 4L, 6L, 7L }, added.Select(account => account.Id));
        Assert.Equal(existing.Id, store.GetCurrentId());
        Assert.Equal(8L, kv.GetLong("nextId", -1));
        Assert.DoesNotContain(store.All(), account => reservedDirectoryIds.Contains(account.Id));
    }

    [Fact]
    public void EmptyStore_SelectsFirstAddedIdAndPublishesOnlyAfterPersist()
    {
        var kv = new MemoryKv();
        var store = new AccountsStore(kv, id => id is 1 or 2);
        var prepareCalls = 0;
        kv.BeforeWrite = () =>
        {
            Assert.Empty(store.All());
            Assert.Equal(-1L, store.GetCurrentId());
        };

        var added = store.AppendImported(new[] { new Account(90, "first"), new Account(90, "second") }, items =>
        {
            prepareCalls++;
            Assert.Equal(new[] { 3L, 4L }, items.Select(account => account.Id));
            Assert.Empty(store.All());
            Assert.Equal(-1L, store.GetCurrentId());
        });

        kv.BeforeWrite = null;
        Assert.Equal(1, prepareCalls);
        Assert.Equal(1, kv.WriteAttempts);
        Assert.Equal(3L, store.GetCurrentId());
        Assert.Equal(added[0].Id, kv.GetLong("currentId", -1));
        Assert.Equal(5L, kv.GetLong("nextId", -1));
        Assert.Equal(3L, new AccountsStore(kv).GetCurrentId());
    }

    [Fact]
    public void Prepare_UpdatesCandidateAndKeepsAllReferencesIsolated()
    {
        var kv = new MemoryKv();
        var store = new AccountsStore(kv);
        var draft = new Account(42, "draft")
        {
            AvatarImage = "synthetic/source.png",
            Tags = new List<string> { "source" },
            Logged = true,
        };
        IReadOnlyList<Account>? prepared = null;
        var added = store.AppendImported(new[] { draft, draft }, items =>
        {
            prepared = items;
            Assert.False(items[0].Logged);
            Assert.NotSame(draft, items[0]);
            Assert.NotSame(items[0], items[1]);
            Assert.NotSame(items[0].Tags, items[1].Tags);
            // The collection cannot replace an allocated account; Account.Id itself is getter-only.
            var readOnly = Assert.IsAssignableFrom<IList<Account>>(items);
            Assert.True(readOnly.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => readOnly[0] = new Account(999, "replacement"));
            foreach (var account in items)
            {
                account.Name = "prepared";
                account.Avatar = "P";
                account.AvatarImage = $"synthetic/avatars/{account.Id}.png";
                account.Proxy = "socks5://127.0.0.1:1080";
                account.ForceProxy = true;
                account.Tags.Add("prepared");
                account.Logged = true;
            }
        });

        Assert.NotNull(prepared);
        Assert.Equal(42L, draft.Id);
        Assert.Equal("draft", draft.Name);
        Assert.Equal("synthetic/source.png", draft.AvatarImage);
        Assert.Equal(new[] { "source" }, draft.Tags);
        Assert.True(draft.Logged);
        Assert.NotSame(prepared[0], added[0]);
        Assert.NotSame(prepared[0].Tags, added[0].Tags);
        Assert.NotSame(draft.Tags, added[0].Tags);
        Assert.NotSame(added[0].Tags, added[1].Tags);
        var committed = AccountJsonCodec.ToJson(store.All());
        Assert.Equal(committed, AccountJsonCodec.ToJson(new AccountsStore(kv).All()));
        foreach (var account in added)
        {
            var persisted = store.FindById(account.Id)!;
            Assert.Equal("prepared", persisted.Name);
            Assert.Equal("P", persisted.Avatar);
            Assert.Equal($"synthetic/avatars/{account.Id}.png", persisted.AvatarImage);
            Assert.Equal("socks5://127.0.0.1:1080", persisted.Proxy);
            Assert.True(persisted.ForceProxy);
            Assert.Equal(new[] { "source", "prepared" }, persisted.Tags);
            Assert.False(persisted.Logged);
            Assert.False(account.Logged);
        }

        prepared[0].Name = "retained callback reference";
        prepared[0].AvatarImage = "synthetic/callback-only.png";
        prepared[0].Tags.Clear();
        Assert.Equal("prepared", added[0].Name);
        Assert.Equal(new[] { "source", "prepared" }, added[0].Tags);
        draft.Name = "changed draft";
        draft.Tags.Add("draft-only");
        Assert.Equal("prepared", added[1].Name);
        Assert.Equal(new[] { "source", "prepared" }, added[1].Tags);
        added[0].Name = "changed result";
        added[0].AvatarImage = "synthetic/result-only.png";
        added[0].Tags.Clear();
        Assert.Equal(new[] { "source", "prepared" }, added[1].Tags);
        Assert.Equal(committed, AccountJsonCodec.ToJson(store.All()));
        Assert.Equal(committed, kv.GetString("list", null));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedPrepareOrPersist_LeavesStateAndIdsAvailableForRetry(bool failPersist, bool hasExisting)
    {
        var kv = new MemoryKv();
        var store = new AccountsStore(kv);
        var current = -1L;
        if (hasExisting)
        {
            var existing = store.AddDefault();
            existing.AvatarImage = "synthetic/existing.png";
            existing.Tags.Add("existing");
            store.Update(existing);
            store.SetLogged(existing.Id, true);
            current = existing.Id;
        }
        var before = AccountJsonCodec.ToJson(store.All());
        var persistedBefore = new Dictionary<string, object?>(kv.Data);
        var writeAttempts = kv.WriteAttempts;
        var drafts = new[] { new Account(50, "one"), new Account(60, "two") };
        IReadOnlyList<Account>? failedPrepared = null;
        kv.FailWrites = failPersist;

        Assert.Throws<IOException>(() => store.AppendImported(drafts, items =>
        {
            failedPrepared = items;
            foreach (var account in items)
            {
                account.AvatarImage = $"synthetic/prepared/{account.Id}.png";
                account.Tags.Add("uncommitted");
            }
            Assert.Equal(before, AccountJsonCodec.ToJson(store.All()));
            Assert.Equal(current, store.GetCurrentId());
            if (!failPersist) throw new IOException("Synthetic prepare failure");
        }));

        Assert.NotNull(failedPrepared);
        Assert.Equal(before, AccountJsonCodec.ToJson(store.All()));
        Assert.Equal(current, store.GetCurrentId());
        Assert.Equal(writeAttempts + (failPersist ? 1 : 0), kv.WriteAttempts);
        Assert.Equal(persistedBefore.Count, kv.Data.Count);
        foreach (var (key, value) in persistedBefore) Assert.Equal(value, kv.Data[key]);
        if (hasExisting) Assert.True(Assert.Single(store.All()).Logged);
        Assert.Equal(before, AccountJsonCodec.ToJson(new AccountsStore(kv).All()));
        Assert.Equal(current, new AccountsStore(kv).GetCurrentId());

        kv.FailWrites = false;
        var retried = store.AppendImported(drafts);
        Assert.Equal(failedPrepared.Select(account => account.Id), retried.Select(account => account.Id));
        Assert.Equal(hasExisting ? current : retried[0].Id, store.GetCurrentId());
        Assert.Equal(retried[^1].Id + 1, kv.GetLong("nextId", -1));
        Assert.All(retried, account =>
        {
            Assert.Equal("", account.AvatarImage);
            Assert.Empty(account.Tags);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyImport_DoesNotPrepareOrWrite(bool hasExisting)
    {
        var kv = new MemoryKv();
        var store = new AccountsStore(kv);
        if (hasExisting) store.AddDefault();
        var writes = kv.WriteAttempts;
        var before = AccountJsonCodec.ToJson(store.All());
        var current = store.GetCurrentId();
        kv.FailWrites = true;

        var added = store.AppendImported(Array.Empty<Account>(), _ => throw new InvalidOperationException("Must not prepare"));

        Assert.Empty(added);
        Assert.Equal(writes, kv.WriteAttempts);
        Assert.Equal(current, store.GetCurrentId());
        Assert.Equal(before, AccountJsonCodec.ToJson(store.All()));
        kv.FailWrites = false;
        Assert.Equal(hasExisting ? 2L : 1L, Assert.Single(store.AppendImported(new[] { new Account(99, "next") })).Id);
    }

    [Fact]
    public void ImportLimit_Allows200AndRejects201BeforePreparingOrWriting()
    {
        var kv = new MemoryKv();
        var store = new AccountsStore(kv);
        var drafts = Enumerable.Range(0, 201).Select(_ => new Account(1000, "same")).ToArray();
        var prepareCalled = false;

        Assert.Throws<ArgumentOutOfRangeException>(() => store.AppendImported(drafts, _ => prepareCalled = true));

        Assert.False(prepareCalled);
        Assert.Equal(0, kv.WriteAttempts);
        Assert.Empty(store.All());
        Assert.Equal(-1L, store.GetCurrentId());
        var added = store.AppendImported(drafts.Take(200).ToArray());
        Assert.Equal(Enumerable.Range(1, 200).Select(id => (long)id), added.Select(account => account.Id));
        Assert.Equal(1, kv.WriteAttempts);
        Assert.Equal(1L, store.GetCurrentId());
        Assert.Equal(201L, kv.GetLong("nextId", -1));
    }

    [Fact]
    public void NullDrafts_AreRejectedWithoutConsumingIds()
    {
        var kv = new MemoryKv();
        var store = new AccountsStore(kv);

        Assert.Throws<ArgumentNullException>(() => store.AppendImported(null!));
        Assert.Throws<ArgumentNullException>(() => store.AppendImported(new Account[] { new(40, "valid"), null! }));

        Assert.Equal(0, kv.WriteAttempts);
        Assert.Empty(store.All());
        Assert.Equal(1L, Assert.Single(store.AppendImported(new[] { new Account(40, "valid") })).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExhaustedBatch_RejectsBeforePrepareAndKeepsEarlierIdsForRetry(bool reserveLastId)
    {
        var kv = new MemoryKv();
        kv.Data["nextId"] = long.MaxValue - 1;
        var store = new AccountsStore(kv, id => reserveLastId && id == long.MaxValue);
        var prepareCalled = false;

        Assert.Throws<InvalidOperationException>(() => store.AppendImported(
            new[] { new Account(4, "first"), new Account(5, "second") }, _ => prepareCalled = true));

        Assert.False(prepareCalled);
        Assert.Empty(store.All());
        Assert.Equal(-1L, store.GetCurrentId());
        Assert.Equal(0, kv.WriteAttempts);
        Assert.Equal(long.MaxValue - 1, kv.GetLong("nextId", -1));
        var retried = Assert.Single(store.AppendImported(new[] { new Account(4, "retry") }));
        Assert.Equal(long.MaxValue - 1, retried.Id);
        Assert.Equal(retried.Id, store.GetCurrentId());
        Assert.Equal(long.MaxValue, kv.GetLong("nextId", -1));
        var writes = kv.WriteAttempts;
        Assert.Throws<InvalidOperationException>(() => store.AppendImported(new[] { new Account(4, "exhausted") }));
        Assert.Equal(writes, kv.WriteAttempts);
        Assert.Single(store.All());
    }
}
