using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Launcher.Core.Common;

namespace Launcher.Core.Auth.Microsoft;

/// <summary>
/// Microsoft access token bundle. <see cref="RefreshToken"/> is nullable because
/// the token endpoint may omit it on refresh responses; callers must fall back
/// to the previously stored refresh token in that case.
/// </summary>
public sealed record MsTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

public class OAuth2AuthException(string message) : Exception(message);

/// <summary>
/// Microsoft Live OAuth Authorization Code Flow with out-of-band (OOB) redirect.
///
/// Why OOB instead of PKCE+loopback: the well-known public Minecraft client id
/// (<see cref="MicrosoftAuthConfig.ClientId"/>) is registered only at
/// login.live.com and only with the OOB redirect URI. We can't add a loopback
/// URI to an id we don't own, and PKCE/device-code aren't supported here.
///
/// Flow:
///   1. Caller invokes <see cref="BuildAuthorizationUrl"/> and opens the URL
///      in the user's browser.
///   2. User signs in to Microsoft and approves the Xbox Live scope.
///   3. Microsoft redirects to login.live.com/oauth20_desktop.srf — a
///      near-blank page whose URL contains ?code=...
///   4. User copies that URL into our launcher; we call
///      <see cref="ExchangeCallbackUrlAsync"/> to extract the code (verifying
///      the state) and trade it for an MS access token.
/// </summary>
public sealed class OAuth2Flow(HttpClient http)
{
    /// <summary>
    /// Builds the authorize URL the user should open in their browser.
    /// Returns the URL plus the random state we expect to see echoed back
    /// in the redirect.
    /// </summary>
    public (string Url, string State) BuildAuthorizationUrl()
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        // prompt=select_account forces the account-picker every time. Without
        // it, Microsoft tries silent SSO and — when the user has already
        // signed into another Minecraft client — falls into a cleanup flow
        // that redirects back to oauth20_desktop.srf?removed=true with no
        // authorization code. select_account avoids that branch entirely.
        var url =
            MicrosoftAuthConfig.AuthorizeUrl
            + "?client_id=" + MicrosoftAuthConfig.ClientId
            + "&response_type=code"
            + "&redirect_uri=" + Uri.EscapeDataString(MicrosoftAuthConfig.RedirectUri)
            + "&scope=" + Uri.EscapeDataString(MicrosoftAuthConfig.Scope)
            + "&state=" + state
            + "&prompt=select_account";
        return (url, state);
    }

    /// <summary>
    /// User pasted the full redirect URL back into the launcher. Pull out the
    /// authorization code (verifying state) and trade it for tokens.
    /// </summary>
    public async Task<MsTokens> ExchangeCallbackUrlAsync(
        string callbackUrl, string expectedState, CancellationToken ct)
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

        // Microsoft cleanup redirect — no code returned. User needs to start over.
        if (query["removed"] == "true")
            throw new OAuth2AuthException("Microsoft 세션이 정리되었습니다. '취소' 후 'Microsoft로 로그인'을 한 번 더 눌러주세요.");

        var code = query["code"];
        if (string.IsNullOrEmpty(code))
            throw new OAuth2AuthException("주소에 인증 코드(code)가 없습니다. 로그인 후 빈 페이지가 뜨면 그 페이지의 주소를 복사해 주세요.");

        var state = query["state"];
        if (state != expectedState)
            throw new OAuth2AuthException("State 불일치. 보안을 위해 처음부터 다시 시도해주세요.");

        return await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = MicrosoftAuthConfig.ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = MicrosoftAuthConfig.RedirectUri,
            ["scope"] = MicrosoftAuthConfig.Scope,
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

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("scope")] string Scope);
}
