using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using MashiroLauncher.Core.Common;

namespace MashiroLauncher.Core.Auth.Microsoft;

/// <summary>
/// Microsoft access token bundle. <see cref="RefreshToken"/> is nullable because
/// the token endpoint may omit it on refresh responses; callers must fall back
/// to the previously stored refresh token in that case.
/// </summary>
public sealed record MsTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Per-sign-in secrets the authorize step generates and the token-exchange step
/// must echo back: the CSRF <see cref="State"/> and the PKCE
/// <see cref="CodeVerifier"/>. The caller holds this between opening the
/// authorize URL and completing sign-in.
/// </summary>
public sealed record PkceSession(string State, string CodeVerifier);

public class OAuth2AuthException(string message) : Exception(message);

/// <summary>
/// Microsoft identity platform (v2.0) Authorization Code Flow with PKCE, against
/// the /consumers/ tenant (personal Microsoft accounts — what Minecraft uses).
///
/// We use our own Azure AD application id (<see cref="MicrosoftAuthConfig.ClientId"/>),
/// approved by Mojang. As a public desktop client it has no secret, so PKCE
/// (S256) protects the code exchange.
///
/// Flow:
///   1. <see cref="BuildAuthorizationUrl"/> mints a state + PKCE verifier and
///      returns the authorize URL plus the <see cref="PkceSession"/> to keep.
///   2. The embedded WebView drives sign-in + Xbox consent, then Microsoft
///      redirects to the registered nativeclient URI with ?code=...
///   3. The WebView intercepts that navigation and hands the URL to
///      <see cref="ExchangeCallbackUrlAsync"/>, which verifies the state and
///      trades the code (+ verifier) for an MS access token.
/// </summary>
public sealed class OAuth2Flow(HttpClient http)
{
    /// <summary>
    /// Builds the authorize URL to load in the WebView. Returns the URL plus the
    /// <see cref="PkceSession"/> (state + code verifier) the caller must pass
    /// back to <see cref="ExchangeCallbackUrlAsync"/>.
    /// </summary>
    public (string Url, PkceSession Session) BuildAuthorizationUrl()
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var verifier = GenerateCodeVerifier();
        var challenge = CodeChallengeFor(verifier);

        // prompt=select_account forces the account-picker every time so a user
        // with multiple Microsoft accounts can choose which one to add.
        var url =
            MicrosoftAuthConfig.AuthorizeUrl
            + "?client_id=" + MicrosoftAuthConfig.ClientId
            + "&response_type=code"
            + "&redirect_uri=" + Uri.EscapeDataString(MicrosoftAuthConfig.RedirectUri)
            + "&scope=" + Uri.EscapeDataString(MicrosoftAuthConfig.Scope)
            + "&state=" + state
            + "&code_challenge=" + challenge
            + "&code_challenge_method=S256"
            + "&prompt=select_account";
        return (url, new PkceSession(state, verifier));
    }

    /// <summary>
    /// The WebView intercepted the redirect to the nativeclient URI. Pull out the
    /// authorization code (verifying the state matches the session) and trade it
    /// — with the PKCE verifier — for tokens.
    /// </summary>
    public async Task<MsTokens> ExchangeCallbackUrlAsync(
        string callbackUrl, PkceSession session, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(callbackUrl))
            throw new OAuth2AuthException("주소(URL)가 비어 있습니다.");

        Uri uri;
        try { uri = new Uri(callbackUrl.Trim()); }
        catch { throw new OAuth2AuthException("올바른 주소(URL)가 아닙니다."); }

        var query = HttpUtility.ParseQueryString(uri.Query);

        var error = query["error"];
        if (!string.IsNullOrEmpty(error))
            throw new OAuth2AuthException($"OAuth2 오류: {error} - {query["error_description"]}");

        var code = query["code"];
        if (string.IsNullOrEmpty(code))
            throw new OAuth2AuthException("인증 코드(code)를 받지 못했습니다. 처음부터 다시 시도해 주세요.");

        if (query["state"] != session.State)
            throw new OAuth2AuthException("State 불일치. 보안을 위해 처음부터 다시 시도해주세요.");

        return await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = MicrosoftAuthConfig.ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = MicrosoftAuthConfig.RedirectUri,
            ["scope"] = MicrosoftAuthConfig.Scope,
            ["code_verifier"] = session.CodeVerifier,
        }, ct);
    }

    public Task<MsTokens> RefreshAsync(string refreshToken, CancellationToken ct) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = MicrosoftAuthConfig.ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = MicrosoftAuthConfig.Scope,
        }, ct);

    private async Task<MsTokens> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, MicrosoftAuthConfig.TokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new OAuth2AuthException($"Token endpoint {(int)resp.StatusCode}: {body}");

        var dto = JsonSerializer.Deserialize<TokenResponse>(body, JsonOptions.Default)
                  ?? throw new OAuth2AuthException("토큰 응답 파싱 실패.");
        return new MsTokens(
            dto.AccessToken,
            dto.RefreshToken,  // may be null on refresh responses — caller preserves the old one
            DateTimeOffset.UtcNow.AddSeconds(dto.ExpiresIn));
    }

    // ---- PKCE helpers -------------------------------------------------------

    /// <summary>32 random bytes → 43-char base64url verifier (within the 43–128 spec range).</summary>
    private static string GenerateCodeVerifier() =>
        Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>S256 challenge: base64url(SHA-256(ASCII(verifier))).</summary>
    private static string CodeChallengeFor(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>URL-safe, unpadded Base64 (RFC 7636 / 4648 §5).</summary>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("scope")] string Scope);
}
