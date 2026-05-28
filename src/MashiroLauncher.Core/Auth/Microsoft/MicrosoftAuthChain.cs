namespace MashiroLauncher.Core.Auth.Microsoft;

public sealed record SignInResult(
    string Uuid,
    string Username,
    string MinecraftAccessToken,
    DateTimeOffset McTokenExpiresAt,
    string MsRefreshToken,
    string Xuid,
    string? SkinUrl);

public sealed class MicrosoftAuthChain(HttpClient http)
{
    private readonly OAuth2Flow _oauth = new(http);
    private readonly XboxAuth _xbox = new(http);
    private readonly MinecraftAuth _mc = new(http);

    /// <summary>Step 1 of OOB sign-in: open this URL in the user's browser.</summary>
    public (string Url, string State) BuildAuthorizationUrl() =>
        _oauth.BuildAuthorizationUrl();

    /// <summary>
    /// Step 2: user pasted the redirect URL from the browser back into the
    /// launcher. Trade the code for tokens and complete the Xbox/XSTS/MC chain.
    /// </summary>
    public async Task<SignInResult> CompleteSignInAsync(
        string callbackUrl, string expectedState, CancellationToken ct = default)
    {
        var ms = await _oauth.ExchangeCallbackUrlAsync(callbackUrl, expectedState, ct);
        return await CompleteChainAsync(ms, ct);
    }

    public async Task<SignInResult> RefreshSignInAsync(string refreshToken, CancellationToken ct = default)
    {
        var ms = await _oauth.RefreshAsync(refreshToken, ct);
        // Microsoft sometimes omits a fresh refresh_token in the refresh response
        // (the old one is still valid). Preserve the caller's token in that case
        // so we don't silently lose the ability to renew next time.
        var effective = ms with { RefreshToken = ms.RefreshToken ?? refreshToken };
        return await CompleteChainAsync(effective, ct);
    }

    private async Task<SignInResult> CompleteChainAsync(MsTokens ms, CancellationToken ct)
    {
        var xbox = await _xbox.AuthenticateAsync(ms.AccessToken, ct);
        var mc = await _mc.LoginAsync(xbox.UserHash, xbox.XstsToken, ct);
        var profile = await _mc.GetProfileAsync(mc.AccessToken, ct);
        return new SignInResult(
            profile.Uuid,
            profile.Username,
            mc.AccessToken,
            mc.ExpiresAt,
            ms.RefreshToken ?? "",
            xbox.Xuid,
            profile.SkinUrl);
    }
}
