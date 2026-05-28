using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Launcher.Core.Common;
using StoreLib.Models;
using StoreLib.Services;

namespace Launcher.Core.Bedrock;

public sealed record BedrockInstallProgress(string Stage, int Step, int TotalSteps);

public class BedrockInstallException(string message) : Exception(message);

public sealed class BedrockInstaller(Downloader downloader)
{
    // Microsoft Store product ID for "Minecraft for Windows" (Bedrock Edition).
    private const string ProductId = "9NBLGGH2JHXJ";

    public async Task InstallLatestAsync(
        IProgress<BedrockInstallProgress>? progress,
        CancellationToken ct = default)
    {
        progress?.Report(new("Microsoft Store 조회 중", 0, 1));

        var dcat = new DisplayCatalogHandler(DCatEndpoint.Production, new Locale(Market.US, Lang.en, true));
        await dcat.QueryDCATAsync(ProductId);
        if (dcat.Result != DisplayCatalogResult.Found)
            throw new BedrockInstallException($"Microsoft Store에서 제품을 찾지 못했습니다 (Result={dcat.Result}).");

        IList<PackageInstance> packages;
        try
        {
            packages = await dcat.GetPackagesForProductAsync();
        }
        catch (Exception ex)
        {
            throw new BedrockInstallException($"패키지 메타데이터 조회 실패: {ex.Message}");
        }
        if (packages.Count == 0)
            throw new BedrockInstallException("Store가 받을 수 있는 패키지를 반환하지 않았습니다.");

        // StoreLib's PackageMoniker is unreliable for Bedrock (architecture in the name
        // does not match the actual content). Strategy: download each unique URI,
        // then read AppxManifest.xml to determine the real identity.
        var uniqueByUri = packages
            .GroupBy(p => p.PackageUri.AbsoluteUri)
            .Select(g => g.First())
            .ToList();

        var downloadDir = Path.Combine(Paths.Data, "bedrock-install");
        if (Directory.Exists(downloadDir)) Directory.Delete(downloadDir, recursive: true);
        Directory.CreateDirectory(downloadDir);

        var entries = new List<(PackageIdentity Identity, string Path)>();
        var totalSteps = uniqueByUri.Count + 1;
        for (var i = 0; i < uniqueByUri.Count; i++)
        {
            var pkg = uniqueByUri[i];
            progress?.Report(new($"다운로드 ({i + 1}/{uniqueByUri.Count})", i, totalSteps));
            var tempPath = Path.Combine(downloadDir, $"pkg-{i}.appx");
            try
            {
                await downloader.DownloadToFileAsync(pkg.PackageUri, tempPath, null, null, ct);
            }
            catch (Exception ex)
            {
                throw new BedrockInstallException($"다운로드 실패 ({pkg.PackageMoniker}): {ex.Message}");
            }

            var id = TryReadIdentity(tempPath);
            if (id is null)
            {
                File.Delete(tempPath);
                continue;
            }
            entries.Add((id, tempPath));
        }

        // Filter to packages compatible with current architecture.
        // Bedrock ships as x86 only — x64 hosts can run x86 packages just fine.
        var osArch = RuntimeInformation.OSArchitecture;
        bool compatible(string archName) =>
            string.Equals(archName, "neutral", StringComparison.OrdinalIgnoreCase)
            || (osArch == Architecture.Arm64 && archName.Equals("arm64", StringComparison.OrdinalIgnoreCase))
            || (osArch == Architecture.X64 && (archName.Equals("x64", StringComparison.OrdinalIgnoreCase) || archName.Equals("x86", StringComparison.OrdinalIgnoreCase)))
            || (osArch == Architecture.X86 && archName.Equals("x86", StringComparison.OrdinalIgnoreCase));

        var compat = entries.Where(e => compatible(e.Identity.Architecture)).ToList();
        if (compat.Count == 0)
            throw new BedrockInstallException($"{osArch}에 호환되는 패키지가 없습니다.");

        // Dedupe by package name — keep the highest version of each.
        var picked = compat
            .GroupBy(e => e.Identity.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(e => ParseVersion(e.Identity.Version))
                .First())
            .ToList();

        var main = picked.FirstOrDefault(e =>
            e.Identity.Name.Equals("Microsoft.MinecraftUWP", StringComparison.OrdinalIgnoreCase));
        if (main == default)
            throw new BedrockInstallException("Microsoft.MinecraftUWP 패키지를 식별하지 못했습니다.");
        var deps = picked.Where(e => e.Path != main.Path).Select(e => e.Path).ToList();

        progress?.Report(new("패키지 등록 중 (Add-AppxPackage)", uniqueByUri.Count, totalSteps));
        await InstallViaPowerShellAsync(main.Path, deps, ct);

        try { Directory.Delete(downloadDir, recursive: true); } catch { /* best-effort */ }
    }

    private sealed record PackageIdentity(string Name, string Version, string Architecture);

    private static PackageIdentity? TryReadIdentity(string appxPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(appxPath);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase)
                || e.FullName.Equals("AppxMetadata/AppxBundleManifest.xml", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;

            using var reader = new StreamReader(entry.Open());
            var xml = reader.ReadToEnd();
            var match = Regex.Match(xml, @"<Identity\s+([^/>]+?)\s*/?>", RegexOptions.IgnoreCase);
            if (!match.Success) return null;

            var attrs = match.Groups[1].Value;
            var name = Regex.Match(attrs, @"Name\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase).Groups[1].Value;
            var version = Regex.Match(attrs, @"Version\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase).Groups[1].Value;
            var arch = Regex.Match(attrs, @"ProcessorArchitecture\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(name)) return null;
            return new PackageIdentity(
                name,
                string.IsNullOrEmpty(version) ? "0.0" : version,
                string.IsNullOrEmpty(arch) ? "neutral" : arch);
        }
        catch
        {
            return null;
        }
    }

    private static Version ParseVersion(string s) =>
        Version.TryParse(s, out var v) ? v : new Version(0, 0);

    private static async Task InstallViaPowerShellAsync(string mainPath, IReadOnlyList<string> depPaths, CancellationToken ct)
    {
        static string Quote(string p) => "'" + p.Replace("'", "''") + "'";

        var depArg = depPaths.Count > 0
            ? $" -DependencyPath @({string.Join(",", depPaths.Select(Quote))})"
            : "";
        // -ForceApplicationShutdown: shared frameworks (VCLibs, GamingServices…) are often
        // in use by other apps (Xbox, iCloud, Widgets). Without this flag Windows refuses
        // to upgrade them. With it, Windows briefly closes the holders and proceeds.
        var command = $"Add-AppxPackage -Path {Quote(mainPath)}{depArg} -ForceApplicationShutdown";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "`\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var p = Process.Start(psi)
            ?? throw new BedrockInstallException("powershell.exe 실행 실패.");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new BedrockInstallException($"Add-AppxPackage 실패 (코드 {p.ExitCode}): {detail.Trim()}");
        }
    }
}
