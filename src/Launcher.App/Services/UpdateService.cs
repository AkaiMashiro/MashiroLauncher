using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Core.Common;

namespace Launcher.App.Services;

public sealed record UpdateInfo(
    string ShortSha,
    string DownloadUrl,
    string Notes,
    DateTimeOffset? PublishedAt);

/// <summary>
/// Checks GitHub Releases for the latest build. Used in two places only:
///   1. <see cref="MainWindowViewModel.InitializeAsync"/> — single check ~30s after app start.
///   2. The Settings → 업데이트 → "지금 확인" button.
/// Background polling was removed in favor of those two entry points so the
/// launcher doesn't make API calls (or surface notifications) the user didn't
/// ask for.
///
/// Dismissed SHAs are remembered on disk so the user isn't re-prompted for the
/// same build across restarts.
/// </summary>
public sealed class UpdateService(HttpClient http)
{
    // The single source of truth for releases. Public repo — no auth header needed.
    private const string ReleasesLatestUrl =
        "https://api.github.com/repos/AkaiMashiro/MashiroLauncher/releases/latest";

    private const string TagPrefix = "build-";

    public event EventHandler<UpdateInfo>? UpdateAvailable;

    public async Task CheckOnceAsync(CancellationToken ct)
    {
        // Dev builds never auto-update — they're whatever the developer has locally.
        if (BuildInfo.IsDev) return;

        using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesLatestUrl);
        req.Headers.UserAgent.ParseAdd("MashiroLauncher/0.1");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return;

        var body = await resp.Content.ReadAsStringAsync(ct);
        var release = JsonSerializer.Deserialize<ReleaseResponse>(body, JsonOptions.Default);
        if (release?.TagName is null) return;

        var tagShort = release.TagName.StartsWith(TagPrefix, StringComparison.Ordinal)
            ? release.TagName[TagPrefix.Length..]
            : release.TagName;
        if (string.IsNullOrEmpty(tagShort)) return;

        // Same as what we're already running — nothing to do.
        if (string.Equals(tagShort, BuildInfo.ShortSha, StringComparison.OrdinalIgnoreCase))
            return;

        // User has already chosen to skip this exact build.
        if (string.Equals(tagShort, LoadDismissed(), StringComparison.OrdinalIgnoreCase))
            return;

        var asset = release.Assets?.FirstOrDefault(a =>
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (asset is null) return;

        UpdateAvailable?.Invoke(this, new UpdateInfo(
            tagShort, asset.BrowserDownloadUrl, release.Body ?? "", release.PublishedAt));
    }

    // -------------------------------------------------------------------------
    // Dismissed-build memory
    // -------------------------------------------------------------------------

    private static string DismissedPath => Path.Combine(Paths.Data, "update_dismissed.txt");

    public string? LoadDismissed()
    {
        try { return File.Exists(DismissedPath) ? File.ReadAllText(DismissedPath).Trim() : null; }
        catch { return null; }
    }

    public void Dismiss(string shortSha)
    {
        try
        {
            Directory.CreateDirectory(Paths.Data);
            File.WriteAllText(DismissedPath, shortSha);
        }
        catch { /* best-effort; if we can't save, user gets re-prompted, that's OK */ }
    }

    // -------------------------------------------------------------------------
    // Install
    // -------------------------------------------------------------------------

    /// <summary>
    /// Download the new .exe to %TEMP%, write a one-shot PowerShell script that
    /// waits for us to exit then swaps the binary and relaunches, kick off that
    /// script, and exit the current process. Caller should not assume control
    /// returns — the process is about to die.
    /// </summary>
    public async Task DownloadAndInstallAsync(
        UpdateInfo info,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var tempExe = Path.Combine(Path.GetTempPath(), $"Launcher.App.{info.ShortSha}.exe");
        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("현재 실행 파일 경로를 가져오지 못했습니다.");

        // 1. Download
        using (var resp = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1L;
            await using var fs = File.Create(tempExe);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);

            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await stream.ReadAsync(buf, ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }

        // 2. Write the one-shot updater script
        var scriptPath = Path.Combine(Path.GetTempPath(), $"mashiro_update_{info.ShortSha}.ps1");
        var script = $$"""
            Start-Sleep -Seconds 2
            $src = '{{tempExe.Replace("'", "''")}}'
            $dst = '{{currentExe.Replace("'", "''")}}'
            $attempts = 0
            while ($attempts -lt 15) {
                try {
                    Copy-Item -LiteralPath $src -Destination $dst -Force -ErrorAction Stop
                    break
                } catch {
                    Start-Sleep -Seconds 1
                    $attempts++
                }
            }
            if ($attempts -ge 15) { exit 1 }
            Start-Process -FilePath $dst
            Remove-Item -LiteralPath $src -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $PSCommandPath -ErrorAction SilentlyContinue
            """;
        await File.WriteAllTextAsync(scriptPath, script, ct);

        // 3. Launch the script in a hidden PowerShell. We MUST verify it
        //    actually started before exiting ourselves — otherwise on a system
        //    without powershell.exe in PATH (or with restrictive policy) we
        //    would kill the launcher and the update would never run.
        var psProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (psProcess is null || psProcess.HasExited && psProcess.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "업데이트 스크립트 실행 실패 (PowerShell 사용 불가). 새 .exe는 임시 폴더에 다운로드되어 있습니다.");
        }

        // Give the script's Start-Sleep a moment to begin before we exit.
        await Task.Delay(300, ct);
        Environment.Exit(0);
    }

    // -------------------------------------------------------------------------
    // GitHub Releases JSON shape (only fields we care about)
    // -------------------------------------------------------------------------

    private sealed record ReleaseResponse(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("assets")] List<ReleaseAsset>? Assets);

    private sealed record ReleaseAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);
}
