namespace MuseLauncher.Core;

/// <summary>键值存储抽象：内存实现供测试，文件实现供运行时（镜像安卓 KeyValueStore）。</summary>
public interface IKeyValueStore
{
    string? GetString(string key, string? defValue);
    long GetLong(string key, long defValue);
    bool GetBoolean(string key, bool defValue);
    void PutAll(IReadOnlyDictionary<string, object?> values);
    void PutString(string key, string? value);
    void PutLong(string key, long value);
    void PutBoolean(string key, bool value);
}
