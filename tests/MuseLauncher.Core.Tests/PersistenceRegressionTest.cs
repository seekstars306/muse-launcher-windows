using MuseLauncher.Core;
using Xunit;

namespace MuseLauncher.Core.Tests;

public sealed class PersistenceRegressionTest
{
    private sealed class FailingKv : IKeyValueStore
    {
        public readonly Dictionary<string, object?> Data = new();
        public bool FailWrites;
        public string? GetString(string key, string? fallback) => Data.TryGetValue(key, out var value) && value is string s ? s : fallback;
        public long GetLong(string key, long fallback) => Data.TryGetValue(key, out var value) && value is long l ? l : fallback;
        public bool GetBoolean(string key, bool fallback) => Data.TryGetValue(key, out var value) && value is bool b ? b : fallback;
        public void PutAll(IReadOnlyDictionary<string, object?> values)
        {
            if (FailWrites) throw new IOException("Synthetic write failure");
            foreach (var (key, value) in values) Data[key] = value;
        }
        public void PutString(string key, string? value) => PutAll(new Dictionary<string, object?> { [key] = value });
        public void PutLong(string key, long value) => PutAll(new Dictionary<string, object?> { [key] = value });
        public void PutBoolean(string key, bool value) => PutAll(new Dictionary<string, object?> { [key] = value });
    }

    [Fact]
    public void AvatarImage_Clone_PreservesPathWithoutSharingMutableState()
    {
        var account = new Account(7, "synthetic") { AvatarImage = "synthetic/original.png" };
        account.Tags.Add("original");
        var clone = account.Clone();

        Assert.NotSame(account, clone);
        Assert.Equal(account.AvatarImage, clone.AvatarImage);
        clone.AvatarImage = "synthetic/changed.png";
        clone.Tags.Add("draft");
        Assert.Equal("synthetic/original.png", account.AvatarImage);
        Assert.Equal(new[] { "original" }, account.Tags);
    }

    [Theory]
    [InlineData("synthetic/replacement.png")]
    [InlineData("")]
    public void AvatarImage_SuccessfulUpdate_PersistsAndIsolatesSnapshots(string path)
    {
        var kv = new FailingKv();
        var store = new AccountsStore(kv);
        var account = store.AddDefault();
        account.AvatarImage = "synthetic/original.png";
        store.Update(account);
        var oldSnapshot = store.FindById(account.Id)!;
        var draft = store.FindById(account.Id)!;
        draft.AvatarImage = path;
        Assert.Equal("synthetic/original.png", store.FindById(account.Id)!.AvatarImage);

        store.Update(draft);
        draft.AvatarImage = "synthetic/uncommitted.png";
        var listSnapshot = Assert.Single(store.All());
        Assert.Equal(path, listSnapshot.AvatarImage);
        listSnapshot.AvatarImage = "synthetic/list-only.png";

        Assert.Equal("synthetic/original.png", oldSnapshot.AvatarImage);
        Assert.Equal(path, store.FindById(account.Id)!.AvatarImage);
        Assert.Equal(path, new AccountsStore(kv).FindById(account.Id)!.AvatarImage);
        Assert.Equal("synthetic/original.png",
            Assert.Single(AccountJsonCodec.FromJsonOrNull(kv.GetString("list_prev", null))!).AvatarImage);
    }

    [Fact]
    public void AvatarImage_FailedUpdate_PreservesPublishedStateAndBackup()
    {
        var kv = new FailingKv();
        var store = new AccountsStore(kv);
        var account = store.AddDefault();
        account.AvatarImage = "synthetic/original.png";
        store.Update(account);
        var oldList = kv.GetString("list", null);
        var oldBackup = kv.GetString("list_prev", null);
        var draft = store.FindById(account.Id)!;
        draft.AvatarImage = "synthetic/uncommitted.png";
        kv.FailWrites = true;

        Assert.Throws<IOException>(() => store.Update(draft));
        Assert.Equal("synthetic/original.png", store.FindById(account.Id)!.AvatarImage);
        Assert.Equal("synthetic/original.png", Assert.Single(store.All()).AvatarImage);
        Assert.Equal("synthetic/original.png", new AccountsStore(kv).FindById(account.Id)!.AvatarImage);
        Assert.Equal(oldList, kv.GetString("list", null));
        Assert.Equal(oldBackup, kv.GetString("list_prev", null));
    }

    [Fact]
    public void FailedAdd_DoesNotPublishOrConsumeAnId()
    {
        var kv = new FailingKv();
        var store = new AccountsStore(kv);
        kv.FailWrites = true;
        Assert.Throws<IOException>(() => store.AddDefault());
        Assert.Empty(store.All());
        Assert.Equal(-1L, store.GetCurrentId());
        kv.FailWrites = false;
        Assert.Equal(1L, store.AddDefault().Id);
    }

    [Fact]
    public void FailedRename_DoesNotChangePublishedAccountOrBackup()
    {
        var kv = new FailingKv();
        var store = new AccountsStore(kv);
        var account = store.AddDefault();
        var oldList = kv.GetString("list", null);
        kv.FailWrites = true;
        Assert.Throws<IOException>(() => store.Rename(account.Id, "uncommitted"));
        Assert.Equal("Muse 1", store.FindById(account.Id)!.Name);
        Assert.Equal(oldList, kv.GetString("list", null));
        kv.FailWrites = false;
        store.Rename(account.Id, "new-committed");
        Assert.Equal(oldList, kv.GetString("list_prev", null));
    }

    [Fact]
    public void FailedRemove_DoesNotRemovePublishedAccount()
    {
        var kv = new FailingKv();
        var store = new AccountsStore(kv);
        var account = store.AddDefault();
        kv.FailWrites = true;
        Assert.Throws<IOException>(() => store.Remove(account.Id));
        Assert.Single(store.All());
        Assert.Equal(account.Id, store.GetCurrentId());
    }

    [Fact]
    public void FailedSetCurrent_DoesNotPublishNewCurrentId()
    {
        var kv = new FailingKv();
        var store = new AccountsStore(kv);
        var first = store.AddDefault();
        var second = store.AddDefault();
        kv.FailWrites = true;
        Assert.Throws<IOException>(() => store.SetCurrent(first.Id));
        Assert.Equal(second.Id, store.GetCurrentId());
        Assert.Equal(second.Id, kv.GetLong("currentId", -1));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void StaleNextId_IsRepairedAboveExistingIds(long nextId)
    {
        var kv = new FailingKv();
        kv.Data["list"] = "[{\"id\":3,\"name\":\"synthetic\"}]";
        kv.Data["nextId"] = nextId;
        var store = new AccountsStore(kv);
        Assert.Equal(4L, store.AddDefault().Id);
        Assert.Equal(2, store.All().Select(a => a.Id).Distinct().Count());
        Assert.Equal("corrupt", store.TakeLoadNotice());
    }

    [Fact]
    public void DuplicateAndInvalidIds_AreSkippedAndNoticed()
    {
        var kv = new FailingKv();
        kv.Data["list"] = "[{\"id\":1,\"name\":\"first\"},{\"id\":1,\"name\":\"duplicate\"},"
            + "{\"id\":0,\"name\":\"zero\"},{\"id\":-1,\"name\":\"negative\"}]";
        kv.Data["nextId"] = 2L;
        var store = new AccountsStore(kv);
        Assert.Single(store.All());
        Assert.Equal("first", store.All()[0].Name);
        Assert.Equal("corrupt", store.TakeLoadNotice());
    }

    [Fact]
    public void AllocationOverflow_DoesNotWrapOrPublishAnAccount()
    {
        var kv = new FailingKv();
        kv.Data["nextId"] = long.MaxValue;
        var store = new AccountsStore(kv);
        Assert.Throws<InvalidOperationException>(() => store.AddDefault());
        Assert.Empty(store.All());
        Assert.Equal(long.MaxValue, kv.GetLong("nextId", -1));
    }
}
