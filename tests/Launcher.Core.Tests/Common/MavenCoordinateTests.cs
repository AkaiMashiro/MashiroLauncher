using Launcher.Core.Common;

namespace Launcher.Core.Tests.Common;

public class MavenCoordinateTests
{
    [Theory]
    [InlineData("a:b:c", "a", "b", "c", null, "jar")]
    [InlineData("a:b:c:d", "a", "b", "c", "d", "jar")]
    [InlineData("a:b:c@zip", "a", "b", "c", null, "zip")]
    [InlineData("a:b:c:d@zip", "a", "b", "c", "d", "zip")]
    [InlineData("org.ow2.asm:asm:9.9", "org.ow2.asm", "asm", "9.9", null, "jar")]
    [InlineData("com.mojang:jtracy:1.0.37:natives-windows", "com.mojang", "jtracy", "1.0.37", "natives-windows", "jar")]
    [InlineData("net.fabricmc:fabric-loader:0.19.2", "net.fabricmc", "fabric-loader", "0.19.2", null, "jar")]
    public void Parse_ValidSpec_ReturnsParts(
        string spec, string groupId, string artifactId, string version, string? classifier, string extension)
    {
        var c = MavenCoordinate.Parse(spec);
        Assert.Equal(groupId, c.GroupId);
        Assert.Equal(artifactId, c.ArtifactId);
        Assert.Equal(version, c.Version);
        Assert.Equal(classifier, c.Classifier);
        Assert.Equal(extension, c.Extension);
    }

    [Theory]
    [InlineData("a:b")]
    [InlineData("a:b:c:d:e")]
    [InlineData("a::c")]
    [InlineData("a:b:")]
    [InlineData("a:b:c@")]
    [InlineData("")]
    public void Parse_InvalidSpec_Throws(string spec)
    {
        Assert.ThrowsAny<Exception>(() => MavenCoordinate.Parse(spec));
    }

    [Theory]
    [InlineData("org.ow2.asm:asm:9.9", "org/ow2/asm/asm/9.9/asm-9.9.jar")]
    [InlineData("net.fabricmc:fabric-loader:0.19.2", "net/fabricmc/fabric-loader/0.19.2/fabric-loader-0.19.2.jar")]
    [InlineData("com.mojang:jtracy:1.0.37:natives-windows", "com/mojang/jtracy/1.0.37/jtracy-1.0.37-natives-windows.jar")]
    [InlineData("a:b:c@zip", "a/b/c/b-c.zip")]
    [InlineData("a:b:c:d@zip", "a/b/c/b-c-d.zip")]
    public void ToPath_ProducesExpectedRelativePath(string spec, string expected)
    {
        Assert.Equal(expected, MavenCoordinate.Parse(spec).ToPath());
    }
}
