namespace MuseLauncher.Core;

/// <summary>
/// 按账号 id 保存重资源的注册表（WebView 等），带 LRU 容量上限：
/// TouchAndEvict 把指定 id 标记为最近使用，超过容量时从最旧开始驱逐。
/// 与安卓版逐行对齐；驱逐决策与资源销毁（WPF 侧）解耦。
/// </summary>
public class LruAccountRegistry<T> where T : class
{
    private readonly int maxAlive;
    private readonly Dictionary<long, T> map = new();
    private readonly LinkedList<long> order = new(); // First = 最久未用
    private readonly Dictionary<long, LinkedListNode<long>> nodes = new();

    public LruAccountRegistry(int maxAlive)
    {
        this.maxAlive = Math.Max(1, maxAlive);
    }

    public T? Get(long id)
    {
        if (!map.TryGetValue(id, out var v)) return null;
        MoveToNewest(id);
        return v;
    }

    public void Put(long id, T value)
    {
        if (map.ContainsKey(id))
        {
            map[id] = value;
            MoveToNewest(id);
        }
        else
        {
            map[id] = value;
            var node = new LinkedListNode<long>(id);
            order.AddLast(node);
            nodes[id] = node;
        }
    }

    public T? Remove(long id)
    {
        if (!map.TryGetValue(id, out var v)) return null;
        map.Remove(id);
        if (nodes.Remove(id, out var node)) order.Remove(node);
        return v;
    }

    public bool Contains(long id) => map.ContainsKey(id);

    public int Size => map.Count;

    /// <summary>副本，供遍历时安全使用（例如在 WPF 侧做可见性切换 / 崩溃匹配）。</summary>
    public IReadOnlyDictionary<long, T> Snapshot() => new Dictionary<long, T>(map);

    /// <summary>
    /// 放入（或刷新）id 对应的资源并标记为最近使用；若总量超出 maxAlive，
    /// 从最久未用的开始返回应驱逐的 id。返回列表永远不包含 id 本身。
    /// </summary>
    public List<long> TouchAndEvict(long id, T value)
    {
        Put(id, value); // put 同时刷新为最新
        var evict = new List<long>();
        while (map.Count > maxAlive)
        {
            var eldest = order.First;
            if (eldest == null) break;
            if (eldest.Value == id) break; // 理论不可达（刚 put 的 id 最新），防御性保护
            evict.Add(eldest.Value);
            Remove(eldest.Value);
        }
        return evict;
    }

    private void MoveToNewest(long id)
    {
        if (nodes.Remove(id, out var node))
        {
            order.Remove(node);
            order.AddLast(node);
            nodes[id] = node;
        }
    }
}
