using System.Text.Json;
using Launcher.Core.Common;
using Launcher.Core.Modloaders;
using Launcher.Core.Modloaders.Fabric;
using Launcher.Core.Modloaders.NeoForge;
using Launcher.Core.Versions.Assets;
using Launcher.Core.Versions.Libraries;
using Launcher.Core.Versions.Mojang;
using Launcher.Core.Versions.Rules;

namespace Launcher.Core.Installation;

public sealed record InstallResult(
    VersionJson VersionJson,
    IReadOnlyList<PlannedLibrary> Libraries,
    string ClientJarPath,
    string AssetIndexId,
    int AssetCount,
    string? LoggingConfigPath);

public sealed class InstallService(Downloader downloader, int parallelism = 8)
{
    public async Task<InstallResult> InstallVanillaAsync(string versionId, CancellationToken ct = default)
    {
        Paths.EnsureBaseDirectories();

        Log.Step($"Resolving version: {versionId}");
        var manifestSvc = new VersionManifestService(downloader);
        var entry = await manifestSvc.FindAsync(versionId, ct);

        Log.Step("Fetching version JSON");
        var versionFetcher = new VersionFetcher(downloader);
        var versionJson = await versionFetcher.FetchAsync(entry, ct);
        Log.Detail($"mainClass = {versionJson.MainClass}");
        Log.Detail($"javaVersion = {versionJson.JavaVersion.Component} (Java {versionJson.JavaVersion.MajorVersion})");

        return await CompleteInstallAsync(versionJson, ct);
    }

    /// <summary>
    /// Overlay Fabric on top of an already-installed vanilla version. Caller is
    /// responsible for the vanilla step (see <see cref="InstallVanillaAsync"/>).
    /// </summary>
    public async Task<InstallResult> ApplyFabricAsync(InstallResult vanilla, CancellationToken ct = default)
    {
        var mcVersionId = vanilla.VersionJson.Id;
        var meta = new FabricMetaClient(downloader);
        Log.Step("Resolving Fabric loader");
        var loaderVersion = await meta.PickLatestStableLoaderAsync(mcVersionId, ct);
        Log.Detail($"loader: {loaderVersion}");

        Log.Step("Fetching Fabric profile");
        var profile = await meta.FetchProfileAsync(mcVersionId, loaderVersion, ct);

        return await ApplyOverlayAsync(vanilla, profile, "Fabric", ct);
    }

    /// <summary>
    /// Overlay NeoForge on top of an already-installed vanilla version.
    /// Requires a working <paramref name="javaExecutable"/> because NeoForge's
    /// installer is itself a Java program. Caller installs both first (vanilla
    /// via <see cref="InstallVanillaAsync"/>, JRE via
    /// <see cref="Java.JreInstaller.InstallAsync"/>).
    /// </summary>
    public async Task<InstallResult> ApplyNeoForgeAsync(
        InstallResult vanilla, string javaExecutable, CancellationToken ct = default)
    {
        var mcVersionId = vanilla.VersionJson.Id;
        var meta = new NeoForgeMetaClient(downloader);
        Log.Step("Resolving NeoForge version");
        var neoforgeVersion = await meta.PickLatestAsync(mcVersionId, ct);
        Log.Detail($"neoforge: {neoforgeVersion}");

        Log.Step("Running NeoForge installer");
        var installer = new NeoForgeInstaller(downloader);
        var nfResult = await installer.InstallAsync(
            mcVersionId, neoforgeVersion, javaExecutable, progress: null, ct);

        return await ApplyOverlayAsync(vanilla, nfResult.Profile, "NeoForge", ct);
    }

    private async Task<InstallResult> ApplyOverlayAsync(
        InstallResult vanilla, IModloaderOverlayProfile profile, string loaderName, CancellationToken ct)
    {
        var merged = ModloaderOverlayMerger.Merge(vanilla.VersionJson, profile);
        SaveMergedJson(merged);
        Log.Detail($"merged id: {merged.Id}");

        // Install only the additional libraries the overlay adds — vanilla
        // libs already live on disk after InstallVanillaAsync.
        var ctx = RuleContext.Detect();
        var allPlanned = await LibraryPlanner.PlanAsync(merged.Libraries, ctx, downloader, ct);
        var extra = allPlanned.Where(p => !File.Exists(p.LocalPath)).ToList();
        Log.Step($"Downloading {extra.Count} {loaderName} libraries");
        await DownloadManyAsync(
            extra,
            p => (p.Artifact.Url, p.LocalPath, p.Artifact.Sha1),
            ct);

        return vanilla with
        {
            VersionJson = merged,
            Libraries = allPlanned,
        };
    }

    private async Task<InstallResult> CompleteInstallAsync(VersionJson versionJson, CancellationToken ct)
    {
        var ctx = RuleContext.Detect();
        Log.Step($"Planning libraries for {ctx.Os}/{ctx.Arch} {ctx.OsVersion}");
        var planned = await LibraryPlanner.PlanAsync(versionJson.Libraries, ctx, downloader, ct);
        Log.Detail($"libraries kept after rule filter: {planned.Count} / {versionJson.Libraries.Count}");

        Log.Step("Downloading libraries");
        await DownloadManyAsync(
            planned,
            p => (p.Artifact.Url, p.LocalPath, p.Artifact.Sha1),
            ct);

        Log.Step("Downloading client jar");
        var clientJar = Paths.VersionJar(versionJson.Id);
        await downloader.DownloadToFileAsync(
            versionJson.Downloads.Client.Url,
            clientJar,
            versionJson.Downloads.Client.Sha1,
            null,
            ct);

        Log.Step("Fetching asset index");
        var assetSvc = new AssetIndexFetcher(downloader);
        var assetIndex = await assetSvc.FetchAsync(versionJson.AssetIndex, ct);
        Log.Detail($"asset objects: {assetIndex.Objects.Count}");

        Log.Step("Downloading assets");
        await DownloadManyAsync(
            assetIndex.Objects.Values.DistinctBy(o => o.Hash),
            obj => (
                new Uri($"https://resources.download.minecraft.net/{obj.Hash[..2]}/{obj.Hash}"),
                Paths.AssetObject(obj.Hash),
                obj.Hash),
            ct);

        string? loggingPath = null;
        if (versionJson.Logging is { Client: var client })
        {
            Log.Step("Downloading log4j configuration");
            loggingPath = Path.Combine(Paths.Assets, "log_configs", client.File.Id);
            await downloader.DownloadToFileAsync(client.File.Url, loggingPath, client.File.Sha1, null, ct);
        }

        return new InstallResult(
            versionJson,
            planned,
            clientJar,
            versionJson.AssetIndex.Id,
            assetIndex.Objects.Count,
            loggingPath);
    }

    private static void SaveMergedJson(VersionJson merged)
    {
        var dir = Paths.VersionDir(merged.Id);
        Directory.CreateDirectory(dir);
        var path = Paths.VersionJson(merged.Id);
        File.WriteAllText(path, JsonSerializer.Serialize(merged, JsonOptions.Default));
    }

    private async Task DownloadManyAsync<T>(
        IEnumerable<T> items,
        Func<T, (Uri Url, string DestPath, string Sha1)> selector,
        CancellationToken ct)
    {
        var list = items.ToList();
        if (list.Count == 0) return;

        var done = 0;
        var total = list.Count;
        await Parallel.ForEachAsync(
            list,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (item, c) =>
            {
                var (url, dest, sha) = selector(item);
                await downloader.DownloadToFileAsync(url, dest, sha, null, c);
                var n = Interlocked.Increment(ref done);
                if (n % 50 == 0 || n == total)
                    Log.Detail($"  {n}/{total}");
            });
    }
}
