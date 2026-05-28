using MashiroLauncher.Core.Auth;
using MashiroLauncher.Core.Auth.Microsoft;

namespace MashiroLauncher.App.Services;

public sealed class MicrosoftAuthService(HttpClient http)
{
    private readonly MicrosoftAuthChain _chain = new(http);
    private readonly AccountStorage _storage = new();

    public async Task<MicrosoftAccount?> TryLoadAsync(CancellationToken ct = default)
    {
        var stored = await _storage.LoadAsync(ct);
        if (stored is null) return null;
        var account = stored.ToAccount();
        if (!account.IsExpired(TimeSpan.FromMinutes(2)))
            return account;
        try
        {
            return await RefreshAsync(account.RefreshToken, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Step 1: returns the URL to open in the user's browser plus the state to remember.</summary>
    public (string Url, string State) BuildAuthorizationUrl() =>
        _chain.BuildAuthorizationUrl();

    /// <summary>Step 2: caller pasted the redirect URL back; complete sign-in.</summary>
    public async Task<MicrosoftAccount> CompleteSignInAsync(
        string callbackUrl, string expectedState, CancellationToken ct = default)
    {
        var r = await _chain.CompleteSignInAsync(callbackUrl, expectedState, ct);
        var account = FromResult(r);
        await _storage.SaveAsync(account, ct);
        return account;
    }

    public async Task<MicrosoftAccount> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var r = await _chain.RefreshSignInAsync(refreshToken, ct);
        var account = FromResult(r);
        await _storage.SaveAsync(account, ct);
        return account;
    }

    public void SignOut()
    {
        try { _storage.Delete(); }
        catch { /* deletion best-effort: in-memory state is what counts */ }
    }

    private static MicrosoftAccount FromResult(SignInResult r)
    {
        var uuid = Guid.ParseExact(r.Uuid, "N");
        return new MicrosoftAccount(
            r.Username, uuid, r.MinecraftAccessToken, r.McTokenExpiresAt,
            r.MsRefreshToken, r.Xuid, r.SkinUrl);
    }
}
