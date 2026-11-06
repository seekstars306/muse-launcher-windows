using System.Text.Encodings.Web;
using System.Text.Json;

namespace MuseLauncher.Core;

/// <summary>
/// 账号列表 JSON 编解码。读取对损坏数据有容错：
/// 顶层不是 JSON 数组返回 null（由调用方走备份恢复），单条损坏只跳过该条。
/// </summary>
public static class AccountJsonCodec
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        // 中文/emoji 原样输出，accounts.json 便于人工查看
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>序列化失败（理论上不会发生）返回 null，调用方应跳过本次写盘以保留旧数据</summary>
    public static string? ToJson(IReadOnlyList<Account> accounts)
    {
        try
        {
            var arr = new List<Dictionary<string, object?>>(accounts.Count);
            foreach (var a in accounts)
            {
                arr.Add(new Dictionary<string, object?>
                {
                    ["id"] = a.Id,
                    ["name"] = a.Name,
                    ["avatar"] = a.Avatar,
                    ["proxy"] = a.Proxy,
                    ["tags"] = a.Tags,
                });
            }
            return JsonSerializer.Serialize(arr, WriteOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析账号列表。
    /// 顶层不是合法 JSON 数组时返回 null（由调用方走备份恢复）；
    /// 数组合法但某一条目字段损坏时跳过该条目，其余照常返回。
    /// </summary>
    public static List<Account>? FromJsonOrNull(string? raw)
    {
        if (raw == null) return null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch
        {
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var output = new List<Account>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                try
                {
                    long id = el.GetProperty("id").GetInt64();
                    string name = el.GetProperty("name").GetString()
                        ?? throw new JsonException();
                    var a = new Account(id, name);
                    if (el.TryGetProperty("avatar", out var avatar) && avatar.ValueKind == JsonValueKind.String)
                        a.Avatar = avatar.GetString() ?? "m";
                    if (el.TryGetProperty("proxy", out var proxy) && proxy.ValueKind == JsonValueKind.String)
                        a.Proxy = proxy.GetString() ?? "";
                    if (el.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in tags.EnumerateArray())
                        {
                            var s = t.GetString();
                            if (!string.IsNullOrEmpty(s)) a.Tags.Add(s);
                        }
                    }
                    output.Add(a);
                }
                catch
                {
                    // 单条损坏不影响其余条目
                }
            }
            return output;
        }
    }
}
