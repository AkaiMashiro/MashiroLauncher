using System.Reflection;

namespace Launcher.App;

/// <summary>
/// Read-only build identity injected at compile time via AssemblyMetadata.
/// CI sets GitCommit to the actual commit SHA; local builds get "dev".
/// </summary>
public static class BuildInfo
{
    public static string GitCommit { get; } = Read("GitCommit") ?? "dev";
    public static string BuildTime { get; } = Read("BuildTime") ?? "unknown";

    public static bool IsDev => GitCommit == "dev";

    /// <summary>First 7 chars of the SHA — what's shown to humans.</summary>
    public static string ShortSha => GitCommit.Length >= 7 ? GitCommit[..7] : GitCommit;

    private static string? Read(string key)
    {
        var asm = typeof(BuildInfo).Assembly;
        foreach (var attr in asm.GetCustomAttributes<AssemblyMetadataAttribute>())
            if (attr.Key == key) return attr.Value;
        return null;
    }
}
