using System.Runtime.InteropServices;

namespace Launcher.Core.Versions.Rules;

public enum OsName { Windows, Linux, Osx }

public enum OsArch { X86, X64, Arm64 }

[Flags]
public enum LaunchFeatures
{
    None                       = 0,
    IsDemoUser                 = 1 << 0,
    HasCustomResolution        = 1 << 1,
    HasQuickPlaysSupport       = 1 << 2,
    HasQuickPlaysSingleplayer  = 1 << 3,
    HasQuickPlaysMultiplayer   = 1 << 4,
    HasQuickPlaysRealms        = 1 << 5,
}

public sealed record RuleContext(
    OsName Os,
    string OsVersion,
    OsArch Arch,
    LaunchFeatures Features)
{
    public static RuleContext Detect(LaunchFeatures features = LaunchFeatures.None)
    {
        var os = OperatingSystem.IsWindows() ? OsName.Windows
               : OperatingSystem.IsLinux()   ? OsName.Linux
               : OperatingSystem.IsMacOS()   ? OsName.Osx
               : throw new PlatformNotSupportedException(
                   $"Unsupported OS: {RuntimeInformation.OSDescription}");

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64   => OsArch.X64,
            Architecture.X86   => OsArch.X86,
            Architecture.Arm64 => OsArch.Arm64,
            var other => throw new PlatformNotSupportedException($"Unsupported arch: {other}")
        };

        return new RuleContext(os, Environment.OSVersion.Version.ToString(), arch, features);
    }
}
