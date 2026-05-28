using System.Text.RegularExpressions;

namespace MashiroLauncher.Core.Versions.Rules;

public sealed record OsConstraint(string? Name, string? Version, string? Arch);

public sealed record Rule(
    string Action,
    OsConstraint? Os = null,
    IReadOnlyDictionary<string, bool>? Features = null);

public static class RuleEvaluator
{
    public static bool Evaluate(IReadOnlyList<Rule>? rules, RuleContext ctx)
    {
        if (rules is null || rules.Count == 0) return true;
        var allowed = false;
        foreach (var rule in rules)
            if (Matches(rule, ctx))
                allowed = rule.Action == "allow";
        return allowed;
    }

    private static bool Matches(Rule rule, RuleContext ctx)
    {
        if (rule.Os is { } os)
        {
            if (os.Name is not null && !OsNameMatches(os.Name, ctx.Os)) return false;
            if (os.Version is not null && !Regex.IsMatch(ctx.OsVersion, os.Version)) return false;
            if (os.Arch is not null && !OsArchMatches(os.Arch, ctx.Arch)) return false;
        }
        if (rule.Features is { Count: > 0 } features)
        {
            foreach (var (name, expected) in features)
                if (HasFeature(name, ctx.Features) != expected) return false;
        }
        return true;
    }

    private static bool OsNameMatches(string mojangName, OsName ctx) => mojangName switch
    {
        "windows" => ctx == OsName.Windows,
        "linux"   => ctx == OsName.Linux,
        "osx" or "macos" or "mac-os" => ctx == OsName.Osx,
        _ => false,
    };

    private static bool OsArchMatches(string mojangArch, OsArch ctx) => mojangArch switch
    {
        "x86"                            => ctx == OsArch.X86,
        "x86_64" or "x64" or "amd64"     => ctx == OsArch.X64,
        "arm64" or "aarch64"             => ctx == OsArch.Arm64,
        _ => false,
    };

    private static bool HasFeature(string name, LaunchFeatures features) => name switch
    {
        "is_demo_user"                  => features.HasFlag(LaunchFeatures.IsDemoUser),
        "has_custom_resolution"         => features.HasFlag(LaunchFeatures.HasCustomResolution),
        "has_quick_plays_support"       => features.HasFlag(LaunchFeatures.HasQuickPlaysSupport),
        "has_quick_plays_singleplayer"  => features.HasFlag(LaunchFeatures.HasQuickPlaysSingleplayer),
        "has_quick_plays_multiplayer"   => features.HasFlag(LaunchFeatures.HasQuickPlaysMultiplayer),
        "has_quick_plays_realms"        => features.HasFlag(LaunchFeatures.HasQuickPlaysRealms),
        _ => false,
    };
}
