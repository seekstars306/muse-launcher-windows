namespace MuseLauncher.Core;

/// <summary>Portable account data. Cookie values and proxy credentials are secrets.</summary>
public sealed class CredentialBundle
{
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<CredentialAccount> Accounts { get; set; } = new();
}

public sealed class CredentialAccount
{
    public string Name { get; set; } = "";
    public string Avatar { get; set; } = "m";
    public string Proxy { get; set; } = "";
    public bool ForceProxy { get; set; }
    public List<string> Tags { get; set; } = new();
    public byte[]? AvatarImage { get; set; }
    public string AvatarExtension { get; set; } = "";
    public bool CookiesCaptured { get; set; }
    public List<CredentialCookie> Cookies { get; set; } = new();
}

/// <summary>Fields supplied by WebView2 CookieManager; session cookies have no expiry.</summary>
public sealed class CredentialCookie
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Path { get; set; } = "/";
    public bool IsSecure { get; set; }
    public bool IsHttpOnly { get; set; }
    public bool IsSession { get; set; }
    public string SameSite { get; set; } = "Lax";
    public double? ExpiresUnixSeconds { get; set; }
}
