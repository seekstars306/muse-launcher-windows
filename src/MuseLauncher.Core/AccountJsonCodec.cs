using System.Text.Encodings.Web;
using System.Text.Json;

namespace MuseLauncher.Core;

/// <summary>兼容旧账号数组，严格校验正数/唯一 ID，单条损坏留给调用方提示。</summary>
public static class AccountJsonCodec
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string? ToJson(IReadOnlyList<Account> accounts)
    {
        var ids = new HashSet<long>();
        var output = new List<Dictionary<string, object?>>();
        foreach (var account in accounts)
        {
            if (account.Id <= 0 || !ids.Add(account.Id) || string.IsNullOrWhiteSpace(account.Name))
                throw new InvalidDataException("账号 ID 或名称无效，未保存");
            output.Add(new Dictionary<string, object?>
            {
                ["id"] = account.Id,
                ["name"] = account.Name,
                ["avatar"] = account.Avatar,
                ["avatarImage"] = account.AvatarImage,
                ["proxy"] = account.Proxy,
                ["forceProxy"] = account.ForceProxy,
                ["tags"] = account.Tags,
            });
        }
        return JsonSerializer.Serialize(output, WriteOptions);
    }

    public static List<Account>? FromJsonOrNull(string? raw) => FromJsonOrNull(raw, out _);

    public static List<Account>? FromJsonOrNull(string? raw, out bool skippedEntries)
    {
        skippedEntries = false;
        if (raw == null) return null;
        JsonDocument document;
        try { document = JsonDocument.Parse(raw); }
        catch (JsonException) { return null; }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var result = new List<Account>();
            var ids = new HashSet<long>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                try
                {
                    var id = item.GetProperty("id").GetInt64();
                    var name = item.GetProperty("name").GetString();
                    if (id <= 0 || string.IsNullOrWhiteSpace(name) || !ids.Add(id))
                    {
                        skippedEntries = true;
                        continue;
                    }
                    var account = new Account(id, name);
                    if (item.TryGetProperty("avatar", out var avatar) && avatar.ValueKind == JsonValueKind.String)
                        account.Avatar = avatar.GetString() ?? "m";
                    if (item.TryGetProperty("avatarImage", out var avatarImage) && avatarImage.ValueKind == JsonValueKind.String)
                        account.AvatarImage = avatarImage.GetString() ?? "";
                    if (item.TryGetProperty("proxy", out var proxy) && proxy.ValueKind == JsonValueKind.String)
                        account.Proxy = proxy.GetString() ?? "";
                    if (item.TryGetProperty("forceProxy", out var force) && force.ValueKind == JsonValueKind.True)
                        account.ForceProxy = true;
                    if (item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tag in tags.EnumerateArray())
                            if (tag.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(tag.GetString()))
                                account.Tags.Add(tag.GetString()!);
                    }
                    result.Add(account);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                {
                    skippedEntries = true;
                }
            }
            return result;
        }
    }
}
