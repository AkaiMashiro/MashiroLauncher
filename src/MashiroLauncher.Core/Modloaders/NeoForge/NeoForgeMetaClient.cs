using System.Text.Json;
using System.Text.Json.Serialization;
using MashiroLauncher.Core.Common;

namespace MashiroLauncher.Core.Modloaders.NeoForge;

public class NeoForgeMetaException(string message) : Exception(message);

/// <summary>
/// Thin client over NeoForge's Maven metadata API (maven.neoforged.net).
/// We only need two things: the list of NeoForge versions that target a
/// specific Minecraft release, and a "latest" pick for the default flow.
///
/// NeoForge versions follow the pattern <c>{mcMinor}.{mcPatch}.{patch}</c>
/// (so MC 1.21.5 → NeoForge 21.5.x, MC 1.20.4 → 20.4.x). MC 1.20.1 is NOT
/// supported by NeoForge — it stayed on Forge's 47.x branch. We throw a
/// friendly error in that case instead of an empty list.
/// </summary>
public sealed class NeoForgeMetaClient(Downloader downloader)
{
    private const string Base = "https://maven.neoforged.net/api/maven";
    private const string Group = "releases/net/neoforged/neoforge";

    public async Task<IReadOnlyList<string>> ListVersionsAsync(
        string mcVersion, CancellationToken ct = default)
    {
        var prefix = NeoForgePrefixFor(mcVersion);
        var url = new Uri($"{Base}/versions/{Group}?filter={Uri.EscapeDataString(prefix)}");
        var json = await downloader.FetchTextAsync(url, ct);

        var dto = JsonSerializer.Deserialize<VersionsResponse>(json, JsonOptions.Default)
                  ?? throw new NeoForgeMetaException("NeoForge 버전 목록 파싱 실패.");
        if (dto.Versions is null || dto.Versions.Count == 0)
            throw new NeoForgeMetaException(
                $"NeoForge가 Minecraft {mcVersion}을(를) 지원하지 않습니다.");

        // Maven API returns oldest-first; flip so callers get newest-first and
        // PickLatestAsync can just take [0].
        return dto.Versions.AsEnumerable().Reverse().ToList();
    }

    public async Task<string> PickLatestAsync(string mcVersion, CancellationToken ct = default)
    {
        var versions = await ListVersionsAsync(mcVersion, ct);
        return versions[0];
    }

    /// <summary>"1.21.5" → "21.5." (the filter NeoForge's API expects).</summary>
    internal static string NeoForgePrefixFor(string mcVersion)
    {
        // NeoForge only started shipping at MC 1.20.2 — earlier releases stayed
        // on Forge. Reject 1.20.1 explicitly so the user sees a useful message.
        if (mcVersion == "1.20.1")
            throw new NeoForgeMetaException("Minecraft 1.20.1은 NeoForge 미지원. Forge를 사용해 주세요.");

        var parts = mcVersion.Split('.');
        if (parts.Length < 2 || parts[0] != "1")
            throw new NeoForgeMetaException(
                $"NeoForge 버전 매핑을 만들지 못했습니다: {mcVersion}");
        var minor = parts[1];                       // e.g. "21"
        var patch = parts.Length >= 3 ? parts[2] : "0";  // e.g. "5"
        return $"{minor}.{patch}.";                 // → "21.5."
    }

    private sealed record VersionsResponse(
        [property: JsonPropertyName("isSnapshot")] bool IsSnapshot,
        [property: JsonPropertyName("versions")] List<string>? Versions);
}
