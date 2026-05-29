using System.Collections.Concurrent;
using MashiroLauncher.Core.Auth;
using MashiroLauncher.Core.Auth.Microsoft;

namespace MashiroLauncher.App.Services;

/// <summary>
/// Multi-account wrapper around <see cref="MicrosoftAuthChain"/>. Owns the
/// on-disk account store and is the single entry point the VM uses for all
/// sign-in / refresh / sign-out flows.
///
/// Per-account refresh semaphores keep us from accidentally racing two refresh
/// chains for the same UUID when both the active-account boot path and a
/// per-instance launch happen to fire at once.
/// </summary>
public sealed class MicrosoftAuthService(HttpClient http)
{
    private readonly MicrosoftAuthChain _chain = new(http);
    private readonly AccountStorage _storage = new();
    private readonly AccountStateStorage _state = new();

    /// <summary>Refresh margin — refresh anything expiring within this window.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    /// <summary>Per-UUID locks so concurrent refresh calls for the same account serialize.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.OrdinalIgnoreCase);

    // ---- Loading & state ----------------------------------------------------

    /// <summary>
    /// Loads every stored Microsoft account from disk. Performs best-effort
    /// refresh of any account whose Minecraft access token is about to expire,
    /// so the UI can show a usable session immediately. Accounts that fail to
    /// refresh are still returned with their stored (expired) token — the
    /// per-launch path will retry later and can surface a meaningful error.
    /// </summary>
    public async Task<IReadOnlyList<MicrosoftAccount>> LoadAllAsync(CancellationToken ct = default)
    {
        var stored = await _storage.LoadAllAsync(ct);
        if (stored.Count == 0) return Array.Empty<MicrosoftAccount>();

        // Return the stored accounts as-is — refresh is intentionally deferred
        // to the launch path. Eager serial refresh per N accounts at boot used
        // to block startup on the slowest Xbox auth round-trip; the per-launch
        // TryRefreshIfExpiredAsync now handles staleness lazily and only for
        // the account that's actually about to play.
        var loaded = new List<MicrosoftAccount>(stored.Count);
        foreach (var dto in stored)
            loaded.Add(dto.ToAccount());
        return loaded;
    }

    /// <summary>Returns the active account's UUID hex, or null if no active selection.</summary>
    public Task<string?> GetActiveAccountIdAsync(CancellationToken ct = default) =>
        _state.GetActiveAccountIdAsync(ct);

    /// <summary>Persists the active account UUID. Pass null to clear.</summary>
    public Task SetActiveAccountIdAsync(string? uuidHex, CancellationToken ct = default) =>
        _state.SetActiveAccountIdAsync(uuidHex, ct);

    // ---- Sign-in flow -------------------------------------------------------

    /// <summary>Step 1: returns the authorize URL to load in the WebView plus the
    /// PKCE session (state + verifier) to pass back to <see cref="CompleteSignInAsync"/>.</summary>
    public (string Url, PkceSession Session) BuildAuthorizationUrl() =>
        _chain.BuildAuthorizationUrl();

    /// <summary>
    /// Step 2: caller captured the redirect URL; complete sign-in using the PKCE
    /// session from step 1. The resulting account is added (or updated, dedup'd
    /// by UUID) to the store.
    /// </summary>
    public async Task<MicrosoftAccount> CompleteSignInAsync(
        string callbackUrl, PkceSession session, CancellationToken ct = default)
    {
        var r = await _chain.CompleteSignInAsync(callbackUrl, session, ct);
        var account = FromResult(r);
        await _storage.SaveAsync(account, ct);
        return account;
    }

    /// <summary>
    /// Persist an account that came from somewhere other than <see cref="CompleteSignInAsync"/>
    /// (e.g. a refresh path that already produced a fresh <see cref="MicrosoftAccount"/>).
    /// Dedup'd by UUID — calling twice for the same account just overwrites the file.
    /// </summary>
    public Task AddOrUpdateAsync(MicrosoftAccount account, CancellationToken ct = default) =>
        _storage.SaveAsync(account, ct);

    // ---- Refresh ------------------------------------------------------------

    /// <summary>
    /// Refresh a specific account by UUID. Returns the refreshed account on
    /// success; throws on token error (caller is expected to surface). The
    /// account is loaded from storage to find the current refresh token, then
    /// re-saved with the fresh tokens.
    /// </summary>
    public async Task<MicrosoftAccount?> RefreshSpecificAsync(string uuidHex, CancellationToken ct = default)
    {
        var all = await _storage.LoadAllAsync(ct);
        var target = all.FirstOrDefault(a => string.Equals(a.UuidHex, uuidHex, StringComparison.OrdinalIgnoreCase));
        if (target is null) return null;
        return await RefreshSpecificAsync(target.ToAccount(), ct);
    }

    /// <summary>Internal: refresh using an already-loaded MicrosoftAccount. Serializes per-UUID.</summary>
    private async Task<MicrosoftAccount> RefreshSpecificAsync(MicrosoftAccount account, CancellationToken ct)
    {
        var key = account.Uuid.ToString("N");
        var gate = _refreshLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Check expiry again inside the lock — another caller may have just
            // refreshed while we were waiting.
            var fresh = await ReloadFromDiskAsync(key, ct);
            if (fresh is not null && !fresh.IsExpired(RefreshMargin))
                return fresh;

            var r = await _chain.RefreshSignInAsync(account.RefreshToken, ct);
            var refreshed = FromResult(r);
            await _storage.SaveAsync(refreshed, ct);
            return refreshed;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<MicrosoftAccount?> ReloadFromDiskAsync(string uuidHex, CancellationToken ct)
    {
        var all = await _storage.LoadAllAsync(ct);
        var match = all.FirstOrDefault(a => string.Equals(a.UuidHex, uuidHex, StringComparison.OrdinalIgnoreCase));
        return match?.ToAccount();
    }

    // ---- Removal ------------------------------------------------------------

    /// <summary>
    /// Removes a single account from the store. If it was the active account,
    /// the active-account pointer is cleared (caller is responsible for
    /// promoting another account or falling back to offline).
    /// </summary>
    public async Task RemoveAccountAsync(string uuidHex, CancellationToken ct = default)
    {
        _storage.Remove(uuidHex);
        _refreshLocks.TryRemove(uuidHex, out _);

        var active = await _state.GetActiveAccountIdAsync(ct);
        if (string.Equals(active, uuidHex, StringComparison.OrdinalIgnoreCase))
            await _state.SetActiveAccountIdAsync(null, ct);
    }

    /// <summary>
    /// Clears every stored account and the active-account pointer. Used by the
    /// legacy "전체 로그아웃" path (sign out of everything).
    /// </summary>
    public void SignOutAll()
    {
        try { _storage.RemoveAll(); }
        catch { /* deletion best-effort: in-memory state is what counts */ }
        try { _state.Delete(); }
        catch { /* same */ }
        _refreshLocks.Clear();
    }

    // ---- Helpers ------------------------------------------------------------

    private static MicrosoftAccount FromResult(SignInResult r)
    {
        var uuid = Guid.ParseExact(r.Uuid, "N");
        return new MicrosoftAccount(
            r.Username, uuid, r.MinecraftAccessToken, r.McTokenExpiresAt,
            r.MsRefreshToken, r.Xuid, r.SkinUrl);
    }
}
