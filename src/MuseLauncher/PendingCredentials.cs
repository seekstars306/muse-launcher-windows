using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MuseLauncher.Core;

namespace MuseLauncher;

/// <summary>仅用于把导入的会话型 Cookie 交给首次正常启动的浏览器，不保存导出密码。</summary>
internal static class PendingCredentials
{
    private const string FileName = "pending-muse-credentials.bin";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MuseLauncher.PendingCookies.v1");

    private static string FilePath(string profileDirectory) => Path.Combine(profileDirectory, FileName);
    internal static bool Exists(string profileDirectory) => File.Exists(FilePath(profileDirectory));

    internal static void Write(string profileDirectory, CredentialAccount account)
    {
        var bundle = new CredentialBundle { Accounts = new() { account } };
        CredentialArchive.Validate(bundle);
        var plain = JsonSerializer.SerializeToUtf8Bytes(bundle);
        byte[]? encrypted = null;
        try
        {
            encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(profileDirectory);
            using var file = new FileStream(FilePath(profileDirectory), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(encrypted);
            file.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (encrypted != null) CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    internal static CredentialAccount Read(string profileDirectory)
    {
        var path = FilePath(profileDirectory);
        var info = new FileInfo(path);
        if (info.Length > CredentialArchive.MaximumFileBytes) throw new InvalidDataException("待恢复凭证文件过大");
        var encrypted = File.ReadAllBytes(path);
        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var bundle = JsonSerializer.Deserialize<CredentialBundle>(plain)
                ?? throw new InvalidDataException("待恢复凭证无效");
            CredentialArchive.Validate(bundle);
            if (bundle.Accounts.Count != 1) throw new InvalidDataException("待恢复凭证无效");
            return bundle.Accounts[0];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (plain != null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    internal static void Delete(string profileDirectory) => File.Delete(FilePath(profileDirectory));
}
