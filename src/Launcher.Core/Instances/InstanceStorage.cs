using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Core.Common;
using Launcher.Core.Launching;

namespace Launcher.Core.Instances;

public sealed class InstanceStorage
{
    private static string IndexPath => Path.Combine(Paths.Data, "instances.json");

    // Instances are user-editable JSON. Pretty-print + string enums so the file
    // is readable if anyone opens it.
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public List<Instance> Load()
    {
        if (!File.Exists(IndexPath)) return [];
        try
        {
            var json = File.ReadAllText(IndexPath);
            return JsonSerializer.Deserialize<List<Instance>>(json, Opts) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SaveAll(IEnumerable<Instance> instances)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!);
        File.WriteAllText(IndexPath, JsonSerializer.Serialize(instances.ToList(), Opts));
    }

    /// <summary>
    /// First-run import: scan data/instances/* for folders that aren't already in the
    /// index. Useful when upgrading from a pre-instance-manager launcher state where
    /// vanilla and Fabric runs left folders behind named "{versionId}" / "{versionId}-fabric".
    /// </summary>
    public List<Instance> ScanFilesystem(IEnumerable<string> knownIds)
    {
        if (!Directory.Exists(Paths.Instances)) return [];
        var known = knownIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<Instance>();

        foreach (var dir in Directory.EnumerateDirectories(Paths.Instances))
        {
            var folderName = Path.GetFileName(dir);
            if (known.Contains(folderName)) continue;
            // Heuristic: only count folders that look like a real instance
            // (have a game subdirectory the launcher would have created).
            if (!Directory.Exists(Path.Combine(dir, "game"))) continue;

            // Folder name heuristic: matches the naming LaunchPipeline.PrepareAsync
            // generates when an unnamed quick launch creates an instance dir.
            //   "1.21.5"           → vanilla
            //   "1.21.5-fabric"    → Fabric
            //   "1.21.5-neoforge"  → NeoForge
            var (modloader, versionId, suffixLabel) = folderName switch
            {
                var n when n.EndsWith("-neoforge", StringComparison.OrdinalIgnoreCase)
                    => (Modloader.NeoForge, n[..^"-neoforge".Length], "NeoForge"),
                var n when n.EndsWith("-fabric", StringComparison.OrdinalIgnoreCase)
                    => (Modloader.Fabric, n[..^"-fabric".Length], "Fabric"),
                _ => (Modloader.Vanilla, folderName, ""),
            };

            found.Add(new Instance
            {
                Id = folderName,
                Name = suffixLabel.Length == 0 ? versionId : $"{versionId} ({suffixLabel})",
                VersionId = versionId,
                Modloader = modloader,
                CreatedAt = Directory.GetCreationTime(dir),
                LastPlayedAt = null,
            });
        }
        return found;
    }

    /// <summary>
    /// Derives a folder-safe id from a display name. Strips non-ASCII letters/digits
    /// (so e.g. "나의 서버" → "instance") and avoids collisions with <paramref name="existing"/>
    /// by appending "-2", "-3", ...
    /// </summary>
    public static string GenerateId(string name, IEnumerable<string> existing)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (c < 128 && char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is ' ' or '-' or '_' or '.') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (string.IsNullOrEmpty(slug)) slug = "instance";

        var existingSet = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existingSet.Contains(slug)) return slug;
        for (var n = 2; ; n++)
        {
            var candidate = $"{slug}-{n}";
            if (!existingSet.Contains(candidate)) return candidate;
        }
    }
}
