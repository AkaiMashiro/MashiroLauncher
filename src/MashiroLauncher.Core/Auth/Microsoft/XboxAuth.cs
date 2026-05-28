using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MashiroLauncher.Core.Common;

namespace MashiroLauncher.Core.Auth.Microsoft;

public sealed record XboxTokens(string XblToken, string UserHash, string XstsToken, string Xuid);

public class XboxAuthException(string message) : Exception(message);

public sealed class XboxAuth(HttpClient http)
{
    public async Task<XboxTokens> AuthenticateAsync(string msAccessToken, CancellationToken ct)
    {
        var xblResp = await PostJsonAsync<XblResponse>(MicrosoftAuthConfig.XblAuthUrl, new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = $"d={msAccessToken}",
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        }, ct);
        if (xblResp is null || xblResp.Token is null)
            throw new XboxAuthException("XBL 토큰을 받지 못했습니다.");

        var xstsBody = new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xblResp.Token },
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT",
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, MicrosoftAuthConfig.XstsAuthUrl);
        req.Content = JsonBody(xstsBody);
        req.Headers.Accept.Add(new("application/json"));
        using var resp = await http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            var err = TryParse<XstsError>(json);
            throw new XboxAuthException(MapXstsError(err?.XErr ?? 0));
        }
        if (!resp.IsSuccessStatusCode)
            throw new XboxAuthException($"XSTS {(int)resp.StatusCode}: {json}");

        var xsts = JsonSerializer.Deserialize<XstsResponse>(json, JsonOptions.Default)
                   ?? throw new XboxAuthException("XSTS 응답 파싱 실패.");
        var xui = xsts.DisplayClaims?.Xui?.FirstOrDefault();
        var uhs = xui?.Uhs ?? throw new XboxAuthException("XSTS 응답에 user hash가 없습니다.");
        var xuid = xui.Xid ?? "0";

        return new XboxTokens(xblResp.Token, uhs, xsts.Token, xuid);
    }

    private async Task<T?> PostJsonAsync<T>(string url, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = JsonBody(body);
        req.Headers.Accept.Add(new("application/json"));
        using var resp = await http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new XboxAuthException($"{url} {(int)resp.StatusCode}: {json}");
        return JsonSerializer.Deserialize<T>(json, JsonOptions.Default);
    }

    // Serialize using the runtime type so anonymous-typed bodies don't get flattened to "{}".
    private static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body, body.GetType()), Encoding.UTF8, "application/json");

    private static T? TryParse<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions.Default); }
        catch { return default; }
    }

    private static string MapXstsError(long xerr) => xerr switch
    {
        2148916233 => "이 Microsoft 계정에 Xbox 프로필이 없습니다. xbox.com에서 한 번 가입한 다음 다시 시도하세요.",
        2148916235 => "거주 지역에서 Xbox Live 서비스를 사용할 수 없습니다.",
        2148916236 or 2148916237 => "성인 인증이 필요합니다.",
        2148916238 => "미성년자 계정은 가족 그룹에 추가되어 있어야 합니다.",
        2148916227 => "계정이 보안 상의 이유로 잠겨 있습니다. account.live.com에서 확인 후 다시 시도하세요.",
        0 => "XSTS 인증이 거부되었습니다. (코드 미상)",
        _ => $"XSTS 인증 실패 (코드 {xerr}).",
    };

    private sealed record XblResponse(string Token, DisplayClaims? DisplayClaims);
    private sealed record XstsResponse(string Token, DisplayClaims? DisplayClaims);
    private sealed record DisplayClaims(IReadOnlyList<Xui>? Xui);
    private sealed record Xui(string Uhs, string? Xid);

    private sealed record XstsError(
        [property: JsonPropertyName("XErr")] long XErr,
        [property: JsonPropertyName("Message")] string? Message);
}
