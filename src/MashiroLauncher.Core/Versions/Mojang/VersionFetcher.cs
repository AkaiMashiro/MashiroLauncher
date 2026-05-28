using System.Text.Json;
using MashiroLauncher.Core.Common;

namespace MashiroLauncher.Core.Versions.Mojang;

public sealed class VersionFetcher(Downloader downloader)
{
    public async Task<VersionJson> FetchAsync(VersionManifestEntry entry, CancellationToken ct = default)
    {
        var dest = Paths.VersionJson(entry.Id);
        await downloader.DownloadToFileAsync(entry.Url, dest, entry.Sha1, null, ct);

        await using var fs = File.OpenRead(dest);
        var versionJson = await JsonSerializer.DeserializeAsync<VersionJson>(fs, JsonOptions.Default, ct)
            ?? throw new InvalidOperationException($"Failed to parse version JSON: {dest}");
        return versionJson;
    }
}
