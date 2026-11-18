using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MuseLauncher.Core;
using Xunit;

namespace MuseLauncher.Core.Tests;

public class CredentialArchiveTest
{
    private const string Passphrase = "  归档🔑 password  ";
    private const int HeaderBytes = 56;
    private static readonly Lazy<byte[]> SampleArchive = new(() => CredentialArchive.Encrypt(Sample(), Passphrase));
    private static readonly Lazy<byte[]> OversizeFile = new(() => new byte[CredentialArchive.MaximumFileBytes + 1]);

    private static CredentialBundle Sample() => new()
    {
        CreatedUtc = new DateTimeOffset(2025, 3, 2, 1, 4, 5, TimeSpan.Zero),
        Accounts =
        [
            new()
            {
                Name = "测试账号 😊", Avatar = "头像", ForceProxy = true,
                Proxy = "socks5://synthetic%40user:" + Uri.EscapeDataString("synthetic-token") + "@proxy.invalid:1080",
                Tags = ["主力", "标签 🔑"], AvatarImage = [0x89, 0x50, 0x4e, 0x47, 0, 0xff],
                AvatarExtension = ".png", CookiesCaptured = true,
                Cookies =
                [
                    new()
                    {
                        Name = "MuseCookie", Value = "synthetic-cookie-value-only", Domain = ".muse.ai",
                        Path = "/", IsSecure = true, IsHttpOnly = true, SameSite = "None",
                        ExpiresUnixSeconds = 1_900_000_000.125,
                    },
                    new()
                    {
                        Name = "session", Value = "synthetic-session", Domain = "app.muse.ai", Path = "/account",
                        IsSession = true, SameSite = "Strict",
                    },
                ],
            },
            new() { Name = "未捕获账号", Avatar = "m", Proxy = "", CookiesCaptured = false },
        ],
    };

    [Fact]
    public void RoundTrip_PreservesEveryPortableFieldAndDoesNotIncludePlaintext()
    {
        var expected = Sample();
        var archive = CredentialArchive.Encrypt(expected, Passphrase);
        var restored = CredentialArchive.Decrypt(archive, Passphrase);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(restored));
        Assert.False(archive.AsSpan().IndexOf(Encoding.UTF8.GetBytes(expected.Accounts[0].Proxy)) >= 0);
        Assert.False(archive.AsSpan().IndexOf(Encoding.UTF8.GetBytes(expected.Accounts[0].Cookies[0].Value)) >= 0);
        Assert.Equal(2, restored.Accounts.Count);
        Assert.Null(restored.Accounts[1].AvatarImage);
        Assert.Null(restored.Accounts[0].Cookies[1].ExpiresUnixSeconds);
    }

    [Fact]
    public void Export_UsesDocumentedV1EnvelopeAndCanBeOpenedWithStandardCrypto()
    {
        var archive = SampleArchive.Value;
        Assert.Equal("MUSECRED", Encoding.ASCII.GetString(archive, 0, 8));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(8)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(12)));
        Assert.Equal(600_000, BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(16)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(20)));
        var length = BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(24));
        Assert.Equal(archive.Length - 72, length);
        var key = Rfc2898DeriveBytes.Pbkdf2(Passphrase, archive.AsSpan(28, 16), 600_000,
            HashAlgorithmName.SHA256, 32);
        var plaintext = new byte[length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(archive.AsSpan(44, 12), archive.AsSpan(56, length), archive.AsSpan(56 + length, 16),
                plaintext, archive.AsSpan(0, 56));
            using var json = JsonDocument.Parse(plaintext);
            Assert.Equal("MuseCookie", json.RootElement.GetProperty("Accounts")[0].GetProperty("Cookies")[0]
                .GetProperty("Name").GetString());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    [Fact]
    public void SameDataAndPassword_UsesFreshSaltAndNonce()
    {
        var first = SampleArchive.Value;
        var second = CredentialArchive.Encrypt(Sample(), Passphrase);
        Assert.False(first.AsSpan(28, 16).SequenceEqual(second.AsSpan(28, 16)));
        Assert.False(first.AsSpan(44, 12).SequenceEqual(second.AsSpan(44, 12)));
        Assert.False(first.AsSpan().SequenceEqual(second));
        Assert.Equal("测试账号 😊", CredentialArchive.Decrypt(second, Passphrase).Accounts[0].Name);
    }

    [Fact]
    public void UnicodePassword_PreservesLeadingAndTrailingSpaces()
    {
        AssertAuthenticationFailure(SampleArchive.Value, Passphrase.Trim());
        Assert.Equal("测试账号 😊", CredentialArchive.Decrypt(SampleArchive.Value, Passphrase).Accounts[0].Name);
    }

    [Fact]
    public void SpacesOnlyPassword_IsNotTrimmedByArchiveLayer()
    {
        var archive = CredentialArchive.Encrypt(new CredentialBundle(), " ");
        Assert.Empty(CredentialArchive.Decrypt(archive, " ").Accounts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyPassword_IsRejectedWithoutSensitiveException(string? passphrase)
    {
        Assert.Null(Assert.Throws<ArgumentException>(() => CredentialArchive.Encrypt(Sample(), passphrase!)).InnerException);
        Assert.Null(Assert.Throws<ArgumentException>(() => CredentialArchive.Decrypt(SampleArchive.Value, passphrase!)).InnerException);
    }

    [Fact]
    public void OverlongPassword_IsRejectedWithoutEchoingIt()
    {
        var passphrase = new string('x', 1025);
        var error = Assert.Throws<ArgumentException>(() => CredentialArchive.Encrypt(Sample(), passphrase));
        Assert.DoesNotContain(passphrase, error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void WrongPasswordAndAuthenticationTampering_ProduceIdenticalRedactedError()
    {
        AssertAuthenticationFailure(SampleArchive.Value, "synthetic-wrong-key");
        foreach (var offset in new[] { 28, 44, HeaderBytes, SampleArchive.Value.Length - 1 })
        {
            var changed = SampleArchive.Value.ToArray();
            changed[offset] ^= 1;
            AssertAuthenticationFailure(changed, Passphrase);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(55)]
    [InlineData(72)]
    public void TruncatedHeaderOrTag_IsRejected(int length) =>
        AssertAuthenticationFailure(SampleArchive.Value[..length], Passphrase);

    [Fact]
    public void TruncatedPayloadOrExtraBytes_AreRejected()
    {
        AssertAuthenticationFailure(SampleArchive.Value[..^1], Passphrase);
        AssertAuthenticationFailure([.. SampleArchive.Value, 0], Passphrase);
    }

    [Theory]
    [InlineData(12, 2)]
    [InlineData(16, 0)]
    [InlineData(16, 599999)]
    [InlineData(16, int.MaxValue)]
    [InlineData(20, 2)]
    [InlineData(24, -1)]
    [InlineData(24, int.MaxValue)]
    public void HeaderKdfCipherAndLengthParameters_AreRejectedBeforeKdf(int offset, int value)
    {
        // int.MaxValue iterations must never cause a KDF delay or an unbounded allocation.
        var changed = SampleArchive.Value.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(changed.AsSpan(offset), value);
        AssertAuthenticationFailure(changed, Passphrase);
    }

    [Fact]
    public void ChangedMagic_IsRejected()
    {
        var changed = SampleArchive.Value.ToArray();
        changed[0] ^= 1;
        AssertAuthenticationFailure(changed, Passphrase);
    }

    [Fact]
    public void UnknownEnvelopeAndBundleVersion_AreReportedSeparately()
    {
        var changed = SampleArchive.Value.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(changed.AsSpan(8), 2);
        Assert.Null(Assert.Throws<NotSupportedException>(() => CredentialArchive.Decrypt(changed, Passphrase)).InnerException);
        Assert.Throws<NotSupportedException>(() => CredentialArchive.Validate(new() { Version = 2 }));
        Assert.Throws<NotSupportedException>(() => CredentialArchive.Encrypt(new() { Version = 0 }, Passphrase));
        var json = JsonSerializer.Serialize(Sample()).Replace("\"Version\":1", "\"Version\":2");
        Assert.Throws<NotSupportedException>(() => CredentialArchive.Decrypt(SealJson(json), Passphrase));
    }

    [Fact]
    public void FileLimit_IsCheckedBeforeUnknownVersionOrKdf()
    {
        Assert.Equal(64 * 1024 * 1024, CredentialArchive.MaximumFileBytes);
        Assert.Equal(200, CredentialArchive.MaximumAccounts);
        var oversized = OversizeFile.Value;
        "MUSECRED"u8.CopyTo(oversized);
        BinaryPrimitives.WriteInt32LittleEndian(oversized.AsSpan(8), 2);
        BinaryPrimitives.WriteInt32LittleEndian(oversized.AsSpan(16), int.MaxValue);
        // Size wins over even the separately reported unsupported version.
        AssertAuthenticationFailure(oversized, Passphrase);
    }

    [Fact]
    public void Import_AcceptsIndependentlyEncryptedUtf8Json()
    {
        var json = JsonSerializer.Serialize(Sample());
        var bundle = CredentialArchive.Decrypt(SealJson(json), Passphrase);
        Assert.Equal(json, JsonSerializer.Serialize(bundle));
    }

    [Theory]
    [InlineData("bundle")]
    [InlineData("escaped-bundle")]
    [InlineData("account")]
    [InlineData("cookie")]
    [InlineData("trailing-object")]
    [InlineData("trailing-text")]
    [InlineData("unknown-property")]
    [InlineData("missing-property")]
    [InlineData("null-string")]
    [InlineData("null-array")]
    [InlineData("null-element")]
    [InlineData("wrong-type")]
    public void AuthenticatedJson_RejectsAmbiguityExtraContentNullAndWrongTypes(string variant)
    {
        var json = JsonSerializer.Serialize(Sample());
        json = variant switch
        {
            "bundle" => json.Replace("\"Version\":1", "\"Version\":1,\"Version\":1"),
            "escaped-bundle" => json.Replace("\"Version\":1", "\"Version\":1,\"\\u0056ersion\":1"),
            "account" => json.Replace("\"ForceProxy\":true", "\"ForceProxy\":true,\"ForceProxy\":false"),
            "cookie" => json.Replace("\"SameSite\":\"None\"", "\"SameSite\":\"None\",\"SameSite\":\"Lax\""),
            "trailing-object" => json + "{}",
            "trailing-text" => json + "synthetic-secret-text",
            "unknown-property" => json.Insert(1, "\"Unexpected\":true,"),
            "missing-property" => json.Replace("\"Version\":1,", ""),
            "null-string" => json.Replace("\"Avatar\":\"m\"", "\"Avatar\":null"),
            "null-array" => json.Replace("\"Tags\":[]", "\"Tags\":null"),
            "null-element" => json.Replace("\"Tags\":[]", "\"Tags\":[null]"),
            _ => json.Replace("\"ForceProxy\":true", "\"ForceProxy\":\"synthetic-secret-text\""),
        };
        var error = Assert.Throws<InvalidDataException>(() => CredentialArchive.Decrypt(SealJson(json), Passphrase));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic-secret", error.ToString());
        Assert.DoesNotContain("synthetic-cookie", error.ToString());
    }

    [Theory]
    [InlineData("muse.ai")]
    [InlineData(".muse.ai")]
    [InlineData(".MUSE.AI")]
    [InlineData("APP.MUSE.AI")]
    [InlineData(".APP.MUSE.AI")]
    [InlineData("api-v2.muse.ai")]
    [InlineData(".api.app.muse.ai")]
    public void CookieDomain_AcceptsOnlyMuseHostOrRealSubdomains(string domain)
    {
        var bundle = Sample();
        bundle.Accounts[0].Cookies[0].Domain = domain;
        CredentialArchive.Validate(bundle);
    }

    [Theory]
    [InlineData("notmuse.ai")]
    [InlineData("evilmuse.ai")]
    [InlineData(".evilmuse.ai")]
    [InlineData("muse.ai.evil.invalid")]
    [InlineData(".muse.ai.evil.invalid")]
    [InlineData("muse.ai.")]
    [InlineData(".muse.ai.")]
    [InlineData("..muse.ai")]
    [InlineData("bad..muse.ai")]
    [InlineData(".bad..muse.ai")]
    [InlineData("https://muse.ai")]
    [InlineData("evil/muse.ai")]
    [InlineData("-bad.muse.ai")]
    [InlineData(".bad-.muse.ai")]
    [InlineData("bad_label.muse.ai")]
    [InlineData("*.muse.ai")]
    [InlineData("muse.ai\r\n")]
    [InlineData("")]
    public void CookieDomain_RejectsSuffixConfusionAndInvalidDns(string domain)
    {
        var bundle = Sample();
        bundle.Accounts[0].Cookies[0].Domain = domain;
        Assert.Throws<InvalidDataException>(() => CredentialArchive.Validate(bundle));
    }

    [Theory]
    [InlineData("null-account")]
    [InlineData("null-accounts")]
    [InlineData("null-name")]
    [InlineData("null-tags")]
    [InlineData("null-tag")]
    [InlineData("null-cookie")]
    [InlineData("null-cookies")]
    [InlineData("null-value")]
    [InlineData("name-control")]
    [InlineData("value-control")]
    [InlineData("path-control")]
    [InlineData("bad-name")]
    [InlineData("bad-path")]
    [InlineData("duplicate-cookie")]
    [InlineData("samesite")]
    [InlineData("session-expiry")]
    [InlineData("missing-expiry")]
    [InlineData("nan-expiry")]
    [InlineData("infinite-expiry")]
    [InlineData("range-expiry")]
    [InlineData("uncaptured-cookie")]
    [InlineData("invalid-proxy")]
    [InlineData("invalid-unicode")]
    public void Validate_RejectsInvalidFieldsWithoutLeakingProxyOrCookie(string variant)
    {
        var bundle = Sample();
        var account = bundle.Accounts[0];
        var cookie = account.Cookies[0];
        switch (variant)
        {
            case "null-account": bundle.Accounts.Add(null!); break;
            case "null-accounts": bundle.Accounts = null!; break;
            case "null-name": account.Name = null!; break;
            case "null-tags": account.Tags = null!; break;
            case "null-tag": account.Tags.Add(null!); break;
            case "null-cookie": account.Cookies.Add(null!); break;
            case "null-cookies": account.Cookies = null!; break;
            case "null-value": cookie.Value = null!; break;
            case "name-control": cookie.Name = "bad\tname"; break;
            case "value-control": cookie.Value += "\u0000"; break;
            case "path-control": cookie.Path = "/bad\r\n"; break;
            case "bad-name": cookie.Name = "bad=name"; break;
            case "bad-path": cookie.Path = "relative"; break;
            case "duplicate-cookie":
                account.Cookies.Add(new()
                {
                    Domain = ".MUSE.AI", Path = cookie.Path, Name = cookie.Name, IsSession = true,
                });
                break;
            case "samesite": cookie.SameSite = "lax"; break;
            case "session-expiry": cookie.IsSession = true; break;
            case "missing-expiry": cookie.ExpiresUnixSeconds = null; break;
            case "nan-expiry": cookie.ExpiresUnixSeconds = double.NaN; break;
            case "infinite-expiry": cookie.ExpiresUnixSeconds = double.PositiveInfinity; break;
            case "range-expiry": cookie.ExpiresUnixSeconds = 253_402_300_800d; break;
            case "uncaptured-cookie": account.CookiesCaptured = false; break;
            case "invalid-proxy": account.Proxy = "http://synthetic-user:" + Uri.EscapeDataString("synthetic-token") + "@invalid:bad-port"; break;
            case "invalid-unicode": account.Name = "\ud800"; break;
        }
        var error = Assert.Throws<InvalidDataException>(() => CredentialArchive.Validate(bundle));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic-user", error.ToString());
        Assert.DoesNotContain("synthetic-token", error.ToString());
        Assert.DoesNotContain("synthetic-cookie", error.ToString());
    }

    [Theory]
    [InlineData("muse.ai", ".MUSE.AI")]
    [InlineData(".muse.ai", "MUSE.AI")]
    [InlineData("app.muse.ai", ".APP.MUSE.AI")]
    [InlineData(".app.muse.ai", "APP.MUSE.AI")]
    public void CookieIdentity_HostOnlyAndDomainCookiesWithSameNameAndPathRoundTrip(
        string firstDomain, string secondDomain)
    {
        var bundle = Sample();
        bundle.Accounts[0].Cookies =
        [
            new() { Domain = firstDomain, Path = "/account", Name = "session",
                Value = "synthetic-first-scope", IsSession = true },
            new() { Domain = secondDomain, Path = "/account", Name = "session",
                Value = "synthetic-second-scope", IsSession = true },
        ];

        CredentialArchive.Validate(bundle);
        var restored = CredentialArchive.Decrypt(CredentialArchive.Encrypt(bundle, Passphrase), Passphrase);

        Assert.Equal(2, restored.Accounts[0].Cookies.Count);
        Assert.Equal(firstDomain, restored.Accounts[0].Cookies[0].Domain);
        Assert.Equal(secondDomain, restored.Accounts[0].Cookies[1].Domain);
        Assert.Equal(JsonSerializer.Serialize(bundle), JsonSerializer.Serialize(restored));
    }

    [Theory]
    [InlineData("muse.ai", "muse.ai")]
    [InlineData("muse.ai", "MUSE.AI")]
    [InlineData(".muse.ai", ".muse.ai")]
    [InlineData(".muse.ai", ".MUSE.AI")]
    [InlineData("app.muse.ai", "app.muse.ai")]
    [InlineData("app.muse.ai", "APP.MUSE.AI")]
    [InlineData(".app.muse.ai", ".app.muse.ai")]
    [InlineData(".app.muse.ai", ".APP.MUSE.AI")]
    public void CookieIdentity_RejectsTrueDuplicatesDuringValidationExportAndImport(
        string firstDomain, string secondDomain)
    {
        var bundle = Sample();
        bundle.Accounts[0].Cookies =
        [
            new() { Domain = firstDomain, Path = "/", Name = "session",
                Value = "synthetic-first-value", IsSession = true },
            new() { Domain = secondDomain, Path = "/", Name = "session",
                Value = "synthetic-different-value", IsSession = true },
        ];

        Assert.Throws<InvalidDataException>(() => CredentialArchive.Validate(bundle));
        Assert.Throws<InvalidDataException>(() => CredentialArchive.Encrypt(bundle, Passphrase));
        Assert.Throws<InvalidDataException>(() => CredentialArchive.Decrypt(
            SealJson(JsonSerializer.Serialize(bundle)), Passphrase));
    }

    [Fact]
    public void CookieIdentity_IsScopedByDomainPathAndCaseSensitiveName()
    {
        var bundle = Sample();
        bundle.Accounts[0].Cookies.AddRange(
        [
            new() { Domain = ".muse.ai", Path = "/other", Name = "MuseCookie", IsSession = true },
            new() { Domain = ".muse.ai", Path = "/Other", Name = "MuseCookie", IsSession = true },
            new() { Domain = "app.muse.ai", Path = "/", Name = "MuseCookie", IsSession = true },
            new() { Domain = ".muse.ai", Path = "/", Name = "musecookie", IsSession = true },
        ]);
        CredentialArchive.Validate(bundle);
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("name")]
    [InlineData("avatar-text")]
    [InlineData("proxy")]
    [InlineData("tags")]
    [InlineData("tag")]
    [InlineData("cookies")]
    [InlineData("cookie-name")]
    [InlineData("cookie-value")]
    [InlineData("cookie-path")]
    [InlineData("image")]
    [InlineData("extension")]
    [InlineData("image-missing")]
    public void Limits_AreEnforcedBeforeEncryption(string variant)
    {
        var bundle = Sample();
        var account = bundle.Accounts[0];
        var cookie = account.Cookies[0];
        switch (variant)
        {
            case "accounts": bundle.Accounts = Enumerable.Range(0, 201).Select(_ => new CredentialAccount()).ToList(); break;
            case "name": account.Name = new string('n', 1025); break;
            case "avatar-text": account.Avatar = new string('a', 129); break;
            case "proxy": account.Proxy = new string('p', 8193); break;
            case "tags": account.Tags = Enumerable.Repeat("tag", 101).ToList(); break;
            case "tag": account.Tags = [new string('t', 257)]; break;
            case "cookies": account.Cookies = Enumerable.Repeat(cookie, 1001).ToList(); break;
            case "cookie-name": cookie.Name = new string('n', 257); break;
            case "cookie-value": cookie.Value = new string('v', 16_385); break;
            case "cookie-path": cookie.Path = "/" + new string('p', 2048); break;
            case "image": account.AvatarImage = new byte[5 * 1024 * 1024 + 1]; break;
            case "extension": account.AvatarExtension = ".svg"; break;
            case "image-missing": account.AvatarImage = null; break;
        }
        Assert.Throws<InvalidDataException>(() => CredentialArchive.Encrypt(bundle, Passphrase));
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".bmp")]
    [InlineData(".gif")]
    [InlineData(".PNG")]
    public void Avatar_AllowedExtensionsAndExactFiveMiBBoundaryAreAccepted(string extension)
    {
        var bundle = new CredentialBundle
        {
            Accounts = [new() { AvatarImage = new byte[5 * 1024 * 1024], AvatarExtension = extension }],
        };
        CredentialArchive.Validate(bundle);
    }

    [Fact]
    public void CombinedAvatarLimit_IsCheckedEvenWhenEachImageIsAllowed()
    {
        var image = new byte[5 * 1024 * 1024];
        var bundle = new CredentialBundle
        {
            Accounts = Enumerable.Range(0, 10).Select(_ => new CredentialAccount
                { AvatarImage = image, AvatarExtension = ".png" }).ToList(),
        };
        Assert.Throws<InvalidDataException>(() => CredentialArchive.Encrypt(bundle, Passphrase));
    }

    [Fact]
    public void CombinedJsonLimit_CountsEscapedTextAsWellAsBase64()
    {
        var image = new byte[5 * 1024 * 1024];
        var bundle = new CredentialBundle
        {
            Accounts = Enumerable.Range(0, 9).Select(_ => new CredentialAccount
                { AvatarImage = image, AvatarExtension = ".png" }).ToList(),
        };
        bundle.Accounts[0].CookiesCaptured = true;
        bundle.Accounts[0].Cookies = Enumerable.Range(0, 100).Select(i => new CredentialCookie
        {
            Name = "cookie" + i, Domain = "muse.ai", IsSession = true, Value = new string('中', 16_384),
        }).ToList();
        Assert.Throws<InvalidDataException>(() => CredentialArchive.Encrypt(bundle, Passphrase));
    }

    [Fact]
    public void TotalCookieLimit_IsEnforcedAcrossAccounts()
    {
        var cookies = Enumerable.Range(0, 1000).Select(i => new CredentialCookie
            { Name = "cookie" + i, Domain = "muse.ai", IsSession = true }).ToList();
        var bundle = new CredentialBundle
        {
            Accounts = Enumerable.Range(0, 21).Select(_ => new CredentialAccount
                { CookiesCaptured = true, Cookies = cookies }).ToList(),
        };
        Assert.Throws<InvalidDataException>(() => CredentialArchive.Validate(bundle));
    }

    [Fact]
    public void AuthenticatedJson_AccountAndCookieLimitsAreEnforcedDuringImport()
    {
        var tooManyAccounts = new CredentialBundle
        {
            Accounts = Enumerable.Range(0, 201).Select(_ => new CredentialAccount()).ToList(),
        };
        Assert.Throws<InvalidDataException>(() => CredentialArchive.Decrypt(
            SealJson(JsonSerializer.Serialize(tooManyAccounts)), Passphrase));
        var bundle = Sample();
        bundle.Accounts[0].Cookies = Enumerable.Range(0, 1001).Select(i => new CredentialCookie
            { Name = "cookie" + i, Domain = "muse.ai", IsSession = true }).ToList();
        Assert.Throws<InvalidDataException>(() => CredentialArchive.Decrypt(
            SealJson(JsonSerializer.Serialize(bundle)), Passphrase));
    }

    [Fact]
    public void AuthenticatedJson_RejectsInvalidUtf8AndIncompleteStructure()
    {
        var json = JsonSerializer.Serialize(Sample());
        var invalidUtf8 = Encoding.UTF8.GetBytes(json);
        invalidUtf8[Array.IndexOf(invalidUtf8, (byte)'M')] = 0xff;
        Assert.Throws<InvalidDataException>(() => CredentialArchive.Decrypt(SealPayload(invalidUtf8), Passphrase));
        Assert.Throws<InvalidDataException>(() => CredentialArchive.Decrypt(SealJson(json[..^1]), Passphrase));
    }

    [Fact]
    public void MaximumAccountCount_RoundTripsWithDefaults()
    {
        var bundle = new CredentialBundle
        {
            Accounts = Enumerable.Range(0, CredentialArchive.MaximumAccounts)
                .Select(_ => new CredentialAccount()).ToList(),
        };
        var archive = CredentialArchive.Encrypt(bundle, Passphrase);
        Assert.Equal(CredentialArchive.MaximumAccounts, CredentialArchive.Decrypt(archive, Passphrase).Accounts.Count);
    }

    private static void AssertAuthenticationFailure(byte[] bytes, string passphrase)
    {
        var error = Assert.Throws<InvalidDataException>(() => CredentialArchive.Decrypt(bytes, passphrase));
        Assert.Equal("密码错误或文件损坏", error.Message);
        Assert.Null(error.InnerException);
    }

    // An independent producer exercises import validation of authenticated but malicious JSON.
    // It uses the documented format, not private members or the production serializer/validator.
    private static byte[] SealJson(string json) => SealPayload(Encoding.UTF8.GetBytes(json));

    private static byte[] SealPayload(byte[] plaintext)
    {
        var result = new byte[HeaderBytes + plaintext.Length + 16];
        "MUSECRED"u8.CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12), 1);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16), 600_000);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(20), 1);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(24), plaintext.Length);
        RandomNumberGenerator.Fill(result.AsSpan(28, 16));
        RandomNumberGenerator.Fill(result.AsSpan(44, 12));
        var key = Rfc2898DeriveBytes.Pbkdf2(Passphrase, result.AsSpan(28, 16), 600_000, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(result.AsSpan(44, 12), plaintext, result.AsSpan(56, plaintext.Length),
                result.AsSpan(56 + plaintext.Length, 16), result.AsSpan(0, 56));
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
