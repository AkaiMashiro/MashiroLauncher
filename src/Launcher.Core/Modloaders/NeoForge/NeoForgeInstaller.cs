using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Launcher.Core.Common;
using Launcher.Core.Versions.Mojang;

namespace Launcher.Core.Modloaders.NeoForge;

public sealed record NeoForgeInstallResult(string MergedVersionId, NeoForgeProfile Profile);

public class NeoForgeInstallException(string message) : Exception(message);

/// <summary>
/// Downloads NeoForge's installer JAR for a given <c>(mcVersion, neoforgeVersion)</c>
/// pair and runs it headlessly against our <c>data/</c> root.
///
/// The installer is a Java program that:
///   1. Reads <c>data/versions/{mcVersion}/{mcVersion}.jar</c> (vanilla client jar).
///   2. Writes <c>data/versions/neoforge-{version}/neoforge-{version}.json</c>
///      (the overlay profile we hand back to <see cref="ModloaderOverlayMerger"/>).
///   3. Drops NeoForge's runtime libraries into <c>data/libraries/</c>.
///   4. May patch a few vanilla libraries in-place — that's fine for our usage
///      because we always launch with the merged classpath anyway.
///
/// We launch with <c>-Djava.awt.headless=true</c> so the installer's Swing GUI
/// cannot try to open a window on machines without a display, and capture
/// stdout/stderr to <c>data/logs/neoforge-installer.log</c> so failures are
/// debuggable.
/// </summary>
public sealed class NeoForgeInstaller(Downloader downloader)
{
    private const string MavenBase = "https://maven.neoforged.net/releases/net/neoforged/neoforge";

    public async Task<NeoForgeInstallResult> InstallAsync(
        string mcVersion,
        string neoforgeVersion,
        string javaExecutable,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // Pre-flight: vanilla client jar must already be on disk. InstallService
        // calls this after InstallVanillaAsync so the file is guaranteed, but
        // we double-check to fail loud + early.
        var vanillaJar = Paths.VersionJar(mcVersion);
        if (!File.Exists(vanillaJar))
            throw new NeoForgeInstallException(
                $"Vanilla {mcVersion} client jar이 먼저 설치되어 있어야 합니다: {vanillaJar}");

        var mergedId = MergedVersionId(neoforgeVersion);
        var resultJson = Path.Combine(Paths.VersionDir(mergedId), $"{mergedId}.json");

        // Already installed (e.g. the user launched the same instance twice) — skip
        // the network + JVM round-trip and just re-read what's on disk.
        if (File.Exists(resultJson))
        {
            progress?.Report($"NeoForge {neoforgeVersion} 캐시 사용");
            return new NeoForgeInstallResult(mergedId, ReadProfile(resultJson));
        }

        var installerDir = Path.Combine(Paths.Data, "neoforge-installers");
        Directory.CreateDirectory(installerDir);
        var installerJar = Path.Combine(installerDir, $"neoforge-{neoforgeVersion}-installer.jar");

        // Step 1: fetch the installer JAR from NeoForge's Maven.
        progress?.Report($"NeoForge {neoforgeVersion} installer 다운로드 중");
        var installerUrl = new Uri(
            $"{MavenBase}/{neoforgeVersion}/neoforge-{neoforgeVersion}-installer.jar");
        await downloader.DownloadToFileAsync(installerUrl, installerJar, null, null, ct);

        // Step 2: the installer wants a launcher_profiles.json next to the data
        // root. An empty stub satisfies it without polluting state.
        EnsureLauncherProfilesStub();

        // Step 3: run the installer.
        progress?.Report($"NeoForge {neoforgeVersion} 설치 중");
        await RunInstallerAsync(installerJar, javaExecutable, ct);

        // Step 4: validate. If the installer didn't produce our expected JSON
        // path it either failed silently or NeoForge changed their layout.
        if (!File.Exists(resultJson))
            throw new NeoForgeInstallException(
                $"NeoForge installer가 끝났지만 profile JSON이 만들어지지 않았습니다: {resultJson}");

        return new NeoForgeInstallResult(mergedId, ReadProfile(resultJson));
    }

    /// <summary>The merged version id produced by NeoForge: e.g. "neoforge-21.5.95".</summary>
    public static string MergedVersionId(string neoforgeVersion) => $"neoforge-{neoforgeVersion}";

    private static NeoForgeProfile ReadProfile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<NeoForgeProfile>(json, JsonOptions.Default)
                   ?? throw new NeoForgeInstallException($"NeoForge profile JSON 파싱 실패: {path}");
        }
        catch (JsonException ex)
        {
            throw new NeoForgeInstallException($"NeoForge profile JSON 파싱 실패: {ex.Message}");
        }
    }

    private static void EnsureLauncherProfilesStub()
    {
        var path = Path.Combine(Paths.Data, "launcher_profiles.json");
        if (File.Exists(path)) return;
        const string stub = """
            {"profiles":{},"settings":{},"version":3}
            """;
        File.WriteAllText(path, stub);
    }

    private static async Task RunInstallerAsync(
        string installerJar, string javaExecutable, CancellationToken ct)
    {
        Directory.CreateDirectory(Paths.Logs);
        var logPath = Path.Combine(Paths.Logs, "neoforge-installer.log");

        var psi = new ProcessStartInfo
        {
            FileName = javaExecutable,
            WorkingDirectory = Paths.Data,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // Headless so Swing can't pop a window on a server / locked desktop.
        psi.ArgumentList.Add("-Djava.awt.headless=true");
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installerJar);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add(Paths.Data);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!process.Start())
            throw new NeoForgeInstallException("NeoForge installer 프로세스를 시작하지 못했습니다.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }
        });

        await process.WaitForExitAsync(ct);

        // Persist the full output regardless of exit code so the user has
        // something to attach to bug reports.
        try
        {
            await File.WriteAllTextAsync(
                logPath,
                $"=== NeoForge installer @ {DateTimeOffset.Now:O} ===\n" +
                $"exit: {process.ExitCode}\n\n" +
                $"--- stdout ---\n{stdout}\n" +
                $"--- stderr ---\n{stderr}\n",
                ct);
        }
        catch { /* logging is best-effort */ }

        if (process.ExitCode != 0)
        {
            var tail = stderr.Length > 0 ? stderr.ToString() : stdout.ToString();
            throw new NeoForgeInstallException(
                $"NeoForge installer 실패 (exit {process.ExitCode}). 자세한 내용: data/logs/neoforge-installer.log\n{Truncate(tail, 500)}");
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
