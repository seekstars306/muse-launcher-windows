using System.Text.Encodings.Web;
using System.Text.Json;

namespace MuseLauncher.Core;

/// <summary>
/// 单文件 JSON 键值存储（对应安卓的 SharedPreferencesStore）。
/// 所有键值落在一个 JSON 对象里，原子写（临时文件 + 替换），损坏时当作空存储处理。
/// </summary>
public sealed class FileKeyValueStore : IKeyValueStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string filePath;
    private readonly object ioLock = new();
    private Dictionary<string, object?> data = new();

    public FileKeyValueStore(string filePath)
    {
        this.filePath = filePath;
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(filePath)) return;
            var json = File.ReadAllText(filePath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (parsed == null) return;
            var loaded = new Dictionary<string, object?>();
            foreach (var (key, el) in parsed)
            {
                loaded[key] = el.ValueKind switch
                {
                    JsonValueKind.String => el.GetString(),
                    JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => el.GetRawText(),
                };
            }
            data = loaded;
        }
        catch
        {
            // 文件损坏：当作空存储，后续保存会覆盖
            data = new Dictionary<string, object?>();
        }
    }

    private void Flush()
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, WriteOptions));
            if (File.Exists(filePath)) File.Replace(tmp, filePath, null);
            else File.Move(tmp, filePath);
        }
        catch
        {
            // 写盘失败不抛出（对齐安卓 apply 的尽力而为语义）
        }
    }

    public string? GetString(string key, string? defValue)
    {
        lock (ioLock)
        {
            return data.TryGetValue(key, out var v) && v is string s ? s : defValue;
        }
    }

    public long GetLong(string key, long defValue)
    {
        lock (ioLock)
        {
            if (!data.TryGetValue(key, out var v)) return defValue;
            return v switch
            {
                long l => l,
                double d => (long)d,
                _ => defValue,
            };
        }
    }

    public bool GetBoolean(string key, bool defValue)
    {
        lock (ioLock)
        {
            return data.TryGetValue(key, out var v) && v is bool b ? b : defValue;
        }
    }

    public void PutAll(IReadOnlyDictionary<string, object?> values)
    {
        lock (ioLock)
        {
            foreach (var (key, value) in values) data[key] = value;
            Flush();
        }
    }

    public void PutString(string key, string? value)
    {
        lock (ioLock)
        {
            data[key] = value;
            Flush();
        }
    }

    public void PutLong(string key, long value)
    {
        lock (ioLock)
        {
            data[key] = value;
            Flush();
        }
    }

    public void PutBoolean(string key, bool value)
    {
        lock (ioLock)
        {
            data[key] = value;
            Flush();
        }
    }
}
