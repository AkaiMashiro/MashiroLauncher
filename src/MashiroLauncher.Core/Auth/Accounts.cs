using System.Security.Cryptography;
using System.Text;

namespace MashiroLauncher.Core.Auth;

public interface IAccount
{
    string Username { get; }
    Guid Uuid { get; }
    string AccessToken { get; }
    string UserType { get; }
    string Xuid { get; }
    string ClientId { get; }
}

public sealed class OfflineAccount : IAccount
{
    public OfflineAccount(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username must be non-empty.", nameof(username));
        Username = username;
        Uuid = ComputeOfflineUuid(username);
    }

    public string Username { get; }
    public Guid Uuid { get; }
    public string AccessToken => "0";
    public string UserType => "legacy";
    public string Xuid => "0";
    public string ClientId => string.Empty;

    // Mojang convention: UUID v3 from MD5 of "OfflinePlayer:<name>" (UTF-8).
    public static Guid ComputeOfflineUuid(string username)
    {
        var input = Encoding.UTF8.GetBytes($"OfflinePlayer:{username}");
        var hash = MD5.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // version 3
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122 variant
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return Guid.Parse($"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}");
    }
}
