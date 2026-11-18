namespace MuseLauncher.Core;

/// <summary>保存候选成功后才发布账号快照，ID 不复用，文件损坏不能静默当作首次运行。</summary>
public sealed class AccountsStore
{
    private readonly object gate = new();
    private readonly IKeyValueStore prefs;
    private readonly Func<long, bool> isIdReserved;
    private List<Account> accounts = new();
    private long nextId = 1;
    private long currentId = -1;
    private string? loadNotice;

    public AccountsStore(IKeyValueStore store, Func<long, bool>? isIdReserved = null)
    {
        prefs = store;
        this.isIdReserved = isIdReserved ?? (_ => false);
        Load();
    }

    public void Reload() { lock (gate) Load(); }

    private void Load()
    {
        var notice = (prefs as IStoreLoadNotice)?.TakeLoadNotice();
        var raw = prefs.GetString("list", "[]");
        var parsed = AccountJsonCodec.FromJsonOrNull(raw, out var skipped);
        if (parsed == null)
        {
            var backup = AccountJsonCodec.FromJsonOrNull(prefs.GetString("list_prev", null), out skipped);
            if (backup == null) throw new StoreLoadException("账号列表与历史备份均无法解析，停止修改账号数据");
            var values = new Dictionary<string, object?>
            {
                ["list"] = AccountJsonCodec.ToJson(backup),
                ["list_corrupt"] = raw,
            };
            prefs.PutAll(values);
            parsed = backup;
            notice = "recovered";
        }
        if (skipped)
        {
            prefs.PutString("list_corrupt", raw);
            notice ??= "corrupt";
        }
        var persistedNext = prefs.GetLong("nextId", 1);
        var largest = parsed.Count == 0 ? 0 : parsed.Max(account => account.Id);
        if (largest == long.MaxValue) throw new StoreLoadException("账号 ID 已到上限，无法安全分配新环境");
        var correctedNext = Math.Max(largest + 1, Math.Max(1, persistedNext));
        if (correctedNext != persistedNext) notice ??= "corrupt";
        var loadedCurrent = prefs.GetLong("currentId", -1);
        if (!parsed.Any(account => account.Id == loadedCurrent)) loadedCurrent = parsed.Count == 0 ? -1 : parsed[0].Id;
        accounts = parsed;
        nextId = correctedNext;
        currentId = loadedCurrent;
        loadNotice = notice;
    }

    private void Persist(List<Account> candidate, long candidateNext, long candidateCurrent)
    {
        var json = AccountJsonCodec.ToJson(candidate) ?? throw new InvalidDataException("账号序列化失败");
        var values = new Dictionary<string, object?>
        {
            ["list"] = json,
            ["nextId"] = candidateNext,
            ["currentId"] = candidateCurrent,
        };
        var previous = prefs.GetString("list", null);
        if (AccountJsonCodec.FromJsonOrNull(previous) != null) values["list_prev"] = previous;
        prefs.PutAll(values);
        accounts = candidate;
        nextId = candidateNext;
        currentId = candidateCurrent;
    }

    public void Save() { lock (gate) Persist(accounts.Select(account => account.Clone()).ToList(), nextId, currentId); }
    public string? TakeLoadNotice()
    {
        lock (gate)
        {
            var notice = loadNotice;
            loadNotice = null;
            return notice;
        }
    }
    public IReadOnlyList<Account> All() { lock (gate) return accounts.Select(account => account.Clone()).ToArray(); }
    public Account? FindById(long id) { lock (gate) return accounts.FirstOrDefault(account => account.Id == id)?.Clone(); }
    public long GetCurrentId() { lock (gate) return currentId; }

    public Account AddDefault() => AddDefault(null);

    public Account AddDefault(string? inheritProxy, bool inheritForceProxy = false)
    {
        lock (gate)
        {
            var id = nextId;
            while (accounts.Any(account => account.Id == id) || isIdReserved(id))
            {
                if (id == long.MaxValue) throw new InvalidOperationException("账号 ID 已耗尽");
                id = checked(id + 1);
            }
            if (id == long.MaxValue) throw new InvalidOperationException("账号 ID 已耗尽");
            var added = new Account(id, "Muse " + id)
            {
                Proxy = inheritProxy ?? "",
                ForceProxy = inheritForceProxy,
            };
            var candidate = accounts.Select(account => account.Clone()).ToList();
            candidate.Add(added);
            Persist(candidate, checked(id + 1), id);
            return added.Clone();
        }
    }

    public IReadOnlyList<Account> AppendImported(IReadOnlyList<Account> drafts, Action<IReadOnlyList<Account>>? prepare = null)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        lock (gate)
        {
            if (drafts.Count > 200) throw new ArgumentOutOfRangeException(nameof(drafts), "单次最多导入 200 个账号");
            if (drafts.Count == 0) return Array.Empty<Account>();

            var candidateNext = nextId;
            var added = new List<Account>(drafts.Count);
            foreach (var draft in drafts)
            {
                ArgumentNullException.ThrowIfNull(draft);
                var id = candidateNext;
                while (accounts.Any(account => account.Id == id) || isIdReserved(id))
                {
                    if (id == long.MaxValue) throw new InvalidOperationException("账号 ID 已耗尽");
                    id = checked(id + 1);
                }
                if (id == long.MaxValue) throw new InvalidOperationException("账号 ID 已耗尽");
                added.Add(new Account(id, draft.Name)
                {
                    Avatar = draft.Avatar,
                    AvatarImage = draft.AvatarImage,
                    Proxy = draft.Proxy,
                    ForceProxy = draft.ForceProxy,
                    Tags = new List<string>(draft.Tags),
                    Logged = false,
                });
                candidateNext = checked(id + 1);
            }

            var candidate = accounts.Select(account => account.Clone()).ToList();
            var candidateCurrent = accounts.Count == 0 ? added[0].Id : currentId;
            prepare?.Invoke(added.AsReadOnly());
            // prepare 可以保留这些草稿的引用，发布前再次深拷贝，且导入不携带登录态。
            added = added.Select(account =>
            {
                var clone = account.Clone();
                clone.Logged = false;
                return clone;
            }).ToList();
            candidate.AddRange(added);
            Persist(candidate, candidateNext, candidateCurrent);
            return added.Select(account => account.Clone()).ToArray();
        }
    }

    public void Move(long id, long targetId, bool after)
    {
        lock (gate)
        {
            var candidate = accounts.Select(account => account.Clone()).ToList();
            var index = candidate.FindIndex(account => account.Id == id);
            var targetIndex = candidate.FindIndex(account => account.Id == targetId);
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(id));
            if (targetIndex < 0) throw new ArgumentOutOfRangeException(nameof(targetId));
            if (id == targetId) return;

            var insertionIndex = targetIndex + (after ? 1 : 0);
            if (index < insertionIndex) insertionIndex--;
            if (insertionIndex == index) return;

            var moved = candidate[index];
            candidate.RemoveAt(index);
            candidate.Insert(insertionIndex, moved);
            Persist(candidate, nextId, currentId);
        }
    }

    public void Update(Account updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        lock (gate)
        {
            var candidate = accounts.Select(account => account.Clone()).ToList();
            var index = candidate.FindIndex(account => account.Id == updated.Id);
            if (index < 0) throw new InvalidOperationException("账号已不存在");
            var replacement = updated.Clone();
            replacement.Logged = candidate[index].Logged;
            candidate[index] = replacement;
            Persist(candidate, nextId, currentId);
        }
    }

    public void Rename(long id, string name)
    {
        lock (gate)
        {
            var account = FindById(id);
            if (account == null) return;
            account.Name = name;
            Update(account);
        }
    }

    public void Remove(long id)
    {
        lock (gate)
        {
            if (!accounts.Any(account => account.Id == id)) return;
            var candidate = accounts.Where(account => account.Id != id).Select(account => account.Clone()).ToList();
            var selected = currentId == id ? candidate.FirstOrDefault()?.Id ?? -1 : currentId;
            Persist(candidate, nextId, selected);
        }
    }

    public void SetCurrent(long id)
    {
        lock (gate)
        {
            if (!accounts.Any(account => account.Id == id)) throw new ArgumentOutOfRangeException(nameof(id));
            if (id == currentId) return;
            prefs.PutLong("currentId", id);
            currentId = id;
        }
    }

    public void SetLogged(long id, bool logged)
    {
        lock (gate)
        {
            var account = accounts.FirstOrDefault(account => account.Id == id);
            if (account != null) account.Logged = logged;
        }
    }

    public bool IsFullscreen() { lock (gate) return prefs.GetBoolean("fullscreen", false); }
    public void SetFullscreen(bool value) { lock (gate) prefs.PutBoolean("fullscreen", value); }
}
