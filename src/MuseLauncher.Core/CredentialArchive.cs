using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MuseLauncher.Core;

/// <summary>Password-encrypted, bounded UTF-8 JSON; never reads account files or browser profiles.</summary>
public static class CredentialArchive
{
    public const int MaximumFileBytes = 64 * 1024 * 1024;
    public const int MaximumAccounts = 200;

    // V1: MUSECRED (8), version/u32, KDF/u32, iterations/u32, cipher/u32,
    // JSON byte length/u32, salt (16), nonce (12), ciphertext, GCM tag (16).
    // Integers are little-endian. The entire fixed 56-byte header is authenticated as AAD.
    // KDF 1 = PBKDF2-SHA256/600000/32; cipher 1 = AES-256-GCM/12-byte nonce/16-byte tag.
    private const int HeaderBytes = 56;
    private const int TagBytes = 16;
    private const int Iterations = 600_000;
    private const int MaximumPayloadBytes = MaximumFileBytes - HeaderBytes - TagBytes;
    private const int MaximumAvatarBytes = 5 * 1024 * 1024;
    private const int MaximumTags = 100;
    private const int MaximumCookiesPerAccount = 1000;
    private const int MaximumTotalCookies = 20_000;
    private const string AuthenticationError = "密码错误或文件损坏";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 8 };

    public static byte[] Encrypt(CredentialBundle bundle, string password)
    {
        ValidatePassword(password);
        var length = ValidateCore(bundle);
        var plaintext = new byte[length];
        Span<byte> key = stackalloc byte[32];
        try
        {
            using (var stream = new MemoryStream(plaintext, writable: true))
                SerializePayload(stream, bundle);

            var output = new byte[HeaderBytes + length + TagBytes];
            var header = output.AsSpan(0, HeaderBytes);
            "MUSECRED"u8.CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], 1);
            BinaryPrimitives.WriteInt32LittleEndian(header[12..], 1);
            BinaryPrimitives.WriteInt32LittleEndian(header[16..], Iterations);
            BinaryPrimitives.WriteInt32LittleEndian(header[20..], 1);
            BinaryPrimitives.WriteInt32LittleEndian(header[24..], length);
            RandomNumberGenerator.Fill(header.Slice(28, 16));
            RandomNumberGenerator.Fill(header.Slice(44, 12));
            Rfc2898DeriveBytes.Pbkdf2(password.AsSpan(), header.Slice(28, 16), key,
                Iterations, HashAlgorithmName.SHA256);
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(header.Slice(44, 12), plaintext, output.AsSpan(HeaderBytes, length),
                output.AsSpan(HeaderBytes + length, TagBytes), header);
            return output;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            // In particular, never forward serializer exceptions or their inner exceptions.
            throw InvalidContent();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static CredentialBundle Decrypt(byte[] bytes, string password)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ValidatePassword(password);
        // Do not allocate a plaintext buffer or run any KDF before checking the entire envelope.
        if (bytes.Length is <= HeaderBytes + TagBytes or > MaximumFileBytes)
            throw AuthenticationFailed();
        var header = bytes.AsSpan(0, HeaderBytes);
        if (!header[..8].SequenceEqual("MUSECRED"u8)) throw AuthenticationFailed();
        if (BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != 1)
            throw UnsupportedVersion();
        if (BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != 1 ||
            BinaryPrimitives.ReadInt32LittleEndian(header[16..]) != Iterations ||
            BinaryPrimitives.ReadInt32LittleEndian(header[20..]) != 1)
            throw AuthenticationFailed();
        var length = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
        if (length <= 0 || length > MaximumPayloadBytes || length != bytes.Length - HeaderBytes - TagBytes)
            throw AuthenticationFailed();

        var plaintext = new byte[length];
        Span<byte> key = stackalloc byte[32];
        try
        {
            try
            {
                Rfc2898DeriveBytes.Pbkdf2(password.AsSpan(), header.Slice(28, 16), key,
                    Iterations, HashAlgorithmName.SHA256);
                using var aes = new AesGcm(key, TagBytes);
                aes.Decrypt(header.Slice(44, 12), bytes.AsSpan(HeaderBytes, length),
                    bytes.AsSpan(HeaderBytes + length, TagBytes), plaintext, header);
            }
            catch (CryptographicException) { throw AuthenticationFailed(); }

            var bundle = ReadBundle(plaintext);
            ValidateCore(bundle);
            return bundle;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static void Validate(CredentialBundle bundle) => ValidateCore(bundle);

    private static int ValidateCore(CredentialBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.Version != 1) throw UnsupportedVersion();
        if (bundle.Accounts is null || bundle.Accounts.Count > MaximumAccounts) throw InvalidContent();
        var totalCookies = 0;
        long totalAvatarBytes = 0;
        foreach (var account in bundle.Accounts)
        {
            if (account is null) throw InvalidContent();
            CheckText(account.Name, 1024);
            CheckText(account.Avatar, 128);
            CheckText(account.Proxy, 8192);
            try { _ = ProxyConfiguration.Parse(account.Proxy); }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw new InvalidDataException("账号代理配置无效");
            }
            if (account.Tags is null || account.Tags.Count > MaximumTags) throw InvalidContent();
            foreach (var tag in account.Tags) CheckText(tag, 256);
            CheckText(account.AvatarExtension, 5);
            if (account.AvatarImage is null)
            {
                if (account.AvatarExtension.Length != 0) throw InvalidContent();
            }
            else
            {
                if (account.AvatarImage.Length is 0 or > MaximumAvatarBytes ||
                    !(account.AvatarExtension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif"))
                    throw InvalidContent();
                totalAvatarBytes += account.AvatarImage.Length;
                if ((totalAvatarBytes + 2) / 3 * 4 > MaximumPayloadBytes) throw InvalidContent();
            }
            if (account.Cookies is null || account.Cookies.Count > MaximumCookiesPerAccount ||
                (!account.CookiesCaptured && account.Cookies.Count != 0)) throw InvalidContent();
            totalCookies += account.Cookies.Count;
            if (totalCookies > MaximumTotalCookies) throw InvalidContent();
            var identities = new HashSet<(string Domain, string Path, string Name)>();
            foreach (var cookie in account.Cookies)
            {
                if (cookie is null) throw InvalidContent();
                CheckText(cookie.Name, 256);
                CheckText(cookie.Value, 16_384);
                CheckText(cookie.Domain, 253);
                CheckText(cookie.Path, 2048);
                CheckText(cookie.SameSite, 6);
                if (cookie.Name.Length == 0 || cookie.Name.Any(c =>
                        !(char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c))) ||
                    !cookie.Path.StartsWith('/') || cookie.Path.Contains(';') ||
                    !(cookie.SameSite is "None" or "Lax" or "Strict")) throw InvalidContent();
                var domain = CookieDomain(cookie.Domain);
                if (!identities.Add((domain, cookie.Path, cookie.Name))) throw InvalidContent();
                if (cookie.IsSession)
                {
                    if (cookie.ExpiresUnixSeconds is not null) throw InvalidContent();
                }
                else if (cookie.ExpiresUnixSeconds is not double expiry || !double.IsFinite(expiry) ||
                         expiry < -62_135_596_800d || expiry >= 253_402_300_800d)
                    throw InvalidContent();
            }
        }
        // Count the actual JSON (including escaping and base64) without retaining another plaintext copy.
        // This also bounds combined avatars/strings before allocating the encryption buffers or deriving a key.
        using var counter = new PayloadCounter();
        try { SerializePayload(counter, bundle); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            throw InvalidContent();
        }
        return counter.Count;
    }

    private static string CookieDomain(string value)
    {
        var domain = value.StartsWith('.') ? value[1..] : value;
        if (!(domain.Equals("muse.ai", StringComparison.OrdinalIgnoreCase) ||
              domain.EndsWith(".muse.ai", StringComparison.OrdinalIgnoreCase))) throw InvalidContent();
        foreach (var label in domain.Split('.'))
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))) throw InvalidContent();
        // The leading dot distinguishes a domain cookie from a host-only cookie.
        // Validate the bare DNS name above, but retain the scope in the identity.
        return value.ToLowerInvariant();
    }

    private static void CheckText(string? value, int maximum)
    {
        if (value is null || value.Length > maximum || value.Any(char.IsControl)) throw InvalidContent();
        try { _ = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw InvalidContent(); }
    }

    private static void ValidatePassword(string password)
    {
        if (password is null || password.Length is 0 or > 1024)
            throw new ArgumentException("密码不能为空且不能超过 1024 个字符", nameof(password));
        // Whitespace and Unicode are significant; never trim or normalize the password.
        try { _ = StrictUtf8.GetByteCount(password); }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("密码包含无效字符", nameof(password));
        }
    }

    private static InvalidDataException InvalidContent() => new("账号归档内容无效或超出限制");
    private static InvalidDataException AuthenticationFailed() => new(AuthenticationError);
    private static NotSupportedException UnsupportedVersion() => new("不支持此账号归档版本");

    private static void SerializePayload(Stream stream, CredentialBundle bundle)
    {
        using var buffer = new ClearingBufferWriter(stream);
        using var writer = new Utf8JsonWriter(buffer);
        JsonSerializer.Serialize(writer, bundle, JsonOptions);
        writer.Flush();
        buffer.Flush();
    }

    // Utf8JsonWriter otherwise owns a pooled plaintext buffer. Own and wipe its backing memory,
    // including on overflow/serialization failure; use a bounded scratch buffer for base64/escaping.
    private sealed class ClearingBufferWriter(Stream stream) : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = new byte[16 * 1024];
        private int _written;

        public void Advance(int count)
        {
            if (count < 0 || count > _buffer.Length - _written) throw InvalidContent();
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void Ensure(int sizeHint)
        {
            if (sizeHint < 0 || sizeHint > MaximumPayloadBytes) throw InvalidContent();
            sizeHint = Math.Max(1, sizeHint);
            if (_buffer.Length - _written >= sizeHint) return;
            Flush();
            if (_buffer.Length >= sizeHint) return;
            var replacement = new byte[Math.Max(sizeHint, Math.Min(MaximumPayloadBytes, _buffer.Length * 2))];
            CryptographicOperations.ZeroMemory(_buffer);
            _buffer = replacement;
        }

        public void Flush()
        {
            if (_written == 0) return;
            stream.Write(_buffer.AsSpan(0, _written));
            CryptographicOperations.ZeroMemory(_buffer.AsSpan(0, _written));
            _written = 0;
        }

        public void Dispose() => CryptographicOperations.ZeroMemory(_buffer);
    }

    private sealed class PayloadCounter : Stream
    {
        public int Count { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > MaximumPayloadBytes - Count) throw InvalidContent();
            Count += buffer.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // Read the schema directly instead of deserializing unchecked lists/base64 or accepting last-key-wins JSON.
    private static readonly string[] BundleFields = ["Version", "CreatedUtc", "Accounts"];
    private static readonly string[] AccountFields =
        ["Name", "Avatar", "Proxy", "ForceProxy", "Tags", "AvatarImage", "AvatarExtension", "CookiesCaptured", "Cookies"];
    private static readonly string[] CookieFields =
        ["Name", "Value", "Domain", "Path", "IsSecure", "IsHttpOnly", "IsSession", "SameSite", "ExpiresUnixSeconds"];

    private static CredentialBundle ReadBundle(byte[] plaintext)
    {
        try
        {
            // The JSON reader can skip unmaterialized text (for example a property name).
            // Reject invalid UTF-8 throughout the payload, not only in decoded string values.
            _ = StrictUtf8.GetCharCount(plaintext);
            var reader = new Utf8JsonReader(plaintext, new JsonReaderOptions { MaxDepth = 8 });
            if (!reader.Read()) throw InvalidContent();
            Require(ref reader, JsonTokenType.StartObject);
            var bundle = new CredentialBundle();
            var seen = 0;
            var totalCookies = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                switch (Property(ref reader, BundleFields, ref seen))
                {
                    case 0:
                        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var version))
                            throw InvalidContent();
                        if (version != 1) throw UnsupportedVersion();
                        bundle.Version = version;
                        break;
                    case 1:
                        if (reader.TokenType != JsonTokenType.String || !reader.TryGetDateTimeOffset(out var created))
                            throw InvalidContent();
                        bundle.CreatedUtc = created;
                        break;
                    case 2:
                        Require(ref reader, JsonTokenType.StartArray);
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (bundle.Accounts.Count >= MaximumAccounts) throw InvalidContent();
                            bundle.Accounts.Add(ReadAccount(ref reader, ref totalCookies));
                        }
                        Require(ref reader, JsonTokenType.EndArray);
                        break;
                }
            }
            Require(ref reader, JsonTokenType.EndObject);
            if (seen != (1 << BundleFields.Length) - 1 || reader.Read()) throw InvalidContent();
            return bundle;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            // JSON errors may contain input text. Expose neither their messages nor their inner exceptions.
            throw InvalidContent();
        }
    }

    private static CredentialAccount ReadAccount(ref Utf8JsonReader reader, ref int totalCookies)
    {
        Require(ref reader, JsonTokenType.StartObject);
        var account = new CredentialAccount();
        var seen = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            switch (Property(ref reader, AccountFields, ref seen))
            {
                case 0: account.Name = Text(ref reader, 1024); break;
                case 1: account.Avatar = Text(ref reader, 128); break;
                case 2: account.Proxy = Text(ref reader, 8192); break;
                case 3: account.ForceProxy = Boolean(ref reader); break;
                case 4:
                    Require(ref reader, JsonTokenType.StartArray);
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (account.Tags.Count >= MaximumTags) throw InvalidContent();
                        account.Tags.Add(Text(ref reader, 256));
                    }
                    Require(ref reader, JsonTokenType.EndArray);
                    break;
                case 5:
                    if (reader.TokenType == JsonTokenType.Null) break;
                    Require(ref reader, JsonTokenType.String);
                    if (reader.ValueSpan.Length > ((MaximumAvatarBytes + 2) / 3 * 4) ||
                        !reader.TryGetBytesFromBase64(out var image) || image.Length > MaximumAvatarBytes)
                        throw InvalidContent();
                    account.AvatarImage = image;
                    break;
                case 6: account.AvatarExtension = Text(ref reader, 5); break;
                case 7: account.CookiesCaptured = Boolean(ref reader); break;
                case 8:
                    Require(ref reader, JsonTokenType.StartArray);
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (account.Cookies.Count >= MaximumCookiesPerAccount || ++totalCookies > MaximumTotalCookies)
                            throw InvalidContent();
                        account.Cookies.Add(ReadCookie(ref reader));
                    }
                    Require(ref reader, JsonTokenType.EndArray);
                    break;
            }
        }
        Require(ref reader, JsonTokenType.EndObject);
        if (seen != (1 << AccountFields.Length) - 1) throw InvalidContent();
        return account;
    }

    private static CredentialCookie ReadCookie(ref Utf8JsonReader reader)
    {
        Require(ref reader, JsonTokenType.StartObject);
        var cookie = new CredentialCookie();
        var seen = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            switch (Property(ref reader, CookieFields, ref seen))
            {
                case 0: cookie.Name = Text(ref reader, 256); break;
                case 1: cookie.Value = Text(ref reader, 16_384); break;
                case 2: cookie.Domain = Text(ref reader, 253); break;
                case 3: cookie.Path = Text(ref reader, 2048); break;
                case 4: cookie.IsSecure = Boolean(ref reader); break;
                case 5: cookie.IsHttpOnly = Boolean(ref reader); break;
                case 6: cookie.IsSession = Boolean(ref reader); break;
                case 7: cookie.SameSite = Text(ref reader, 6); break;
                case 8:
                    if (reader.TokenType == JsonTokenType.Null) break;
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out var expiry))
                        throw InvalidContent();
                    cookie.ExpiresUnixSeconds = expiry;
                    break;
            }
        }
        Require(ref reader, JsonTokenType.EndObject);
        if (seen != (1 << CookieFields.Length) - 1) throw InvalidContent();
        return cookie;
    }

    private static int Property(ref Utf8JsonReader reader, string[] fields, ref int seen)
    {
        Require(ref reader, JsonTokenType.PropertyName);
        for (var index = 0; index < fields.Length; index++)
        {
            if (!reader.ValueTextEquals(fields[index])) continue;
            var bit = 1 << index;
            if ((seen & bit) != 0 || !reader.Read()) throw InvalidContent();
            seen |= bit;
            return index;
        }
        throw InvalidContent();
    }

    private static string Text(ref Utf8JsonReader reader, int maximum)
    {
        Require(ref reader, JsonTokenType.String);
        // An escaped UTF-16 code unit takes at most six bytes. Bound allocation before GetString().
        if (reader.ValueSpan.Length > maximum * 6) throw InvalidContent();
        var text = reader.GetString()!;
        CheckText(text, maximum);
        return text;
    }

    private static bool Boolean(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.True => true,
        JsonTokenType.False => false,
        _ => throw InvalidContent(),
    };

    private static void Require(ref Utf8JsonReader reader, JsonTokenType type)
    {
        if (reader.TokenType != type) throw InvalidContent();
    }
}
