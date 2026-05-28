using MashiroLauncher.Core.Auth;
using MashiroLauncher.Core.Common;
using MashiroLauncher.Core.Installation;
using MashiroLauncher.Core.Java;
using MashiroLauncher.Core.Versions.Rules;

namespace MashiroLauncher.Core.Launching;

public enum Modloader { Vanilla, Fabric, NeoForge }

/// <summary>
/// Per-launch overrides for the JVM tuning that normally comes from the
/// global Settings → 고급 panel. Any null field falls back to the global
/// value. Lives in the Launching namespace (not Instances) so ArgumentBuilder
/// doesn't have to take a dependency on the Instances layer.
/// </summary>
public sealed record JvmOverrides(int? MinMemoryMb, int? MaxMemoryMb, string? CustomJvmArgs);

public sealed class LaunchPipeline(Downloader downloader)
{
    public async Task<LaunchPlan> PrepareAsync(
        string versionId,
        IAccount account,
        Modloader modloader = Modloader.Vanilla,
        string? instanceName = null,
        LaunchFeatures features = LaunchFeatures.None,
        JvmOverrides? jvmOverrides = null,
        CancellationToken ct = default)
    {
        var installSvc = new InstallService(downloader);

        // Vanilla first — both modloaders inherit from this layer (client jar,
        // assets, javaVersion are all carried over via the merger).
        var vanilla = await installSvc.InstallVanillaAsync(versionId, ct);

        // JRE up front, before applying NeoForge, because NeoForge's installer
        // is itself a Java program that we run with this same executable.
        var jre = new JreInstaller(downloader);
        var rctx = RuleContext.Detect(features);
        var javaExe = await jre.InstallAsync(
            vanilla.VersionJson.JavaVersion.Component, rctx.Os, rctx.Arch, ct);

        var install = modloader switch
        {
            Modloader.Fabric   => await installSvc.ApplyFabricAsync(vanilla, ct),
            Modloader.NeoForge => await installSvc.ApplyNeoForgeAsync(vanilla, javaExe, ct),
            _                  => vanilla,
        };

        // Default instance dir name. Suffix keeps the mods/ folder separate
        // from a vanilla install of the same MC version.
        var defaultName = modloader switch
        {
            Modloader.Fabric   => $"{versionId}-fabric",
            Modloader.NeoForge => $"{versionId}-neoforge",
            _                  => versionId,
        };
        var name = string.IsNullOrWhiteSpace(instanceName) ? defaultName : instanceName;
        var gameDir = Paths.InstanceGameDir(name);

        var globalSettings = new SettingsStorage().Load();
        // Fold any per-instance overrides on top of the global defaults.
        var effectiveSettings = new LauncherSettings(
            MinMemoryMb:   jvmOverrides?.MinMemoryMb   ?? globalSettings.MinMemoryMb,
            MaxMemoryMb:   jvmOverrides?.MaxMemoryMb   ?? globalSettings.MaxMemoryMb,
            CustomJvmArgs: jvmOverrides?.CustomJvmArgs ?? globalSettings.CustomJvmArgs,
            UseInstanceMode: globalSettings.UseInstanceMode);

        var builder = new ArgumentBuilder();
        return builder.Build(
            install.VersionJson, account, javaExe, gameDir, install.LoggingConfigPath, features, effectiveSettings);
    }
}
