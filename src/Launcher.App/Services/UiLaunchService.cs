using Launcher.Core.Auth;
using Launcher.Core.Common;
using Launcher.Core.Launching;

namespace Launcher.App.Services;

public sealed class UiLaunchService(Downloader downloader)
{
    public Task<LaunchPlan> PrepareAsync(
        string versionId, IAccount account, Modloader modloader, string? instanceName,
        JvmOverrides? jvmOverrides, CancellationToken ct)
    {
        var pipeline = new LaunchPipeline(downloader);
        return pipeline.PrepareAsync(
            versionId, account, modloader, instanceName,
            jvmOverrides: jvmOverrides, ct: ct);
    }

    public Task<int> StartAsync(LaunchPlan plan, CancellationToken ct)
    {
        var launcher = new ProcessLauncher();
        return launcher.LaunchAsync(plan, ct);
    }
}
