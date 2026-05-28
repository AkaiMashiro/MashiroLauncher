using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Core.Common;

namespace Launcher.Core.Auth.Microsoft;

public sealed record McAuthResult(string AccessToken, DateTimeOffset ExpiresAt);

public sealed record McProfile(string Uuid, string Username, string? SkinUrl);

public class MinecraftAuthException(string message) : Exception(message);

public sealed class MinecraftAuth(HttpClient http)
{
    public async Task<McAuthResult> LoginAsync(string userHash, string xstsToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, MicrosoftAuthConfig.McLoginUrl)
        {
            Content = JsonContent.Create(new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" }),
        };
        using var resp = await http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new MinecraftAuthException($"Minecraft 로그인 실패 {(int)resp.StatusCode}: {json}");

        var dto = JsonSerializer.Deserialize<LoginResponse>(json, JsonOptions.Default)
                  ?? throw new MinecraftAuthException("Minecraft 로그인 응답 파싱 실패.");
        return new McAuthResult(dto.AccessToken, DateTimeOffset.UtcNow.AddSeconds(dto.ExpiresIn));
    }

    public async Task<McProfile> GetProfileAsync(string mcAccessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, MicrosoftAuthConfig.McProfileUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcAccessToken);
        using var resp = await http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new MinecraftAuthException("이 Microsoft 계정에 Minecraft Java Edition이 없습니다.");
        if (!resp.IsSuccessStatusCode)
            throw new MinecraftAuthException($"Profile 조회 실패 {(int)resp.StatusCode}: {json}");

        var dto = JsonSerializer.Deserialize<ProfileResponse>(json, JsonOptions.Default)
                  ?? throw new MinecraftAuthException("Profile 응답 파싱 실패.");
        var skin = dto.Skins?.FirstOrDefault(s => string.Equals(s.State, "ACTIVE", StringComparison.OrdinalIgnoreCase))?.Url;
        return new McProfile(dto.Id, dto.Name, skin);
    }

    private sealed record LoginResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType);

    private sealed record ProfileResponse(
        string Id,
        string Name,
        IReadOnlyList<ProfileSkin>? Skins);

    private sealed record ProfileSkin(string Id, string State, string Url);
}
