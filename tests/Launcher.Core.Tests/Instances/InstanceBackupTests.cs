using Launcher.Core.Instances;

namespace Launcher.Core.Tests.Instances;

public class InstanceBackupTests
{
    [Theory]
    [InlineData("game/options.txt", true)]
    [InlineData("game/mods/sodium.jar", true)]
    [InlineData("game/saves/world/level.dat", true)]
    public void IsSafeRelativePath_AcceptsTypicalPaths(string path, bool expected)
    {
        Assert.Equal(expected, InstanceBackup.IsSafeRelativePath(path));
    }

    [Theory]
    [InlineData("../../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("game/../../etc/passwd")]
    [InlineData("C:\\Windows\\System32\\bad.exe")]
    [InlineData("")]
    public void IsSafeRelativePath_RejectsTraversalAndAbsolute(string path)
    {
        Assert.False(InstanceBackup.IsSafeRelativePath(path));
    }
}
