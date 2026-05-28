using Launcher.Core.Launching;

namespace Launcher.Core.Tests.Launching;

public class ArgumentBuilderTests
{
    [Fact]
    public void Substitute_ReplacesKnownTokens()
    {
        var subs = new Dictionary<string, string>
        {
            ["auth_player_name"] = "Steve",
            ["version_name"] = "26.1.2",
        };
        var result = ArgumentBuilder.Substitute("--username ${auth_player_name} --version ${version_name}", subs);
        Assert.Equal("--username Steve --version 26.1.2", result);
    }

    [Fact]
    public void Substitute_LeavesUnknownTokenAsLiteral()
    {
        var subs = new Dictionary<string, string>();
        var result = ArgumentBuilder.Substitute("--mystery ${unknown_token}", subs);
        Assert.Equal("--mystery ${unknown_token}", result);
    }

    [Fact]
    public void Substitute_HandlesMultipleOccurrences()
    {
        var subs = new Dictionary<string, string> { ["x"] = "Y" };
        var result = ArgumentBuilder.Substitute("${x}-${x}-${x}", subs);
        Assert.Equal("Y-Y-Y", result);
    }

    [Fact]
    public void Substitute_PreservesEmbeddedSpaces()
    {
        // Fabric의 -DFabricMcEmu= net.minecraft.client.main.Main 같은 케이스에서
        // 의도된 공백이 그대로 살아남아야 함
        var subs = new Dictionary<string, string>();
        var raw = "-DFabricMcEmu= net.minecraft.client.main.Main ";
        var result = ArgumentBuilder.Substitute(raw, subs);
        Assert.Equal(raw, result);
    }
}
