using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Core.Common;
using Launcher.Core.Launching;

namespace Launcher.Core.Instances;

public class InstanceBackupException(string message) : Exception(message);

/// <summary>
/// Self-describing on-disk record we drop into the zip so import can rebuild
/// the <see cref="Instance"/> faithfully. Id is intentionally NOT stored —
/// the importer assigns a fresh folder-safe id to avoid colliding with
/// whatever the user already has.
/// </summary>
public sealed record InstanceBackupManifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("versionId")] string VersionId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    [property: JsonPropertyName("modloader")] Modloader Modloader,
    [property: JsonPropertyName("minMemoryMb")] int? MinMemoryMb,
    [property: JsonPropertyName("maxMemoryMb")] int? MaxMemoryMb,
    [property: JsonPropertyName("customJvmArgs")] string? CustomJvmArgs,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("exportedAt")] DateTimeOffset ExportedAt,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion = 1);

/// <summary>
/// Zips an entire instance directory (including the game/ subfolder with worlds,
/// mods, configs, options.txt) into a portable archive and the inverse.
///
/// File layout inside the zip:
///   mashiro-instance.json     ← <see cref="InstanceBackupManifest"/>
///   game/...                  ← exact mirror of data/instances/&lt;id&gt;/game
/// </summary>
public sealed class InstanceBackup
{
    private const string ManifestEntryName = "mashiro-instance.json";

    /// <summary>
    /// Stream the instance's directory tree into <paramref name="destZipPath"/>,
    /// prefixed with the on-disk layout so import can reverse it. Overwrites
    /// the target zip if it already exists.
    /// </summary>
    public async Task ExportAsync(
        Instance instance, string destZipPath,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var srcDir = Paths.InstanceDir(instance.Id);
        if (!Directory.Exists(srcDir))
            throw new InstanceBackupException($"인스턴스 폴더가 존재하지 않습니다: {srcDir}");

        Directory.CreateDirectory(Path.GetDirectoryName(destZipPath)!);
        if (File.Exists(destZipPath)) File.Delete(destZipPath);

        progress?.Report("백업 파일 생성 중…");
        await Task.Run(() =>
        {
            using var fs = File.Create(destZipPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            // 1. Manifest first so import can fail fast on malformed zips.
            var manifest = new InstanceBackupManifest(
                Name: instance.Name,
                VersionId: instance.VersionId,
                Modloader: instance.Modloader,
                MinMemoryMb: instance.MinMemoryMb,
                MaxMemoryMb: instance.MaxMemoryMb,
                CustomJvmArgs: instance.CustomJvmArgs,
                CreatedAt: instance.CreatedAt,
                ExportedAt: DateTimeOffset.UtcNow);
            var manifestEntry = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
            using (var stream = manifestEntry.Open())
            using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                writer.Write(JsonSerializer.Serialize(manifest, JsonOpts));
            }

            // 2. Recursively walk srcDir, preserving relative paths.
            AddDirectoryToZip(zip, srcDir, prefix: "", ct, progress);
        }, ct);

        progress?.Report("백업 완료");
    }

    /// <summary>
    /// Pull the archive at <paramref name="srcZipPath"/> apart into a fresh
    /// instance directory under <paramref name="newId"/>. Returns the rebuilt
    /// <see cref="Instance"/> (caller registers it with InstanceStorage).
    /// </summary>
    public async Task<Instance> ImportAsync(
        string srcZipPath, string newId,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(srcZipPath))
            throw new InstanceBackupException($"파일이 존재하지 않습니다: {srcZipPath}");

        return await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(srcZipPath);

            // ---- 1. Manifest ----
            var manifestEntry = zip.GetEntry(ManifestEntryName)
                ?? throw new InstanceBackupException(
                    "올바른 Mashiro 인스턴스 백업이 아닙니다 (manifest 누락).");

            InstanceBackupManifest manifest;
            using (var stream = manifestEntry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                manifest = JsonSerializer.Deserialize<InstanceBackupManifest>(reader.ReadToEnd(), JsonOpts)
                           ?? throw new InstanceBackupException("manifest 파싱 실패.");
            }

            // ---- 2. Extract files ----
            var destDir = Paths.InstanceDir(newId);
            Directory.CreateDirectory(destDir);

            progress?.Report("파일 복원 중…");
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName == ManifestEntryName) continue;

                // Directory entry (zips often store these explicitly).
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                {
                    if (!IsSafeRelativePath(entry.FullName)) continue;
                    var dirDest = Path.Combine(destDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(dirDest);
                    continue;
                }

                if (!IsSafeRelativePath(entry.FullName)) continue;
                var dest = Path.Combine(destDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }

            // ---- 3. Build the Instance — fresh id + fresh CreatedAt ----
            return new Instance
            {
                Id = newId,
                Name = manifest.Name,
                VersionId = manifest.VersionId,
                Modloader = manifest.Modloader,
                CreatedAt = DateTimeOffset.Now,
                LastPlayedAt = null,
                MinMemoryMb = manifest.MinMemoryMb,
                MaxMemoryMb = manifest.MaxMemoryMb,
                CustomJvmArgs = manifest.CustomJvmArgs,
            };
        }, ct);
    }

    private static void AddDirectoryToZip(
        ZipArchive zip, string baseDir, string prefix,
        CancellationToken ct, IProgress<string>? progress)
    {
        var di = new DirectoryInfo(baseDir);
        if (!di.Exists) return;

        foreach (var fi in di.GetFiles())
        {
            ct.ThrowIfCancellationRequested();
            var entryName = prefix + fi.Name;
            // Suppress noisy per-file progress on big instances; a coarse
            // "X files added" message every 50 files is enough feedback.
            zip.CreateEntryFromFile(fi.FullName, entryName, CompressionLevel.Optimal);
        }
        foreach (var sub in di.GetDirectories())
        {
            AddDirectoryToZip(zip, sub.FullName, prefix + sub.Name + "/", ct, progress);
        }
    }

    /// <summary>Reject paths that would escape the target instance dir.</summary>
    internal static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (Path.IsPathRooted(path)) return false;
        if (path.Contains("..")) return false;
        var parts = path.Split('/', '\\');
        return parts.All(p => p != "..");
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}
