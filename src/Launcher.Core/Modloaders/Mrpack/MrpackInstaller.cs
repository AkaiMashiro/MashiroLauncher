using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Core.Common;
using Launcher.Core.Launching;

namespace Launcher.Core.Modloaders.Mrpack;

public sealed record MrpackProgress(string Stage, int Done, int Total);

public class MrpackException(string message) : Exception(message);

/// <summary>
/// Result of a successful import. Caller registers an <see cref="Instances.Instance"/>
/// with these values.
/// </summary>
public sealed record MrpackImportResult(
    string ModpackName,
    string? Summary,
    string McVersion,
    Modloader Modloader,
    string? LoaderVersion,
    int ModFileCount,
    int OverrideFileCount,
    int FailedDownloads);

/// <summary>
/// Imports a Modrinth modpack (<c>.mrpack</c>) into an instance directory.
///
/// A .mrpack is a zip with:
///   - <c>modrinth.index.json</c>     — the manifest (mc version, loader, file list)
///   - <c>overrides/</c>              — opt-in config / resourcepacks / etc, copied as-is
///   - <c>client-overrides/</c>       — client-only variant
///
/// Each file entry has one or more CDN download URLs; we try them in order
/// and continue past per-file failures rather than aborting the whole import
/// (the user can re-run "+ 외부 jar 가져오기" for stragglers).
/// </summary>
public sealed class MrpackInstaller(HttpClient http)
{
    public async Task<MrpackImportResult> ImportAsync(
        string mrpackPath,
        string instanceId,
        IProgress<MrpackProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(mrpackPath))
            throw new MrpackException($"파일이 존재하지 않습니다: {mrpackPath}");

        using var archive = ZipFile.OpenRead(mrpackPath);

        // ---- 1. Manifest ----------------------------------------------------
        var manifestEntry = archive.GetEntry("modrinth.index.json")
            ?? throw new MrpackException("modrinth.index.json이 없습니다 — 올바른 Modrinth 모드팩이 아닙니다.");

        MrpackManifest manifest;
        await using (var stream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<MrpackManifest>(stream, JsonOpts, ct)
                       ?? throw new MrpackException("manifest 파싱 실패.");
        }

        if (!string.Equals(manifest.Game, "minecraft", StringComparison.OrdinalIgnoreCase))
            throw new MrpackException($"지원되지 않는 게임: {manifest.Game}");

        // ---- 2. Resolve MC version + modloader from dependencies ------------
        if (!manifest.Dependencies.TryGetValue("minecraft", out var mcVersion) || string.IsNullOrEmpty(mcVersion))
            throw new MrpackException("dependencies에 minecraft 버전이 없습니다.");

        var (modloader, loaderVersion) = ResolveLoader(manifest.Dependencies);

        // ---- 3. Prepare instance game dir ----------------------------------
        var gameDir = Paths.InstanceGameDir(instanceId);
        Directory.CreateDirectory(gameDir);

        // ---- 4. Download mod files in parallel -----------------------------
        // Filter out server-only files; we're a client launcher.
        var clientFiles = manifest.Files
            .Where(f => !string.Equals(f.Env?.Client, "unsupported", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Downloads is { Count: > 0 })
            .Where(f => IsSafeRelativePath(f.Path))
            .ToList();

        progress?.Report(new MrpackProgress("모드 다운로드 중", 0, clientFiles.Count));
        var done = 0;
        var failed = 0;
        await Parallel.ForEachAsync(clientFiles,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (file, fct) =>
            {
                var dest = Path.Combine(gameDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                var ok = false;
                foreach (var url in file.Downloads)
                {
                    try
                    {
                        var temp = dest + ".part";
                        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, fct))
                        {
                            resp.EnsureSuccessStatusCode();
                            await using var src = await resp.Content.ReadAsStreamAsync(fct);
                            await using var fs = File.Create(temp);
                            await src.CopyToAsync(fs, fct);
                        }
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(temp, dest);
                        ok = true;
                        break;
                    }
                    catch
                    {
                        // Try next mirror.
                    }
                }
                if (!ok) Interlocked.Increment(ref failed);

                var n = Interlocked.Increment(ref done);
                progress?.Report(new MrpackProgress($"모드 다운로드 ({n}/{clientFiles.Count})", n, clientFiles.Count));
            });

        // ---- 5. Extract overrides ------------------------------------------
        progress?.Report(new MrpackProgress("설정 파일 복사 중", clientFiles.Count, clientFiles.Count));
        var overrideCount = 0;
        foreach (var prefix in new[] { "overrides/", "client-overrides/" })
        {
            foreach (var entry in archive.Entries.Where(e =>
                e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                var relative = entry.FullName[prefix.Length..];
                if (string.IsNullOrEmpty(relative)) continue;
                if (!IsSafeRelativePath(relative)) continue;

                var dest = Path.Combine(gameDir, relative.Replace('/', Path.DirectorySeparatorChar));

                // Directory entry (zips often store explicit empty dir entries ending with /).
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await using var src = entry.Open();
                await using var fs = File.Create(dest);
                await src.CopyToAsync(fs, ct);
                overrideCount++;
            }
        }

        progress?.Report(new MrpackProgress("완료", clientFiles.Count, clientFiles.Count));

        return new MrpackImportResult(
            ModpackName: manifest.Name,
            Summary: manifest.Summary,
            McVersion: mcVersion,
            Modloader: modloader,
            LoaderVersion: loaderVersion,
            ModFileCount: clientFiles.Count - failed,
            OverrideFileCount: overrideCount,
            FailedDownloads: failed);
    }

    private static (Modloader Loader, string? Version) ResolveLoader(IReadOnlyDictionary<string, string> deps)
    {
        // Order matters — newer modpacks may declare neoforge alongside legacy
        // forge keys; pick the modern one first.
        if (deps.TryGetValue("neoforge", out var nv))       return (Modloader.NeoForge, nv);
        if (deps.TryGetValue("fabric-loader", out var fv))  return (Modloader.Fabric,   fv);
        if (deps.ContainsKey("forge"))
            throw new MrpackException("Forge 모드팩은 현재 미지원입니다. NeoForge 또는 Fabric 모드팩을 사용해 주세요.");
        if (deps.ContainsKey("quilt-loader"))
            throw new MrpackException("Quilt 모드팩은 현재 미지원입니다.");
        return (Modloader.Vanilla, null);
    }

    /// <summary>
    /// Reject paths that would let a malicious .mrpack write outside the
    /// instance's game dir (e.g. <c>../../Windows/System32/...</c> or
    /// absolute paths).
    /// </summary>
    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (Path.IsPathRooted(path)) return false;
        if (path.Contains("..")) return false;
        // Backslash check guards against Windows-style escapes embedded in zip paths.
        var parts = path.Split('/', '\\');
        return parts.All(p => p != ".." && !string.IsNullOrEmpty(p));
    }

    // Modrinth's JSON is camelCase but stable; we map property names explicitly
    // so renamed fields don't silently drop on the floor.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record MrpackManifest(
        [property: JsonPropertyName("formatVersion")] int FormatVersion,
        [property: JsonPropertyName("game")] string Game,
        [property: JsonPropertyName("versionId")] string VersionId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("files")] List<MrpackFile> Files,
        [property: JsonPropertyName("dependencies")] Dictionary<string, string> Dependencies);

    private sealed record MrpackFile(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("hashes")] Dictionary<string, string>? Hashes,
        [property: JsonPropertyName("downloads")] List<string> Downloads,
        [property: JsonPropertyName("fileSize")] long FileSize,
        [property: JsonPropertyName("env")] MrpackEnv? Env);

    private sealed record MrpackEnv(
        [property: JsonPropertyName("client")] string? Client,
        [property: JsonPropertyName("server")] string? Server);
}
