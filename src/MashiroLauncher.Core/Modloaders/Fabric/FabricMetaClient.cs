using System.Text.Json;
using System.Text.Json.Serialization;
using MashiroLauncher.Core.Common;

namespace MashiroLauncher.Core.Modloaders.Fabric;

public sealed record FabricLoaderEntry(FabricLoaderInfo Loader, FabricIntermediaryInfo Intermediary);
public sealed record FabricLoaderInfo(string Maven, string Version, bool Stable);
public sealed record FabricIntermediaryInfo(string Maven, string Version);

public class FabricMetaException(string message) : Exception(message);

public sealed class FabricMetaClient(Downloader downloader)
{
    private const string Base = "https://meta.fabricmc.net/v2";

    // Returns loader versions for the given MC version, newest first.
    public async Task<IReadOnlyList<FabricLoaderEntry>> ListLoadersAsync(
        string mcVersion, CancellationToken ct = default)
    {
        var url = new Uri($"{Base}/versions/loader/{Uri.EscapeDataString(mcVersion)}");
        var json = await downloader.FetchTextAsync(url, ct);
        var entries = JsonSerializer.Deserialize<List<RawLoaderEntry>>(json, JsonOptions.Default)
                      ?? throw new FabricMetaException("Fabric loader 목록을 파싱하지 못했습니다.");
        if (entries.Count == 0)
            throw new FabricMetaException($"Fabric이 아직 Minecraft {mcVersion}을(를) 지원하지 않습니다.");
        return entries
            .Select(e => new FabricLoaderEntry(
                new FabricLoaderInfo(e.Loader.Maven, e.Loader.Version, e.Loader.Stable),
                new FabricIntermediaryInfo(e.Intermediary.Maven, e.Intermediary.Version)))
            .ToList();
    }

    public async Task<string> PickLatestStableLoaderAsync(
        string mcVersion, CancellationToken ct = default)
    {
        var loaders = await ListLoadersAsync(mcVersion, ct);
        var stable = loaders.FirstOrDefault(l => l.Loader.Stable);
        return (stable ?? loaders[0]).Loader.Version;
    }

    public async Task<FabricProfile> FetchProfileAsync(
        string mcVersion, string loaderVersion, CancellationToken ct = default)
    {
        var url = new Uri($"{Base}/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json");
        var json = await downloader.FetchTextAsync(url, ct);
        var profile = JsonSerializer.Deserialize<FabricProfile>(json, JsonOptions.Default)
                      ?? throw new FabricMetaException("Fabric profile JSON 파싱 실패.");
        return profile;
    }

    private sealed record RawLoaderEntry(
        [property: JsonPropertyName("loader")] RawLoader Loader,
        [property: JsonPropertyName("intermediary")] RawIntermediary Intermediary);

    private sealed record RawLoader(string Maven, string Version, bool Stable);
    private sealed record RawIntermediary(string Maven, string Version);
}
