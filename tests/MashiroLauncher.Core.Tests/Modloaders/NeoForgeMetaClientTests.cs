using MashiroLauncher.Core.Modloaders.NeoForge;

namespace MashiroLauncher.Core.Tests.Modloaders;

public class NeoForgeMetaClientTests
{
    [Theory]
    [InlineData("1.21.5", "21.5.")]
    [InlineData("1.20.4", "20.4.")]
    [InlineData("1.21",   "21.0.")]   // patch defaults to 0 for "1.21" style
    public void Prefix_MatchesNeoForgeNaming(string mcVersion, string expected)
    {
        Assert.Equal(expected, NeoForgeMetaClient.NeoForgePrefixFor(mcVersion));
    }

    [Fact]
    public void Prefix_Rejects_1_20_1()
    {
        // NeoForge never published builds for 1.20.1 — that line stayed on
        // upstream Forge. Make sure we give the user a clear message.
        var ex = Assert.Throws<NeoForgeMetaException>(
            () => NeoForgeMetaClient.NeoForgePrefixFor("1.20.1"));
        Assert.Contains("Forge", ex.Message);
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("2.0.0")]   // hypothetical post-1.x MC
    [InlineData("")]
    public void Prefix_RejectsNon1xVersions(string bad)
    {
        Assert.Throws<NeoForgeMetaException>(() => NeoForgeMetaClient.NeoForgePrefixFor(bad));
    }
}
