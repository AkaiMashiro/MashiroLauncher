using System.Diagnostics;
using System.Text.Json;
using MashiroLauncher.Core.Common;

namespace MashiroLauncher.Core.Bedrock;

public sealed record BedrockInstallInfo(string Name, string Version, string PackageFamilyName);

public class BedrockLaunchException(string message) : Exception(message);

public sealed class BedrockClient
{
    private const string PackageName = "Microsoft.MinecraftUWP";
    private const string AppUserModelId = "Microsoft.MinecraftUWP_8wekyb3d8bbwe!App";

    // Returns info about the installed Bedrock package, or null if not installed.
    // Uses powershell.exe + Get-AppxPackage; works without any UWP SDK reference.
    public async Task<BedrockInstallInfo?> DetectAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"Get-AppxPackage -Name {PackageName} | Select-Object -Property Name,Version,PackageFamilyName | ConvertTo-Json -Compress\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var trimmed = output.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            var info = JsonSerializer.Deserialize<RawInfo>(trimmed, JsonOptions.Default);
            if (info?.Name is null) return null;
            return new BedrockInstallInfo(info.Name, info.Version ?? "?", info.PackageFamilyName ?? "");
        }
        catch
        {
            return null;
        }
    }

    public void Launch()
    {
        if (!OperatingSystem.IsWindows())
            throw new BedrockLaunchException("Bedrock 실행은 Windows에서만 지원됩니다.");

        try
        {
            // Activate the UWP app via the shell:AppsFolder namespace.
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{AppUserModelId}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            throw new BedrockLaunchException($"Bedrock 실행 실패: {ex.Message}");
        }
    }

    private sealed record RawInfo(string? Name, string? Version, string? PackageFamilyName);
}
