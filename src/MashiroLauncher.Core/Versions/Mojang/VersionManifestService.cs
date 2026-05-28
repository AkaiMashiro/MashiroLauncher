using System.Text.Json;
using MashiroLauncher.Core.Common;

namespace MashiroLauncher.Core.Versions.Mojang;

public class VersionNotFoundException(string id)
    : Exception($"Version not found in Mojang manifest: {id}")
{
    public string Id { get; } = id;
}

public sealed class VersionManifestService(Downloader downloader)
{
    private static readonly Uri ManifestUrl =
        new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");

    private VersionManifest? _cached;

    public async Task<VersionManifest> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;
        var json = await downloader.FetchTextAsync(ManifestUrl, ct);
        var manifest = JsonSerializer.Deserialize<VersionManifest>(json, JsonOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse Mojang version manifest");
        _cached = manifest;
        return manifest;
    }

    public async Task<VersionManifestEntry> FindAsync(string id, CancellationToken ct = default)
    {
        var manifest = await GetAsync(ct);
        return manifest.Versions.FirstOrDefault(v => v.Id == id)
            ?? throw new VersionNotFoundException(id);
    }
}
