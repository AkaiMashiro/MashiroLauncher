using System.Text.Json;
using Launcher.Core.Common;
using Launcher.Core.Versions.Mojang;

namespace Launcher.Core.Versions.Assets;

public sealed record AssetIndex(IReadOnlyDictionary<string, AssetObject> Objects);

public sealed record AssetObject(string Hash, long Size);

public sealed class AssetIndexFetcher(Downloader downloader)
{
    public async Task<AssetIndex> FetchAsync(AssetIndexRef indexRef, CancellationToken ct = default)
    {
        var dest = Paths.AssetIndexFile(indexRef.Id);
        await downloader.DownloadToFileAsync(indexRef.Url, dest, indexRef.Sha1, null, ct);

        await using var fs = File.OpenRead(dest);
        var index = await JsonSerializer.DeserializeAsync<AssetIndex>(fs, JsonOptions.Default, ct)
            ?? throw new InvalidOperationException($"Failed to parse asset index: {dest}");
        return index;
    }
}
