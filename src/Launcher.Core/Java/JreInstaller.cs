using System.Text.Json;
using Launcher.Core.Common;
using Launcher.Core.Versions.Rules;

namespace Launcher.Core.Java;

public sealed class JreInstaller(Downloader downloader, int parallelism = 8)
{
    private static readonly Uri AllManifestUrl =
        new("https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json");

    public async Task<string> InstallAsync(string component, OsName os, OsArch arch, CancellationToken ct = default)
    {
        Log.Step($"Resolving JRE component: {component}");
        var entry = await ResolveEntryAsync(component, os, arch, ct);
        Log.Detail($"version: {entry.Version.Name} (released {entry.Version.Released:yyyy-MM-dd})");

        Log.Step("Fetching per-runtime file manifest");
        var manifestJson = await downloader.FetchTextAsync(entry.Manifest.Url, ct);
        var manifest = JsonSerializer.Deserialize<JreFileManifest>(manifestJson, JsonOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse JRE file manifest.");

        var runtimeDir = Paths.RuntimeDir(component);
        Directory.CreateDirectory(runtimeDir);

        // Create directories up front to avoid contention in parallel downloads.
        foreach (var (path, file) in manifest.Files)
        {
            var fullPath = Path.Combine(runtimeDir, path);
            if (file.Type == "directory")
                Directory.CreateDirectory(fullPath);
            else if (file.Type == "file")
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        }

        var downloads = manifest.Files
            .Where(kv => kv.Value.Type == "file" && kv.Value.Downloads?.Raw is not null)
            .ToList();

        Log.Step($"Downloading {downloads.Count} runtime files");
        var done = 0;
        var total = downloads.Count;
        await Parallel.ForEachAsync(
            downloads,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (kv, c) =>
            {
                var fullPath = Path.Combine(runtimeDir, kv.Key);
                var raw = kv.Value.Downloads!.Raw!;
                await downloader.DownloadToFileAsync(raw.Url, fullPath, raw.Sha1, null, c);
                var n = Interlocked.Increment(ref done);
                if (n % 50 == 0 || n == total) Log.Detail($"  {n}/{total}");
            });

        // Symlinks (mostly relevant on macOS/Linux; best-effort on Windows).
        foreach (var (path, file) in manifest.Files.Where(kv => kv.Value.Type == "link" && kv.Value.Target is not null))
        {
            var linkPath = Path.Combine(runtimeDir, path);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
                if (File.Exists(linkPath) || Directory.Exists(linkPath)) continue;
                File.CreateSymbolicLink(linkPath, file.Target!);
            }
            catch (Exception ex)
            {
                Log.Warn($"Symlink skipped ({path} -> {file.Target}): {ex.Message}");
            }
        }

        var javaPath = LocateJavaExecutable(runtimeDir);
        Log.Step($"JRE ready at {javaPath}");
        return javaPath;
    }

    private async Task<JreOsEntry> ResolveEntryAsync(string component, OsName os, OsArch arch, CancellationToken ct)
    {
        var json = await downloader.FetchTextAsync(AllManifestUrl, ct);
        // Mojang's all.json is keyed by OS first, then by component name.
        var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<JreOsEntry>>>>(json, JsonOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse JRE all-manifest.");

        var key = MojangOsKey(os, arch);
        if (!all.TryGetValue(key, out var componentMap))
            throw new PlatformNotSupportedException($"JRE OS key '{key}' not present in manifest.");
        if (!componentMap.TryGetValue(component, out var list) || list.Count == 0)
            throw new InvalidOperationException(
                $"JRE component '{component}' is not available for '{key}'.");
        return list[0];
    }

    private static string MojangOsKey(OsName os, OsArch arch) => (os, arch) switch
    {
        (OsName.Windows, OsArch.X64)   => "windows-x64",
        (OsName.Windows, OsArch.X86)   => "windows-x86",
        (OsName.Windows, OsArch.Arm64) => "windows-arm64",
        (OsName.Linux,   OsArch.X64)   => "linux",
        (OsName.Linux,   OsArch.X86)   => "linux-i386",
        (OsName.Osx,     OsArch.X64)   => "mac-os",
        (OsName.Osx,     OsArch.Arm64) => "mac-os-arm64",
        _ => throw new PlatformNotSupportedException($"No Mojang JRE for {os}/{arch}"),
    };

    public static string LocateJavaExecutable(string runtimeDir)
    {
        if (OperatingSystem.IsWindows())
        {
            // Prefer java.exe so stdout/stderr can be captured. javaw.exe is for GUI launches.
            var java = Path.Combine(runtimeDir, "bin", "java.exe");
            if (File.Exists(java)) return java;
            var javaw = Path.Combine(runtimeDir, "bin", "javaw.exe");
            if (File.Exists(javaw)) return javaw;
        }
        else
        {
            string[] candidates =
            [
                Path.Combine(runtimeDir, "bin", "java"),
                Path.Combine(runtimeDir, "Contents", "Home", "bin", "java"),
                Path.Combine(runtimeDir, "jre.bundle", "Contents", "Home", "bin", "java"),
            ];
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
        }
        throw new InvalidOperationException($"Could not locate Java executable in {runtimeDir}");
    }
}
