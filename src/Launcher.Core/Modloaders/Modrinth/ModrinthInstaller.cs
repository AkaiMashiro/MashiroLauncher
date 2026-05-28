using System.Text.Json;
using Launcher.Core.Common;

namespace Launcher.Core.Modloaders.Modrinth;

public sealed record ModInstallProgress(string Stage, int Done, int Total);

/// <summary>
/// Downloads a Modrinth project's latest compatible version into an instance's
/// <c>mods/</c> folder, recursively installing required dependencies. Files
/// are picked by the project's "primary" flag when available.
///
/// A small <c>.modrinth.json</c> manifest in the mods folder records which
/// project id corresponds to which on-disk file, so the UI can mark search
/// results as 설치됨 and uninstall both the .jar and the manifest entry.
/// </summary>
public sealed class ModrinthInstaller(HttpClient http)
{
    private const string ManifestFile = ".modrinth.json";
    private static readonly JsonSerializerOptions ManifestOpts = new() { WriteIndented = true };
    private readonly ModrinthClient _client = new(http);

    /// <summary>
    /// Install the latest version of <paramref name="projectIdOrSlug"/> that
    /// is compatible with <paramref name="mcVersion"/> + <paramref name="loader"/>,
    /// recursively pulling required deps. This is the default "설치" path from
    /// the search card.
    /// </summary>
    public Task InstallAsync(
        string projectIdOrSlug,
        string instanceId,
        string mcVersion,
        string loader,
        IProgress<ModInstallProgress>? progress = null,
        CancellationToken ct = default)
        => RunInstallAsync(projectIdOrSlug, pinnedVersionId: null, instanceId, mcVersion, loader, progress, ct);

    /// <summary>
    /// Like <see cref="InstallAsync"/> but pins the root project to a specific
    /// Modrinth version (used by the detail view's version picker). Dependencies
    /// of the pinned version still resolve to their latest compatible build.
    /// </summary>
    public Task InstallVersionAsync(
        string projectIdOrSlug,
        string versionId,
        string instanceId,
        string mcVersion,
        string loader,
        IProgress<ModInstallProgress>? progress = null,
        CancellationToken ct = default)
        => RunInstallAsync(projectIdOrSlug, versionId, instanceId, mcVersion, loader, progress, ct);

    private async Task RunInstallAsync(
        string rootProjectIdOrSlug,
        string? pinnedVersionId,
        string instanceId,
        string mcVersion,
        string loader,
        IProgress<ModInstallProgress>? progress,
        CancellationToken ct)
    {
        var modsDir = Path.Combine(Paths.InstanceGameDir(instanceId), "mods");
        Directory.CreateDirectory(modsDir);
        var manifest = ReadManifest(modsDir);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(rootProjectIdOrSlug);
        var rootKey = rootProjectIdOrSlug;  // identifies which queue item should use the pinned version

        var total = 0;
        var done = 0;

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id)) continue;

            ModrinthProject? project = null;
            try { project = await _client.GetProjectAsync(id, ct); }
            catch (ModrinthException) { continue; }

            var displayName = project.Title;
            total++;
            progress?.Report(new ModInstallProgress($"{displayName} 확인 중", done, total));

            // For the root project, prefer the pinned version when given; for
            // every transitive dep, always pick the newest compatible build.
            ModrinthVersion? chosen = null;
            if (pinnedVersionId is not null
                && string.Equals(id, rootKey, StringComparison.OrdinalIgnoreCase))
            {
                try { chosen = await _client.GetVersionAsync(pinnedVersionId, ct); }
                catch (ModrinthException) { chosen = null; }
            }
            if (chosen is null)
            {
                var versions = await _client.GetVersionsAsync(id, mcVersion, loader, ct);
                if (versions.Count == 0)
                {
                    progress?.Report(new ModInstallProgress(
                        $"{displayName}: {mcVersion}/{loader} 호환 버전 없음", done, total));
                    continue;
                }
                chosen = versions[0];
            }

            var primary = chosen.Files.FirstOrDefault(f => f.Primary) ?? chosen.Files.FirstOrDefault();
            if (primary is null) continue;

            var dest = Path.Combine(modsDir, primary.Filename);
            if (!File.Exists(dest))
            {
                progress?.Report(new ModInstallProgress($"{displayName} 다운로드 중", done, total));
                await DownloadAsync(primary.Url, dest, ct);
            }

            // Record canonical project id → (filename, versionId). Storing the
            // version unlocks "is there a newer version?" checks without
            // re-fetching every list call.
            manifest[project.Id] = new ManifestEntry(primary.Filename, chosen.Id);

            foreach (var dep in chosen.Dependencies)
            {
                if (dep.DependencyType != "required") continue;
                if (string.IsNullOrEmpty(dep.ProjectId)) continue;
                if (!seen.Contains(dep.ProjectId))
                    queue.Enqueue(dep.ProjectId);
            }

            done++;
            progress?.Report(new ModInstallProgress($"{displayName} 완료", done, total));
        }

        WriteManifest(modsDir, manifest);
    }

    /// <summary>One entry in the installed-mods listing — exposes the on-disk
    /// filename (may end in <c>.jar.disabled</c>) and whether it's currently
    /// active. Manifest keys always store the enabled-form filename so a
    /// disabled jar can be matched back to its Modrinth project.</summary>
    public sealed record InstalledMod(string Filename, bool IsEnabled)
    {
        /// <summary>Filename normalized to the active (.jar) form for manifest lookup.</summary>
        public string EnabledFilename => IsEnabled
            ? Filename
            : Filename[..^DisabledSuffix.Length];
    }

    private const string DisabledSuffix = ".disabled";

    /// <summary>List all mod files in the instance's mods folder — both active
    /// .jar and disabled .jar.disabled entries, sorted by name.</summary>
    public IReadOnlyList<InstalledMod> ListInstalled(string instanceId)
    {
        var modsDir = Path.Combine(Paths.InstanceGameDir(instanceId), "mods");
        if (!Directory.Exists(modsDir)) return Array.Empty<InstalledMod>();

        var entries = new List<InstalledMod>();
        foreach (var path in Directory.GetFiles(modsDir))
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) continue;
            if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                entries.Add(new InstalledMod(name, IsEnabled: true));
            else if (name.EndsWith(".jar" + DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                entries.Add(new InstalledMod(name, IsEnabled: false));
        }
        return entries
            .OrderBy(e => e.EnabledFilename, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>filename → projectId mapping for tracked mods. Keys use the
    /// on-disk filename (including .disabled suffix when disabled) so the UI
    /// can look up project info regardless of state.</summary>
    public IReadOnlyDictionary<string, string> GetFilenameToProjectIdMap(string instanceId)
    {
        var modsDir = Path.Combine(Paths.InstanceGameDir(instanceId), "mods");
        var manifest = ReadManifest(modsDir);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, entry) in manifest)
        {
            // Manifest stores the enabled-form filename. The mod might be on
            // disk as either .jar or .jar.disabled — expose whichever exists.
            var enabledPath  = Path.Combine(modsDir, entry.Filename);
            var disabledPath = enabledPath + DisabledSuffix;
            if (File.Exists(enabledPath))
                result[entry.Filename] = pid;
            else if (File.Exists(disabledPath))
                result[entry.Filename + DisabledSuffix] = pid;
        }
        return result;
    }

    /// <summary>Set of Modrinth project ids we know are installed (either active
    /// or disabled) in this instance.</summary>
    public IReadOnlySet<string> GetInstalledProjectIds(string instanceId)
    {
        var modsDir = Path.Combine(Paths.InstanceGameDir(instanceId), "mods");
        var manifest = ReadManifest(modsDir);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, entry) in manifest)
        {
            var enabledPath = Path.Combine(modsDir, entry.Filename);
            if (File.Exists(enabledPath) || File.Exists(enabledPath + DisabledSuffix))
                result.Add(pid);
        }
        return result;
    }

    /// <summary>projectId → installed versionId. Useful for "is there an
    /// update?" comparisons against Modrinth's version list.</summary>
    public IReadOnlyDictionary<string, string> GetInstalledVersionMap(string instanceId)
    {
        var modsDir = Path.Combine(Paths.InstanceGameDir(instanceId), "mods");
        var manifest = ReadManifest(modsDir);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, entry) in manifest)
        {
            // Only meaningful when both the file exists and we know what
            // version it was; legacy-migrated entries have VersionId=null and
            // can be filled in by re-installing the mod through the launcher.
            var enabledPath = Path.Combine(modsDir, entry.Filename);
            if ((File.Exists(enabledPath) || File.Exists(enabledPath + DisabledSuffix))
                && entry.VersionId is not null)
            {
                result[pid] = entry.VersionId;
            }
        }
        return result;
    }

    public void Uninstall(string instanceId, string filename)
    {
        if (filename.Contains('/') || filename.Contains('\\') || filename.Contains("..")) return;
        var modsDir = Path.Combine(Paths.InstanceGameDir(instanceId), "mods");
        var path = Path.Combine(modsDir, filename);
        if (File.Exists(path)) File.Delete(path);

        // Drop the matching manifest entry so the UI no longer shows 설치됨.
        // The manifest key is always the enabled-form filename, so strip the
        // .disabled suffix before matching when the caller passed a disabled
        // file's name.
        var manifestKeyForFile = filename.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
            ? filename[..^DisabledSuffix.Length]
            : filename;
        var manifest = ReadManifest(modsDir);
        var orphans = manifest
            .Where(kv => string.Equals(kv.Value.Filename, manifestKeyForFile, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();
        if (orphans.Count > 0)
        {
            foreach (var k in orphans) manifest.Remove(k);
            WriteManifest(modsDir, manifest);
        }
    }

    /// <summary>
    /// Query Modrinth for newer compatible versions of every tracked mod in
    /// the instance. Returns projectId → latest <see cref="ModrinthVersion"/>
    /// when an update is available. Mods at the latest version, whose installed
    /// versionId we don't know (legacy manifest), or whose API call fails are
    /// silently omitted — a failing single mod can't break the whole check.
    /// Parallelism is capped at 8 concurrent requests to stay friendly with
    /// Modrinth's rate limiter.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ModrinthVersion>> CheckUpdatesAsync(
        string instanceId, string mcVersion, string loader, CancellationToken ct = default)
    {
        var installedVersions = GetInstalledVersionMap(instanceId);
        if (installedVersions.Count == 0)
            return new Dictionary<string, ModrinthVersion>();

        using var sem = new SemaphoreSlim(8);
        var tasks = installedVersions.Select(async kv =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var versions = await _client.GetVersionsAsync(kv.Key, mcVersion, loader, ct);
                if (versions.Count == 0) return (Pid: kv.Key, Latest: (ModrinthVersion?)null);
                var latest = versions[0];
                var sameAsInstalled = string.Equals(latest.Id, kv.Value, StringComparison.OrdinalIgnoreCase);
                return (Pid: kv.Key, Latest: sameAsInstalled ? null : latest);
            }
            catch
            {
                return (Pid: kv.Key, Latest: (ModrinthVersion?)null);
            }
            finally { sem.Release(); }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        var updates = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, latest) in results)
        {
            if (latest is not null) updates[pid] = latest;
        }
        return updates;
    }

    /// <summary>
    /// Flip the active state of a mod. When <paramref name="enabled"/> is true
    /// a <c>.jar.disabled</c> file is renamed back to <c>.jar</c>; when false
    /// the opposite. Caller passes the current on-disk filename.
    /// </summary>
    /// <returns>The new on-disk filename, or the original if no rename happened.</returns>
    public string SetEnabled(string instanceId, string filename, bool enabled)
    {
        if (filename.Contains('/') || filename.Contains('\\') || filename.Contains("..")) return filename;
        var modsDir = Path.Combine(Paths.InstanceGameDir(instanceId), "mods");
        var srcPath = Path.Combine(modsDir, filename);
        if (!File.Exists(srcPath)) return filename;

        var isCurrentlyEnabled = !filename.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
        if (isCurrentlyEnabled == enabled) return filename;  // no-op

        var newName = enabled
            ? filename[..^DisabledSuffix.Length]              // strip ".disabled"
            : filename + DisabledSuffix;                       // append ".disabled"
        var dstPath = Path.Combine(modsDir, newName);

        // If a sibling with the target name already exists (rare; manual setup),
        // bail rather than clobber.
        if (File.Exists(dstPath)) return filename;
        File.Move(srcPath, dstPath);
        return newName;
    }

    // ---- Manifest I/O ------------------------------------------------------
    //
    // Schema v2 stores per-project: { filename, versionId }. The versionId
    // unlocks "is there a newer version on Modrinth?" checks without us
    // having to hash the file or fingerprint contents.
    //
    // A v1 manifest from before this change is a flat dict of project → filename.
    // We detect it by the absence of "schemaVersion" and migrate on first load.

    public sealed record ManifestEntry(string Filename, string? VersionId);

    private sealed record ManifestEnvelope(
        int SchemaVersion,
        Dictionary<string, ManifestEntry> Entries);

    private const int CurrentManifestVersion = 2;

    private static Dictionary<string, ManifestEntry> ReadManifest(string modsDir)
    {
        var path = Path.Combine(modsDir, ManifestFile);
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);

        string json;
        try { json = File.ReadAllText(path); }
        catch { return new(StringComparer.OrdinalIgnoreCase); }

        // Try v2 envelope first.
        try
        {
            var envelope = JsonSerializer.Deserialize<ManifestEnvelope>(json, ManifestOpts);
            if (envelope is { SchemaVersion: >= CurrentManifestVersion, Entries: not null })
            {
                // Force case-insensitive lookups (Modrinth project ids are case-insensitive in practice).
                return new Dictionary<string, ManifestEntry>(envelope.Entries, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (JsonException) { /* fall through to legacy attempt */ }

        // Legacy v1: flat dict of projectId → filename, no version info.
        try
        {
            var legacy = JsonSerializer.Deserialize<Dictionary<string, string>>(json, ManifestOpts);
            if (legacy is null) return new(StringComparer.OrdinalIgnoreCase);
            var migrated = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (pid, filename) in legacy)
                migrated[pid] = new ManifestEntry(filename, VersionId: null);
            // Best-effort: persist the migrated form so subsequent loads take the v2 path.
            try { WriteManifest(modsDir, migrated); } catch { /* swallow */ }
            return migrated;
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void WriteManifest(string modsDir, Dictionary<string, ManifestEntry> manifest)
    {
        try
        {
            Directory.CreateDirectory(modsDir);
            var path = Path.Combine(modsDir, ManifestFile);
            var envelope = new ManifestEnvelope(CurrentManifestVersion, manifest);
            File.WriteAllText(path, JsonSerializer.Serialize(envelope, ManifestOpts));
        }
        catch
        {
            // Best-effort; if we can't write, the UI just won't mark things 설치됨 next session.
        }
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var tempPath = destination + ".part";
        try
        {
            await using (var fs = File.Create(tempPath))
            await using (var stream = await resp.Content.ReadAsStreamAsync(ct))
            {
                await stream.CopyToAsync(fs, ct);
            }
            File.Move(tempPath, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
