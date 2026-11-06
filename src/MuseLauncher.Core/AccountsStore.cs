namespace MuseLauncher.Core;

/// <summary>
/// 账号元数据持久化。各账号的登录数据由 WebView2 用户数据目录负责持久化。
///
/// 防损设计（与安卓版一致）：
/// - 每次成功保存前，把上一次的 list 值滚动备份到 list_prev；
/// - list 解析失败时：原始值留底到 list_corrupt（可人工找回），并尝试从 list_prev 自愈；
/// - 单条账号数据损坏只跳过该条，不影响其余；
/// - loadNotice 通过 TakeLoadNotice() 上抛给 UI 提示用户。
/// </summary>
public class AccountsStore
{
    private readonly object gate = new();
    private readonly IKeyValueStore prefs;
    private readonly List<Account> accounts = new();
    private long nextId = 1;
    private long currentId = -1;
    private string? loadNotice;

    public AccountsStore(IKeyValueStore store)
    {
        prefs = store;
        Load();
    }

    public void Reload()
    {
        lock (gate) Load();
    }

    private void Load()
    {
        loadNotice = null;
        accounts.Clear();
        var parsed = AccountJsonCodec.FromJsonOrNull(prefs.GetString("list", "[]"));
        if (parsed == null)
        {
            var raw = prefs.GetString("list", "");
            if (!string.IsNullOrEmpty(raw) && raw != "[]")
            {
                // 原始数据留底，避免被后续保存彻底覆盖，人工可找回
                prefs.PutString("list_corrupt", raw);
                loadNotice = "corrupt";
            }
            // 尝试从上一次保存前的备份自愈
            var recovered = AccountJsonCodec.FromJsonOrNull(prefs.GetString("list_prev", null));
            if (recovered != null && recovered.Count > 0)
            {
                accounts.AddRange(recovered);
                loadNotice = "recovered";
                // 只写回 list 主键。此时 nextId/currentId 还没从磁盘读入，
                // 绝不能走完整 Save()（会用字段默认值 nextId=1 覆盖真实值，造成 id 冲突）
                var json = AccountJsonCodec.ToJson(accounts);
                if (json != null) prefs.PutString("list", json);
            }
        }
        else
        {
            accounts.AddRange(parsed);
        }
        nextId = prefs.GetLong("nextId", 1);
        currentId = prefs.GetLong("currentId", -1);
        if (accounts.Count == 0)
        {
            currentId = -1;
        }
        else if (FindById(currentId) == null)
        {
            currentId = accounts[0].Id;
        }
    }

    public void Save()
    {
        try
        {
            var json = AccountJsonCodec.ToJson(accounts);
            if (json == null) return; // 序列化异常：跳过本次写盘，保留旧数据
            var vals = new Dictionary<string, object?>();
            var prevList = prefs.GetString("list", null);
            if (prevList != null) vals["list_prev"] = prevList; // 滚动备份
            vals["list"] = json;
            vals["nextId"] = nextId;
            vals["currentId"] = currentId;
            prefs.PutAll(vals);
        }
        catch
        {
            // 写盘失败保留旧数据
        }
    }

    /// <summary>
    /// 取走本次加载的结果提示：null=正常，"recovered"=已从备份自愈，"corrupt"=损坏已留底。
    /// 取走后清空，避免重复提示。
    /// </summary>
    public string? TakeLoadNotice()
    {
        lock (gate)
        {
            var n = loadNotice;
            loadNotice = null;
            return n;
        }
    }

    public IReadOnlyList<Account> All()
    {
        lock (gate) return accounts;
    }

    public Account? FindById(long id)
    {
        lock (gate)
        {
            foreach (var a in accounts)
            {
                if (a.Id == id) return a;
            }
            return null;
        }
    }

    public long GetCurrentId()
    {
        lock (gate) return currentId;
    }

    /// <summary>新建账号，默认名 "Muse N"（N 与账号 id 一致）。</summary>
    public Account AddDefault()
    {
        lock (gate)
        {
            var a = new Account(nextId, "Muse " + nextId);
            nextId++;
            accounts.Add(a);
            currentId = a.Id;
            Save();
            return a;
        }
    }

    /// <summary>
    /// 新建账号并继承指定代理（国内直连 muse.ai 不通，新环境沿用可用网络路径），返回新账号。
    /// </summary>
    public Account AddDefault(string? inheritProxy)
    {
        var a = AddDefault();
        if (!string.IsNullOrEmpty(inheritProxy))
        {
            lock (gate)
            {
                a.Proxy = inheritProxy;
                Save();
            }
        }
        return a;
    }

    public void Rename(long id, string name)
    {
        lock (gate)
        {
            var a = FindById(id);
            if (a != null)
            {
                a.Name = name;
                Save();
            }
        }
    }

    public void Remove(long id)
    {
        lock (gate)
        {
            var a = FindById(id);
            if (a != null) accounts.Remove(a);
            if (currentId == id)
            {
                currentId = accounts.Count == 0 ? -1 : accounts[0].Id;
            }
            Save();
        }
    }

    public void SetCurrent(long id)
    {
        lock (gate)
        {
            if (currentId == id) return; // 无变化不写盘
            currentId = id;
            // 与安卓一致：只写 currentId 独立键，不全量序列化账号列表（切换零开销）
            prefs.PutLong("currentId", id);
        }
    }

    public bool IsFullscreen()
    {
        lock (gate) return prefs.GetBoolean("fullscreen", false);
    }

    public void SetFullscreen(bool v)
    {
        lock (gate) prefs.PutBoolean("fullscreen", v);
    }
}
