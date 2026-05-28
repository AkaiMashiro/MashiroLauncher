using Launcher.Core.Auth.Microsoft;

namespace Launcher.Core.Auth;

public sealed class MicrosoftAccount(
    string username,
    Guid uuid,
    string mcAccessToken,
    DateTimeOffset mcTokenExpiresAt,
    string msRefreshToken,
    string xuid,
    string? skinUrl = null) : IAccount
{
    public string Username { get; } = username;
    public Guid Uuid { get; } = uuid;
    public string AccessToken { get; } = mcAccessToken;
    public string UserType => "msa";
    public string Xuid { get; } = xuid;
    public string ClientId => MicrosoftAuthConfig.ClientId;

    public DateTimeOffset McTokenExpiresAt { get; } = mcTokenExpiresAt;
    public string RefreshToken { get; } = msRefreshToken;
    public string? SkinUrl { get; } = skinUrl;

    public bool IsExpired(TimeSpan margin) =>
        DateTimeOffset.UtcNow >= McTokenExpiresAt - margin;
}
