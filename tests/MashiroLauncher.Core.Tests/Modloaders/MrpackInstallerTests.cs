using System.IO.Compression;
using System.Reflection;
using System.Text;
using MashiroLauncher.Core.Modloaders.Mrpack;

namespace MashiroLauncher.Core.Tests.Modloaders;

/// <summary>
/// MrpackInstaller's networked import path needs HTTP, but the safety-critical
/// path-validation logic is pure and worth pinning down.
/// </summary>
public class MrpackInstallerTests
{
    // IsSafeRelativePath is private; reflection avoids exposing it just for tests.
    private static readonly MethodInfo IsSafe = typeof(MrpackInstaller)
        .GetMethod("IsSafeRelativePath", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static bool SafePath(string p) => (bool)IsSafe.Invoke(null, [p])!;

    [Theory]
    [InlineData("mods/sodium.jar", true)]
    [InlineData("config/iris.properties", true)]
    [InlineData("resourcepacks/pack.zip", true)]
    [InlineData("a/b/c/d.txt", true)]
    public void IsSafeRelativePath_AcceptsNormalRelativePaths(string path, bool expected)
    {
        Assert.Equal(expected, SafePath(path));
    }

    [Theory]
    [InlineData("../../Windows/System32/calc.exe")]   // traversal up
    [InlineData("mods/../etc/passwd")]                 // traversal mid-path
    [InlineData("/etc/passwd")]                        // absolute unix
    [InlineData("C:\\Windows\\System32\\calc.exe")]    // absolute Windows
    [InlineData("..")]
    [InlineData("")]
    public void IsSafeRelativePath_RejectsTraversalAndAbsolute(string path)
    {
        Assert.False(SafePath(path));
    }

    [Fact]
    public async Task ImportAsync_ThrowsWhenManifestMissing()
    {
        var tempZip = Path.Combine(Path.GetTempPath(), $"mash-mrpack-{Guid.NewGuid():N}.mrpack");
        try
        {
            // Build a "mrpack" zip with no modrinth.index.json — should be rejected.
            using (var fs = File.Create(tempZip))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("README.txt");
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write("not actually a mrpack");
            }

            using var http = new HttpClient();
            var installer = new MrpackInstaller(http);
            var ex = await Assert.ThrowsAsync<MrpackException>(
                () => installer.ImportAsync(tempZip, "unused", null, default));
            Assert.Contains("modrinth.index.json", ex.Message);
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
        }
    }

    [Fact]
    public async Task ImportAsync_ThrowsWhenFileMissing()
    {
        using var http = new HttpClient();
        var installer = new MrpackInstaller(http);
        var ex = await Assert.ThrowsAsync<MrpackException>(
            () => installer.ImportAsync("Z:/does-not-exist.mrpack", "unused", null, default));
        Assert.Contains("존재하지", ex.Message);
    }
}
