using MashiroLauncher.Core.Versions.Rules;

namespace MashiroLauncher.Core.Tests.Versions.Rules;

public class RuleEvaluatorTests
{
    private static readonly RuleContext Win11   = new(OsName.Windows, "10.0.26200", OsArch.X64, LaunchFeatures.None);
    private static readonly RuleContext WinRtm  = new(OsName.Windows, "10.0.10240", OsArch.X64, LaunchFeatures.None);
    private static readonly RuleContext Linux64 = new(OsName.Linux,   "5.15.0",     OsArch.X64, LaunchFeatures.None);
    private static readonly RuleContext Osx     = new(OsName.Osx,     "14.5",       OsArch.X64, LaunchFeatures.None);

    [Fact]
    public void NullRules_Allow()
    {
        Assert.True(RuleEvaluator.Evaluate(null, Win11));
    }

    [Fact]
    public void EmptyRules_Allow()
    {
        Assert.True(RuleEvaluator.Evaluate([], Win11));
    }

    [Fact]
    public void AllowOnly_Allows()
    {
        Rule[] rules = [new Rule("allow")];
        Assert.True(RuleEvaluator.Evaluate(rules, Win11));
    }

    [Fact]
    public void AllowThenDisallowOsx_DeniesOsx_AllowsOthers()
    {
        Rule[] rules =
        [
            new Rule("allow"),
            new Rule("disallow", new OsConstraint("osx", null, null)),
        ];
        Assert.True(RuleEvaluator.Evaluate(rules, Win11));
        Assert.True(RuleEvaluator.Evaluate(rules, Linux64));
        Assert.False(RuleEvaluator.Evaluate(rules, Osx));
    }

    // 26.1.2의 ZGC 룰을 흉내 — Windows 10.0.17134 이상이어야 매치
    [Fact]
    public void OsVersionRegex_MatchesModernWindowsOnly()
    {
        Rule[] rules =
        [
            new Rule("allow", new OsConstraint("windows", @"^10\.0\.(17134|[2-9]\d{4,})", null))
        ];
        Assert.True(RuleEvaluator.Evaluate(rules, Win11));
        Assert.False(RuleEvaluator.Evaluate(rules, WinRtm));
    }

    [Fact]
    public void DefaultDeny_WhenAllRulesNonMatching()
    {
        Rule[] rules =
        [
            new Rule("allow", new OsConstraint("osx", null, null))
        ];
        Assert.False(RuleEvaluator.Evaluate(rules, Win11));
        Assert.True(RuleEvaluator.Evaluate(rules, Osx));
    }

    [Fact]
    public void Features_AllowWhenSet()
    {
        Rule[] rules =
        [
            new Rule("allow", Features: new Dictionary<string, bool> { ["is_demo_user"] = true })
        ];
        var demoCtx = Win11 with { Features = LaunchFeatures.IsDemoUser };
        Assert.True(RuleEvaluator.Evaluate(rules, demoCtx));
        Assert.False(RuleEvaluator.Evaluate(rules, Win11));
    }

    [Fact]
    public void Features_DenyWhenExpectedFalseButSet()
    {
        Rule[] rules =
        [
            new Rule("allow", Features: new Dictionary<string, bool> { ["is_demo_user"] = false })
        ];
        var demoCtx = Win11 with { Features = LaunchFeatures.IsDemoUser };
        Assert.False(RuleEvaluator.Evaluate(rules, demoCtx));
        Assert.True(RuleEvaluator.Evaluate(rules, Win11));
    }

    [Fact]
    public void LastMatchWins()
    {
        Rule[] rules =
        [
            new Rule("allow"),
            new Rule("disallow", new OsConstraint("windows", null, null)),
            new Rule("allow",    new OsConstraint("windows", null, null)),
        ];
        Assert.True(RuleEvaluator.Evaluate(rules, Win11));
    }

    [Fact]
    public void Arch_FiltersByArchitecture()
    {
        Rule[] rules =
        [
            new Rule("allow", new OsConstraint(null, null, "x86"))
        ];
        var x86Ctx = Win11 with { Arch = OsArch.X86 };
        Assert.True(RuleEvaluator.Evaluate(rules, x86Ctx));
        Assert.False(RuleEvaluator.Evaluate(rules, Win11));
    }

    [Theory]
    [InlineData("osx")]
    [InlineData("macos")]
    [InlineData("mac-os")]
    public void OsName_OsxAliases_AllRecognized(string mojangName)
    {
        Rule[] rules = [new Rule("allow", new OsConstraint(mojangName, null, null))];
        Assert.True(RuleEvaluator.Evaluate(rules, Osx));
        Assert.False(RuleEvaluator.Evaluate(rules, Win11));
    }
}
